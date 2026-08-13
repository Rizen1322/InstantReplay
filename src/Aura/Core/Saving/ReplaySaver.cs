using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Encoding;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Saving;

/// <summary>
/// Сохранение снимка кольцевых буферов в MP4.
///
/// Видео уже сжато (H264/HEVC/AV1) — SinkWriter работает в passthrough-режиме
/// (input type == output type == сжатый), т.е. это чистый remux: сохранение
/// 5-минутного клипа занимает доли секунды и не грузит GPU/CPU.
/// Аудио — PCM16 48k из микшера, кодируется в AAC самим SinkWriter.
/// Режим дорожек (Mixed/Separate/GameOnly/MicOnly) применяется здесь.
/// </summary>
public static class ReplaySaver
{
    private const int MfESinkHeadersNotFound = unchecked((int)0xC00D4A45);

    /// <summary>
    /// Открепить блоки арены — но только когда писатель отпустит ВСЕ буферы ЭТОГО
    /// сохранения.
    ///
    /// Сэмплы ссылаются прямо на память арены, и Dispose писателя не гарантирует,
    /// что он отпустил их немедленно: в замерах он удерживал 6781 сэмпл из 7067.
    /// Если открепить раньше времени, кольцо перезапишет эти байты, а сборщик может
    /// собрать блок целиком — и Media Foundation обратится к чужой памяти. Именно так
    /// приложение и падало молча через несколько секунд после сохранения.
    ///
    /// Считаем по счётчику СВОЕЙ партии (<see cref="ArenaBufferBatch"/>), а не по
    /// общему на процесс: следующее сохранение начинается раньше, чем заканчивается
    /// это ожидание, и на общем счётчике оно выглядело бы как «писатель ещё держит».
    ///
    /// Ждём в фоне, чтобы не задерживать вызывающего: закрепление лишних блоков на
    /// пару секунд ничего не стоит, а обращение к освобождённой памяти стоит краха.
    /// </summary>
    private static void UnpinWhenWriterDone(
        ArenaBufferBatch batch, List<System.Runtime.InteropServices.GCHandle> handles)
    {
        if (handles.Count == 0) { batch.Free(); return; }

        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (batch.Alive > 0 && clock.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(20);

            if (batch.Alive > 0)
            {
                // Так быть не должно. Блоки оставляем закреплёнными навсегда, а
                // счётчик партии намеренно НЕ освобождаем — живые буферы всё ещё
                // будут его править. Утечка нескольких десятков мегабайт безопаснее
                // обращения к чужой памяти.
                Log.Warn("Saver", $"Писатель не отпустил {batch.Alive} буферов за 30 секунд — " +
                                  "блоки арены остаются закреплёнными");
                return;
            }

            foreach (var handle in handles) handle.Free();
            batch.Free();
            if (clock.ElapsedMilliseconds > 50)
                Log.Info("Saver", $"Писатель отпустил буферы через {clock.ElapsedMilliseconds} мс после закрытия");
        });
    }

    /// <summary>Имя кодека из подтипа медиатипа — для понятных сообщений об ошибке.</summary>
    private static string VideoCodecName(IMFMediaType type)
    {
        try
        {
            Guid sub = type.GetGUID(MediaTypeAttributeKeys.Subtype);
            if (sub == HardwareEncoders.SubtypeFor(VideoCodec.AV1)) return "AV1";
            if (sub == VideoFormatGuids.Hevc) return "HEVC";
            if (sub == VideoFormatGuids.H264) return "H.264";
        }
        catch { }
        return "этот кодек";
    }

    public static void Save(
        string filePath,
        List<EncodedFrame> video,
        AudioSnapshot audio,
        IMFMediaType videoType,
        AudioTrackMode trackMode,
        bool hasGame, bool hasMic,
        Action<double>? progress = null)
    {
        if (video.Count == 0) throw new InvalidOperationException("Видеобуфер пуст");

        // Разбивка по этапам: сохранение иногда заметно затягивается, и по одному
        // суммарному времени не понять, что виновато — открытие файла, запись
        // видео, запись аудио или финализация контейнера.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tOpen, tVideo, tAudio;

        // throttling ОТКЛЮЧЁН. Пробовали включить — фаза записи выросла с 0.6 до 33 с:
        // Media Foundation тормозит вызывающего искусственными паузами, рассчитанными
        // на запись в реальном времени, а мы пишем готовый снимок буфера.
        MfMp4Writer.ResetSampleCounters();
        // Писатель освобождается ЯВНО и строго раньше, чем открепляются блоки арены:
        // сэмплы ссылаются на них напрямую, и пока писатель жив, память трогать нельзя.
        IMFSinkWriter writer = MfMp4Writer.Create(filePath);
        var handles = new List<System.Runtime.InteropServices.GCHandle>();
        // Своя партия буферов на это сохранение — по ней и ждём разгрузки писателя
        var batch = new ArenaBufferBatch();
        try
        {

        int videoStream = MfMp4Writer.AddPassthroughVideoStream(writer, videoType);
        var audioStreams = MfMp4Writer.AddAudioStreams(writer, trackMode, hasGame, hasMic);

        writer.BeginWriting();
        tOpen = sw.ElapsedMilliseconds;

        // Ноль времени клипа = pts первого видеокадра (keyframe)
        long baseTicks = video[0].PtsTicks;
        int frameCount = video.Count;
        double clipSeconds = (video[^1].PtsTicks - baseTicks) / 10_000_000.0;

        // Блоки арены, в которых лежат кадры клипа, закрепляем на время записи:
        // сэмплы будут ссылаться прямо на них, без копирования, а писатель держит
        // сэмплы у себя ещё какое-то время после WriteSample. Блоков единицы —
        // по одному на каждые 64 МБ клипа.
        var pinned = new Dictionary<byte[], IntPtr>(ReferenceEqualityComparer.Instance as IEqualityComparer<byte[]>);
        foreach (var f in video)
        {
            if (pinned.ContainsKey(f.Data)) continue;
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(f.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
            handles.Add(handle);
            pinned[f.Data] = handle.AddrOfPinnedObject();
        }

        // Дорожки пишем ЧЕРЕДУЯ по времени, а не «сначала всё видео, потом всё аудио»:
        // MP4 хранит потоки вперемешку, так писателю не нужно переупорядочивать их самому.
        //
        // Кадры снимка лежат в блоках арены кольцевого буфера и освобождаются все
        // разом, когда движок отпустит список. Раньше здесь массивы возвращались в
        // пул по ходу записи — это лечило нелинейность на гигабайтных клипах
        // (367 МБ за 0.95 с против 950 МБ за 20 с: система уходила в подкачку).
        // С ареной проблемы нет: снимок — это те же блоки, что буфер уже занимал,
        // новой памяти сохранение не просит вовсе.
        // Ограничение темпа подачи (см. WriteDrain) работает только когда писатель
        // отдаёт статистику своей очереди. Фиксированный темп пробовали и отказались:
        // ни 300, ни 180 МБ/с на память не повлияли (прирост нативной части всё равно
        // равен объёму клипа), а сохранение растянулось с 2.6 до 5.5 секунды.
        int blockSamples = audio.Count > 0 ? audio.BlockSamples : 960;
        long totalBytes = 0;
        foreach (var f in video) totalBytes += f.Length;
        totalBytes += (long)audio.Count * blockSamples * sizeof(short) * audioStreams.Count;

        var drain = new WriteDrain(writer, videoStream, totalBytes, progress);

        // Аудио пишем КРУПНЫМИ кусками (~1 с), а не блоками по 10 мс, как они приходят
        // из микшера. Скорость финализации у Media Foundation определяется ЧИСЛОМ
        // сэмплов в контейнере, а не объёмом: на 180-секундном клипе блоки по 10 мс
        // давали 18 080 сэмплов на дорожку (36 160 на две) против 10 849 видеокадров —
        // то есть 77% всех сэмплов были аудио. Секундные куски сокращают их до 180
        // на дорожку. AAC-энкодер сам режет PCM на свои кадры, качество не меняется.
        const int BlocksPerChunk = 100;                       // 100 × 10 мс = 1 секунда
        // Буфер СВОЙ на каждую дорожку: они накапливаются параллельно.
        // Второй буфер (в байтах) переиспользуется вместо ToArray() на каждый кусок:
        // раньше это давало 138 МБ мусора на трёхминутный клип с двумя дорожками.
        var chunkBuf = new short[audioStreams.Count][];
        var chunkBytes = new byte[audioStreams.Count][];
        for (int s = 0; s < audioStreams.Count; s++)
        {
            chunkBuf[s] = new short[BlocksPerChunk * blockSamples];
            chunkBytes[s] = new byte[BlocksPerChunk * blockSamples * sizeof(short)];
        }
        var chunkFill = new int[audioStreams.Count];           // сколько блоков накоплено
        var chunkStart = new long[audioStreams.Count];         // pts первого блока куска

        // Раздельный замер: сколько времени уходит на видео (чистый remux) и сколько
        // на аудио (там внутри WriteSample сидит AAC-кодирование). По этим двум цифрам
        // видно, окупится ли перенос кодирования звука в момент записи.
        long videoTicks = 0, audioTicks = 0;

        // Сбросить накопленный кусок дорожки s одним сэмплом
        void FlushAudio(int s)
        {
            if (chunkFill[s] == 0) return;
            int samples = chunkFill[s] * blockSamples;
            int byteCount = samples * sizeof(short);
            MemoryMarshal.AsBytes<short>(chunkBuf[s].AsSpan(0, samples)).CopyTo(chunkBytes[s]);
            using var sample = MfMp4Writer.CreateSample(
                chunkBytes[s], 0, byteCount, chunkStart[s], chunkFill[s] * 100_000L);
            long audioStart = System.Diagnostics.Stopwatch.GetTimestamp();
            writer.WriteSample(audioStreams[s].Index, sample);
            audioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - audioStart;
            drain.Submitted(byteCount);
            chunkFill[s] = 0;
        }

        var audioPos = new int[audioStreams.Count];
        for (int fi = 0; fi < video.Count; fi++)
        {
            var f = video[fi];
            long vpts = f.PtsTicks - baseTicks;

            // Сначала — всё аудио, которое звучит раньше этого кадра
            for (int s = 0; s < audioStreams.Count; s++)
            {
                var kind = audioStreams[s].Kind;
                while (audioPos[s] < audio.Count)
                {
                    long apts = audio.PtsAt(audioPos[s]) - baseTicks;
                    if (apts > vpts) break;
                    if (apts >= 0)
                    {
                        if (chunkFill[s] == 0) chunkStart[s] = apts;
                        audio.CopyTo(audioPos[s], kind,
                                     chunkBuf[s].AsSpan(chunkFill[s] * blockSamples, blockSamples));
                        if (++chunkFill[s] >= BlocksPerChunk) FlushAudio(s);
                    }
                    audioPos[s]++;
                }
            }

            {
                var sample = MfMp4Writer.CreateSampleNoCopy(
                    batch, pinned[f.Data] + f.Offset, f.Length, vpts, f.DurationTicks);
                if (f.IsKeyframe) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
                long videoStart = System.Diagnostics.Stopwatch.GetTimestamp();
                writer.WriteSample(videoStream, sample);
                videoTicks += System.Diagnostics.Stopwatch.GetTimestamp() - videoStart;
                MfMp4Writer.ReleaseSample(sample);
            }
            drain.Submitted(f.Length);
        }
        tVideo = sw.ElapsedMilliseconds;

        // Хвост аудио после последнего видеокадра
        for (int s = 0; s < audioStreams.Count; s++)
        {
            var kind = audioStreams[s].Kind;
            for (; audioPos[s] < audio.Count; audioPos[s]++)
            {
                long apts = audio.PtsAt(audioPos[s]) - baseTicks;
                if (apts < 0) continue;
                if (chunkFill[s] == 0) chunkStart[s] = apts;
                audio.CopyTo(audioPos[s], kind, chunkBuf[s].AsSpan(chunkFill[s] * blockSamples, blockSamples));
                if (++chunkFill[s] >= BlocksPerChunk) FlushAudio(s);
            }
            FlushAudio(s); // остаток дорожки
        }
        tAudio = sw.ElapsedMilliseconds;

        try
        {
            writer.Finalize();
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.ResultCode.Code == MfESinkHeadersNotFound)
        {
            // Windows не смогла собрать контейнер: у MP4-мультиплексора нет поддержки
            // этого кодека (на Windows 10 так бывает с AV1).
            try { File.Delete(filePath); } catch { }
            Log.Error("Saver", $"MP4 не принял поток {VideoCodecName(videoType)}: {ex.Message}");
            throw new NotSupportedException(
                $"Windows не умеет сохранять {VideoCodecName(videoType)} в MP4 на этой системе. " +
                "Выберите кодек HEVC или H.264 на вкладке «Запись».");
        }
        // Реальный fps клипа = кадры / длительность. Именно он показывает, доехал ли
        // конвейер до заданной частоты: при дропах в очереди энкодера кадров в файле
        // меньше, чем секунд × fps, и запись выглядит рванее, чем настроено.
        string fps = clipSeconds > 0.5 ? $", реально {frameCount / clipSeconds:F1} fps" : "";
        long total = sw.ElapsedMilliseconds;
        long fileBytes = 0;
        try { fileBytes = new FileInfo(filePath).Length; } catch { }

        Log.Info("Saver", $"Сохранено: {filePath} ({frameCount} кадров за {clipSeconds:F1} с{fps}, " +
                          $"{audio.Count} аудиоблоков)");
        Log.Info("Saver", MfMp4Writer.SampleReport);
        Log.Info("Saver", $"Запись файла заняла {total} мс " +
                          $"(открытие {tOpen}, видео {tVideo - tOpen}, аудио {tAudio - tVideo}, " +
                          $"финализация {total - tAudio}); {fileBytes / (1024 * 1024)} МБ, " +
                          $"{(total > 0 ? fileBytes / 1024.0 / 1024 / (total / 1000.0) : 0):F0} МБ/с; " +
                          $"{drain.Report}; " +
                          $"WriteSample: видео {videoTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} мс, " +
                          $"аудио+AAC {audioTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} мс");
        progress?.Invoke(1);
        }
        finally
        {
            writer.Dispose();  // отпускает удержанные сэмплы
            UnpinWhenWriterDone(batch, handles);
        }
    }

    /// <summary>
    /// Темп подачи сэмплов в SinkWriter.
    ///
    /// Тормозим подачу ТОЛЬКО когда писатель реально не успевает — по его собственной
    /// очереди (IMFSinkWriter::GetStatistics, ByteCountQueued). Именно эта очередь
    /// раздувала память на гигабайтных клипах: писатель принимал сэмплы быстрее, чем
    /// сливал на диск, копил их в RAM, а потом полминуты разгребал в Finalize.
    ///
    /// ДВОЕ ГРАБЕЛЬ, на которые тут уже наступили:
    ///
    /// 1. Фиксированный потолок «220 МБ/с». Число угадано под медленный диск, а на
    ///    замерах писатель держит ~870 МБ/с: из 4.3 секунды сохранения 811 МБ
    ///    3.4 секунды были чистым сном пейсера при пустой очереди.
    ///
    /// 2. Обратная связь по РАЗМЕРУ ФАЙЛА (FileStream.Length своим дескриптором).
    ///    Размер растущего файла обновляется рывками и сильно отстаёт от реально
    ///    записанного, поэтому механизм постоянно считал, что диск не успевает:
    ///    клип 317 МБ сохранялся 102 секунды, из них 102.4 с — ожидание на пустом месте.
    ///
    /// 3. Запасная равномерная подача «когда очередь не видна». На практике обёртка
    ///    Vortice статистику как раз не отдаёт, и вместо страховки это стало основным
    ///    режимом: из 3.4 секунды сохранения 626 МБ 2.8 секунды поток просто спал.
    ///
    /// Отсюда правило: тормозить только по очереди писателя и только если она реально
    /// видна и реально переполнена; суммарное ожидание ограничено бюджетом «как если бы
    /// диск давал 60 МБ/с». Нет сигнала — не тормозим вообще: память при этом защищена
    /// тем, что кадры возвращаются в пул сразу после записи, а не копятся до конца.
    /// </summary>
    private sealed class WriteDrain
    {
        /// <summary>Как часто сверяться с писателем.</summary>
        private const long CheckEveryBytes = 4L * 1024 * 1024;
        /// <summary>Сколько данных писателю позволено держать в очереди.</summary>
        private const long QueueLimitBytes = 64L * 1024 * 1024;
        /// <summary>Потолок ожидания за одну сверку.</summary>
        private const int MaxWaitPerCheckMs = 500;
        /// <summary>
        /// Бюджет ожидания считаем от «медленного диска» 60 МБ/с: тормозить сохранение
        /// сильнее этого мы не имеем права ни при каких показаниях очереди.
        /// </summary>
        private const double BudgetBytesPerMs = 60.0 * 1024 * 1024 / 1000.0;

        private readonly IMFSinkWriter _writer;
        private readonly int _stream;
        private readonly long _total;
        private readonly long _waitBudgetMs;
        private readonly Action<double>? _progress;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private long _submitted, _lastCheck, _lastProgressMs, _maxQueued;
        private bool _statsAvailable = true;

        /// <summary>Сколько всего ждали писателя (диагностика в логе).</summary>
        public long WaitedMs { get; private set; }

        /// <summary>
        /// Темп подачи, когда очередь писателя не видна.
        ///
        /// Не тормозить вовсе оказалось нельзя: писатель принимает сэмплы намного
        /// быстрее, чем сливает их на диск, и держит клип в нативной памяти. В
        /// замерах сохранение 800 МБ поднимало память процесса с 1.2 до 2.2 ГБ,
        /// причём куча .NET не менялась вовсе — то есть весь гигабайт был внутри
        /// Media Foundation.
        ///
        public string Report => _statsAvailable
            ? $"очередь писателя: пик {Storage.ByteSize.Format(_maxQueued)}, ждали {WaitedMs} мс"
            : "очередь писателя не видна — подача без ограничений";

        public WriteDrain(IMFSinkWriter writer, int probeStream, long totalBytes, Action<double>? progress)
        {
            _writer = writer;
            _stream = probeStream;
            _total = Math.Max(1, totalBytes);
            _progress = progress;
            _waitBudgetMs = Math.Max(3000, (long)(_total / BudgetBytesPerMs));
        }

        public void Submitted(int bytes)
        {
            _submitted += bytes;
            ReportProgress();
            if (_submitted - _lastCheck < CheckEveryBytes) return;
            _lastCheck = _submitted;

            if (!_statsAvailable) return; // очередь писателя не видна — не тормозим

            long start = _clock.ElapsedMilliseconds;
            WaitForQueue();
            WaitedMs += _clock.ElapsedMilliseconds - start;
        }

        /// <summary>
        /// Ждём, только если писатель реально не успевает разбирать очередь.
        /// Пока успевает — не тормозим ни на миллисекунду: на замерах он держит
        /// ~870 МБ/с, и любой фиксированный потолок тут только растягивал сохранение.
        /// </summary>
        private void WaitForQueue()
        {
            long start = _clock.ElapsedMilliseconds;
            while (_clock.ElapsedMilliseconds - start < MaxWaitPerCheckMs && WaitedMs < _waitBudgetMs)
            {
                long queued = QueuedBytes();
                if (queued < 0) return; // статистика отвалилась прямо сейчас
                if (queued > _maxQueued) _maxQueued = queued;
                if (queued <= QueueLimitBytes) break;
                Thread.Sleep(5);
            }
        }

        /// <summary>Байты в очереди писателя; -1 — статистика недоступна.</summary>
        private long QueuedBytes()
        {
            try
            {
                var stats = _writer.GetStatistics(_stream);
                return stats.ByteCountQueued;
            }
            catch
            {
                // Обёртка/система не дают статистику — просто больше не спрашиваем
                _statsAvailable = false;
                return -1;
            }
        }

        /// <summary>Прогресс наружу не чаще пяти раз в секунду — его читает UI.</summary>
        private void ReportProgress()
        {
            if (_progress is null) return;
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastProgressMs < 200) return;
            _lastProgressMs = now;
            _progress(Math.Clamp(_submitted / (double)_total, 0, 1));
        }
    }
}
