using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using InstantReplay.Core.Buffering;
using InstantReplay.Core.Encoding;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Saving;

/// <summary>
/// Сохранение снимка кольцевых буферов в MP4.
///
/// Видео уже сжато (H264/HEVC/AV1) — SinkWriter работает в passthrough-режиме
/// (input type == output type == сжатый), т.е. это чистый remux: сохранение
/// 5-минутного клипа занимает доли секунды и не грузит GPU/CPU.
/// Аудио — PCM float 48k из микшера, кодируется в AAC самим SinkWriter.
/// Режим дорожек (Mixed/Separate/GameOnly/MicOnly) применяется здесь.
/// </summary>
public static class ReplaySaver
{
    private const int MfESinkHeadersNotFound = unchecked((int)0xC00D4A45);

    /// <summary>Имя кодека из подтипа медиатипа — для понятных сообщений об ошибке.</summary>
    private static string VideoCodecName(IMFMediaType type)
    {
        try
        {
            Guid sub = type.GetGUID(MediaTypeAttributeKeys.Subtype);
            if (sub == VideoEncoder.SubtypeFor(VideoCodec.AV1)) return "AV1";
            if (sub == VideoFormatGuids.Hevc) return "HEVC";
            if (sub == VideoFormatGuids.H264) return "H.264";
        }
        catch { }
        return "этот кодек";
    }

    public static void Save(
        string filePath,
        List<EncodedFrame> video,
        List<AudioBlock> audio,
        IMFMediaType videoType,
        AudioTrackMode trackMode,
        bool hasGame, bool hasMic)
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
        using IMFSinkWriter writer = MfMp4Writer.Create(filePath);

        int videoStream = MfMp4Writer.AddPassthroughVideoStream(writer, videoType);
        var audioStreams = MfMp4Writer.AddAudioStreams(writer, trackMode, hasGame, hasMic);

        writer.BeginWriting();
        tOpen = sw.ElapsedMilliseconds;

        // Ноль времени клипа = pts первого видеокадра (keyframe)
        long baseTicks = video[0].PtsTicks;
        // Запоминаем ДО записи: список кадров очищается по ходу (массивы уходят в пул)
        int frameCount = video.Count;
        double clipSeconds = (video[^1].PtsTicks - baseTicks) / 10_000_000.0;

        // Дорожки пишем ЧЕРЕДУЯ по времени, а не «сначала всё видео, потом всё аудио»:
        // MP4 хранит потоки вперемешку, так писателю не нужно переупорядочивать их самому.
        //
        // И освобождаем массивы кадров ПО ХОДУ записи. Замеры показали резкую
        // нелинейность: 367 МБ сохранялись за 0.95 с (386 МБ/с), а 950 МБ — за 20 с
        // (47 МБ/с). Это не диск (NVMe), а нехватка памяти: снимок буфера (~1 ГБ)
        // держался целиком до конца сохранения, столько же копилось внутри писателя,
        // а рядом игра занимает большую часть ОЗУ — система уходила в подкачку.
        // CreateSample копирует данные в буфер Media Foundation, поэтому наш массив
        // можно вернуть в пул сразу же.
        // Ограничение темпа подачи. Замеры показали: на клипе 91 МБ финализация
        // занимает 5 мс — писатель успевает слить всё на диск прямо по ходу записи
        // и держит ~340 МБ/с. На клипе 978 МБ мы отдаём ему всё за 0.98 с, он не
        // успевает, копит в памяти и потом разгребает 54 секунды на 18 МБ/с.
        // Подаём не быстрее, чем он реально пишет — очередь не растёт, финализация
        // остаётся мгновенной, а игра не встаёт от залпового ввода-вывода.
        const double TargetBytesPerMs = 220.0 * 1024 * 1024 / 1000.0; // ~220 МБ/с
        var pace = System.Diagnostics.Stopwatch.StartNew();
        long paced = 0;

        void Pace(int bytes)
        {
            paced += bytes;
            double aheadMs = paced / TargetBytesPerMs - pace.ElapsedMilliseconds;
            if (aheadMs > 5) Thread.Sleep((int)aheadMs);
        }

        // Аудио пишем КРУПНЫМИ кусками (~1 с), а не блоками по 10 мс, как они приходят
        // из микшера. Скорость финализации у Media Foundation определяется ЧИСЛОМ
        // сэмплов в контейнере, а не объёмом: на 180-секундном клипе блоки по 10 мс
        // давали 18 080 сэмплов на дорожку (36 160 на две) против 10 849 видеокадров —
        // то есть 77% всех сэмплов были аудио. Секундные куски сокращают их до 180
        // на дорожку. AAC-энкодер сам режет PCM на свои кадры, качество не меняется.
        const int BlocksPerChunk = 100;                       // 100 × 10 мс = 1 секунда
        int blockSamples = audio.Count > 0 ? audio[0].Game.Length : 960;
        // Буфер СВОЙ на каждую дорожку: они накапливаются параллельно
        var chunkBuf = new float[audioStreams.Count][];
        for (int s = 0; s < audioStreams.Count; s++)
            chunkBuf[s] = new float[BlocksPerChunk * blockSamples];
        var chunkFill = new int[audioStreams.Count];           // сколько блоков накоплено
        var chunkStart = new long[audioStreams.Count];         // pts первого блока куска

        // Сбросить накопленный кусок дорожки s одним сэмплом
        void FlushAudio(int s)
        {
            if (chunkFill[s] == 0) return;
            int floats = chunkFill[s] * blockSamples;
            var bytes = MemoryMarshal.AsBytes<float>(chunkBuf[s].AsSpan(0, floats)).ToArray();
            using var sample = MfMp4Writer.CreateSample(
                bytes, bytes.Length, chunkStart[s], chunkFill[s] * 100_000L);
            writer.WriteSample(audioStreams[s].Index, sample);
            Pace(bytes.Length);
            chunkFill[s] = 0;
        }

        var audioPos = new int[audioStreams.Count];
        int released = 0;
        try
        {
        for (int fi = 0; fi < video.Count; fi++)
        {
            var f = video[fi];
            long vpts = f.PtsTicks - baseTicks;

            // Сначала — всё аудио, которое звучит раньше этого кадра
            for (int s = 0; s < audioStreams.Count; s++)
            {
                var (index, selector) = audioStreams[s];
                while (audioPos[s] < audio.Count)
                {
                    long apts = audio[audioPos[s]].PtsTicks - baseTicks;
                    if (apts > vpts) break;
                    if (apts >= 0)
                    {
                        if (chunkFill[s] == 0) chunkStart[s] = apts;
                        selector(audio[audioPos[s]])
                            .CopyTo(chunkBuf[s].AsSpan(chunkFill[s] * blockSamples));
                        if (++chunkFill[s] >= BlocksPerChunk) FlushAudio(s);
                    }
                    audioPos[s]++;
                }
            }

            using (var sample = MfMp4Writer.CreateSample(f.Data, f.Length, vpts, f.DurationTicks))
            {
                if (f.IsKeyframe) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
                writer.WriteSample(videoStream, sample);
            }
            // Данные уже скопированы в сэмпл — массив больше не нужен
            System.Buffers.ArrayPool<byte>.Shared.Return(f.Data);
            released = fi + 1;
            Pace(f.Length);
        }
        }
        finally
        {
            // Если запись оборвалась — вернуть в пул то, до чего не дошли,
            // и очистить список, чтобы движок не вернул те же массивы повторно.
            for (int i = released; i < video.Count; i++)
                System.Buffers.ArrayPool<byte>.Shared.Return(video[i].Data);
            video.Clear();
        }
        tVideo = sw.ElapsedMilliseconds;

        // Хвост аудио после последнего видеокадра
        for (int s = 0; s < audioStreams.Count; s++)
        {
            var (index, selector) = audioStreams[s];
            for (; audioPos[s] < audio.Count; audioPos[s]++)
            {
                long apts = audio[audioPos[s]].PtsTicks - baseTicks;
                if (apts < 0) continue;
                if (chunkFill[s] == 0) chunkStart[s] = apts;
                selector(audio[audioPos[s]]).CopyTo(chunkBuf[s].AsSpan(chunkFill[s] * blockSamples));
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
        Log.Info("Saver", $"Запись файла заняла {total} мс " +
                          $"(открытие {tOpen}, видео {tVideo - tOpen}, аудио {tAudio - tVideo}, " +
                          $"финализация {total - tAudio}); {fileBytes / (1024 * 1024)} МБ, " +
                          $"{(total > 0 ? fileBytes / 1024.0 / 1024 / (total / 1000.0) : 0):F0} МБ/с");
    }
}
