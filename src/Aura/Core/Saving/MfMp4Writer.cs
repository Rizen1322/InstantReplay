using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Saving;

/// <summary>
/// Общие куски для записи MP4 через SinkWriter: используются и мгновенным
/// сохранением повтора (ReplaySaver), и обычной записью в файл (ManualRecorder).
/// Видео всегда passthrough сжатого битстрима, аудио — AAC из PCM16.
/// </summary>
internal static class MfMp4Writer
{
    public const int SampleRate = Audio.AudioCaptureSource.SampleRate;
    public const int Channels = Audio.AudioCaptureSource.Channels;

    /// <summary>С какого времени создание писателя стоит отдельной строки в логе.</summary>
    private const long SlowCreateLogMs = 1000;

    /// <summary>
    /// SinkWriter для MP4. По умолчанию throttling ВЫКЛЮЧЕН — так работают оба
    /// сценария записи:
    ///
    /// • ManualRecorder пишет в реальном времени из колбэка энкодера, и блокировка
    ///   писателем застопорила бы весь конвейер;
    /// • ReplaySaver отдаёт готовый снимок буфера, и включённый throttling добавляет
    ///   паузы, рассчитанные на реальное время: в замерах фаза записи выросла с 0.6
    ///   до 33 секунд. Темп подачи там ограничивается своим пейсером (см. ReplaySaver).
    /// </summary>
    public static IMFSinkWriter Create(string filePath, bool disableThrottling = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using IMFAttributes attrs = MediaFactory.MFCreateAttributes(3);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        if (disableThrottling) attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);

        // Контейнер задаём ЯВНО, а не расширением файла.
        //
        // MFCreateSinkWriterFromURL выбирает медиасинк по расширению пути. Пока файл
        // назывался «...mp4», это работало само; как только запись пошла во временный
        // «...mp4.part» (чтобы недописанный файл не попадал в библиотеку), Media
        // Foundation перестала узнавать контейнер и отвечала MF_E_NOT_FOUND — то есть
        // сохранение падало на первом же шаге, ещё до единого записанного кадра.
        //
        // С собственным байтовым потоком расширение не участвует вовсе: тип контейнера
        // берётся из атрибута, и имя файла может быть любым.
        attrs.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);

        // Два замера вместо одного «открытие NNNN мс».
        //
        // ЗАЧЕМ. Открытие писателя — это два разных мира: создать файл (диск, права,
        // антивирус на пути записи) и построить конвейер Media Foundation (загрузка
        // DLL кодеков, чтение реестра, активация COM-объектов, опрос аппаратных MFT
        // у драйвера видеокарты). На живой машине этот шаг однажды занял 27.6 секунды,
        // и по одной суммарной цифре было не понять, кто виноват. Логируем только
        // когда медленно: на здоровой системе тут десятки миллисекунд и строки не будет.
        var openClock = System.Diagnostics.Stopwatch.StartNew();
        var stream = MediaFactory.MFCreateFile(
            FileAccessMode.MfAccessModeWrite,
            FileOpenMode.MfOpenModeDeleteIfExist,
            FileFlags.FlagsNone,
            filePath);
        long fileMs = openClock.ElapsedMilliseconds;

        // Байтовый поток отпускаем СРАЗУ после создания писателя. Медиасинк берёт
        // на него свою ссылку, поэтому поток живёт ровно столько, сколько нужен
        // писателю. Раньше Dispose стоял только в catch: на успешном пути наша
        // ссылка оставалась висеть, и объект Media Foundation вместе с его
        // внутренним буфером записи освобождался лишь финализатором. Во время
        // записи включён SustainedLowLatency, блокирующих сборок второго поколения
        // нет — то есть финализаторы не бегут, и после каждого сохранения процесс
        // прибавлял сотню-другую мегабайт до самой остановки конвейера.
        try
        {
            return MediaFactory.MFCreateSinkWriterFromURL(null, stream, attrs);
        }
        finally
        {
            stream.Dispose();
            long totalMs = openClock.ElapsedMilliseconds;
            if (totalMs > SlowCreateLogMs)
                Log.Warn("Saver", $"Создание писателя заняло {totalMs} мс " +
                                  $"(файл {fileMs}, конвейер Media Foundation {totalMs - fileMs})");
        }
    }

    /// <summary>
    /// Открытый файл: писатель и готовые номера потоков.
    ///
    /// Номера раздаёт не вызывающий, а сам контейнер, и у фрагментированного MP4 они
    /// фиксированы ещё до создания писателя. Поэтому оба пути возвращают одно и то же
    /// описание, и вызывающему не нужно знать, какой контейнер ему достался.
    /// </summary>
    internal readonly record struct Mp4Target(
        IMFSinkWriter Writer,
        int VideoStream,
        List<(int Index, AudioTrackKind Kind)> AudioStreams,
        bool Fragmented);

    /// <summary>
    /// Открыть MP4 на запись: сначала фрагментированный, при отказе — обычный.
    ///
    /// ЗАЧЕМ ФРАГМЕНТИРОВАННЫЙ. Обычный MP4 держит оглавление (moov) в конце файла, и
    /// до успешной финализации файла фактически нет: падение, выключение питания или
    /// ошибка на Finalize оставляли от записи ноль. Замер на этой машине, файл обрезан
    /// на 60% длины:
    ///
    ///   фрагментированный — Windows читает 1280x720, 3.00 с, миниатюра есть,
    ///                       Media Foundation вычитывает 55 кадров из 90;
    ///   обычный           — Windows показывает 0x0 и 0.00 с, миниатюры нет,
    ///                       Media Foundation падает с ошибкой.
    ///
    /// То есть у фрагментированного уцелело всё, что успело лечь на диск. Заодно
    /// исчезает причина, по которой запись резалась на части по 3.5 ГБ: каждый
    /// фрагмент адресуется сам по себе.
    ///
    /// Проверено там же: Windows одинаково читает оба контейнера целиком (размер кадра,
    /// длительность, битрейт, миниатюра), passthrough сжатого H.264 через этот синк
    /// работает, и вторая звуковая дорожка добавляется (режим «раздельно» не теряется).
    ///
    /// Откат на обычный контейнер оставлен на случай кодека, которого фрагментированный
    /// синк не знает: лучше записать по-старому, чем не записать вовсе.
    /// </summary>
    public static Mp4Target Open(
        string filePath,
        IMFMediaType videoType,
        AudioTrackMode trackMode,
        bool hasGame,
        bool hasMic,
        bool disableThrottling = true)
    {
        try
        {
            return OpenFragmented(filePath, videoType, trackMode, hasGame, hasMic, disableThrottling);
        }
        catch (Exception ex)
        {
            Log.Warn("Saver", $"Фрагментированный MP4 не открылся ({ex.Message}) — пишу обычный");
        }

        IMFSinkWriter writer = Create(filePath, disableThrottling);
        int videoStream = AddPassthroughVideoStream(writer, videoType);
        var audioStreams = AddAudioStreams(writer, trackMode, hasGame, hasMic);
        writer.BeginWriting();
        return new Mp4Target(writer, videoStream, audioStreams, Fragmented: false);
    }

    /// <summary>
    /// Фрагментированный MP4. Дорожки объявляются ДО создания писателя: синк берёт
    /// типы в конструкторе, а не принимает AddStream, как обычный писатель.
    ///
    /// Порядок дорожек проверен на живом синке: индекс 0 — видео, дальше звук в том
    /// порядке, в котором их добавляли. Идентификаторы 1 и 2 синк занимает сам, поэтому
    /// вторая звуковая идёт под номером 3 (номера 1 и 2 отвечают MF_E_STREAMSINK_EXISTS).
    /// </summary>
    private static Mp4Target OpenFragmented(
        string filePath,
        IMFMediaType videoType,
        AudioTrackMode trackMode,
        bool hasGame,
        bool hasMic,
        bool disableThrottling)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var kinds = AudioTrackKinds(trackMode, hasGame, hasMic);

        var stream = MediaFactory.MFCreateFile(
            FileAccessMode.MfAccessModeWrite,
            FileOpenMode.MfOpenModeDeleteIfExist,
            FileFlags.FlagsNone,
            filePath);

        IMFMediaSink? sink = null;
        IMFMediaType? firstAudio = null;
        try
        {
            firstAudio = kinds.Count > 0 ? AacOutputType() : null;
            MediaFactory.MFCreateFMPEG4MediaSink(stream, videoType, firstAudio, out sink).CheckError();

            // Вторая звуковая дорожка — только для режима «раздельно».
            const int SecondAudioStreamId = 3;
            if (kinds.Count > 1)
            {
                using IMFMediaType secondAudio = AacOutputType();
                sink.AddStreamSink(SecondAudioStreamId, secondAudio).Dispose();
            }

            using IMFAttributes attrs = MediaFactory.MFCreateAttributes(2);
            attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            if (disableThrottling) attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);

            IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromMediaSink(sink, attrs);
            try
            {
                // Видео идёт без перекодирования: вход тем же типом, что и выход.
                writer.SetInputMediaType(0, videoType, null);

                var audioStreams = new List<(int Index, AudioTrackKind Kind)>(kinds.Count);
                for (int i = 0; i < kinds.Count; i++)
                {
                    using IMFMediaType pcm = PcmInputType();
                    writer.SetInputMediaType(i + 1, pcm, null);
                    audioStreams.Add((i + 1, kinds[i]));
                }

                writer.BeginWriting();
                return new Mp4Target(writer, 0, audioStreams, Fragmented: true);
            }
            catch
            {
                // Писатель держит файл открытым. Не закрыв его, запасной обычный путь
                // открыл бы то же имя и получил отказ доступа — то есть отказ
                // фрагментированного контейнера превращался бы в отказ записи вообще.
                writer.Dispose();
                sink.Shutdown();
                throw;
            }
        }
        finally
        {
            firstAudio?.Dispose();
            sink?.Dispose();     // писатель держит свою ссылку на синк
            stream.Dispose();    // и на байтовый поток
        }
    }

    /// <summary>Видеопоток без перекодирования: input type == output type == сжатый.</summary>
    public static int AddPassthroughVideoStream(IMFSinkWriter writer, IMFMediaType videoType)
    {
        int index = writer.AddStream(videoType);
        writer.SetInputMediaType(index, videoType, null);
        return index;
    }

    /// <summary>AAC-LC 48k stereo 192 kbps — то, что лежит в файле.</summary>
    private static IMFMediaType AacOutputType()
    {
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
        outType.Set(MediaTypeAttributeKeys.AudioNumChannels, Channels);
        outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
        outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000); // 192 kbps
        return outType;
    }

    /// <summary>PCM16 из микшера — то, что мы подаём писателю; в AAC он кодирует сам.</summary>
    private static IMFMediaType PcmInputType()
    {
        var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, Channels);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, Channels * 2);
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, SampleRate * Channels * 2);
        return inType;
    }

    /// <summary>AAC-LC 48k stereo 192 kbps; на вход — PCM16 из микшера.</summary>
    public static int AddAacStream(IMFSinkWriter writer)
    {
        using var outType = AacOutputType();
        int idx = writer.AddStream(outType);

        using var inType = PcmInputType();
        writer.SetInputMediaType(idx, inType, null);
        return idx;
    }

    /// <summary>
    /// Какие звуковые дорожки окажутся в файле — по режиму и по тому, что вообще писали.
    /// Вынесено отдельно, потому что фрагментированному синку дорожки надо объявить
    /// ДО создания писателя, а обычному — после.
    /// </summary>
    private static List<AudioTrackKind> AudioTrackKinds(
        AudioTrackMode trackMode, bool hasGame, bool hasMic)
    {
        bool wantGame = hasGame && trackMode is not AudioTrackMode.MicOnly;
        bool wantMic = hasMic && trackMode is not AudioTrackMode.GameOnly;
        if (!wantGame && !wantMic) return [];

        if (trackMode == AudioTrackMode.Separate && wantGame && wantMic)
            return [AudioTrackKind.Game, AudioTrackKind.Mic];

        return [!wantMic ? AudioTrackKind.Game
              : !wantGame ? AudioTrackKind.Mic
              : AudioTrackKind.Mixed];
    }

    /// <summary>
    /// Аудиопотоки по режиму дорожек: список (индекс потока, что в него писать).
    ///
    /// Отдаём именно ВИД дорожки, а не функцию-селектор: селектор возвращал массив,
    /// и в сведённом режиме выделял новый на каждый блок — в обычной записи это
    /// происходило на потоке микшера с приоритетом Highest, сто раз в секунду.
    /// Теперь вызывающий сам подставляет готовый приёмник (см. AudioBlock.Mix).
    /// </summary>
    public static List<(int Index, AudioTrackKind Kind)> AddAudioStreams(
        IMFSinkWriter writer, AudioTrackMode trackMode, bool hasGame, bool hasMic)
    {
        var streams = new List<(int, AudioTrackKind)>();
        foreach (AudioTrackKind kind in AudioTrackKinds(trackMode, hasGame, hasMic))
            streams.Add((AddAacStream(writer), kind));
        return streams;
    }

    // ---------------- Учёт сэмплов Media Foundation ----------------
    //
    // Вопрос, который нельзя решить рассуждением: кто держит ссылки на сэмплы после
    // записи. Если писатель отпускает их сразу, счётчик ссылок на момент нашего
    // Dispose равен единице (только наша). Если больше — объект переживёт нас, и
    // тогда рост памяти это удержание COM-объектов, а не фрагментация кучи.
    private static long _samplesCreated, _samplesHeld, _maxRefCount;

    public static void ResetSampleCounters()
    {
        Interlocked.Exchange(ref _samplesCreated, 0);
        Interlocked.Exchange(ref _samplesHeld, 0);
        Interlocked.Exchange(ref _maxRefCount, 0);
    }

    public static string SampleReport =>
        $"сэмплы MF: создано {Interlocked.Read(ref _samplesCreated)}, " +
        $"удержано писателем {Interlocked.Read(ref _samplesHeld)}, " +
        $"максимум ссылок {Interlocked.Read(ref _maxRefCount)}";

    /// <summary>
    /// Отдать сэмпл, посчитав, сколько ссылок на него осталось у чужого кода.
    /// AddRef/Release вокруг замера не меняют состояние объекта.
    /// </summary>
    public static void ReleaseSample(IMFSample sample)
    {
        try
        {
            IntPtr ptr = sample.NativePointer;
            if (ptr != IntPtr.Zero)
            {
                int afterAddRef = Marshal.AddRef(ptr);
                Marshal.Release(ptr);
                int others = afterAddRef - 2; // минус наш AddRef и минус наша ссылка
                if (others > 0) Interlocked.Increment(ref _samplesHeld);

                long current;
                while (afterAddRef - 1 > (current = Interlocked.Read(ref _maxRefCount)))
                    if (Interlocked.CompareExchange(ref _maxRefCount, afterAddRef - 1, current) == current) break;
            }
        }
        catch { }
        sample.Dispose();
    }

    /// <summary>
    /// Сэмпл БЕЗ копирования: буфер ссылается прямо на память по указателю
    /// (см. <see cref="ArenaMediaBuffer"/>). Вызывающий обязан держать эту память
    /// закреплённой и живой, пока писатель не отпустит сэмпл — за этим и следит
    /// <paramref name="batch"/>, партия буферов текущего сохранения.
    /// </summary>
    public static IMFSample CreateSampleNoCopy(
        ArenaBufferBatch batch, IntPtr data, int length, long ptsTicks, long durationTicks)
    {
        IntPtr bufferPtr = batch.Create(data, length);
        // Обёртка забирает нашу единственную ссылку: AddBuffer поднимет счётчик до
        // двух, Dispose вернёт к одной, и дальше буфером владеет сэмпл.
        using var buffer = new IMFMediaBuffer(bufferPtr);

        var sample = MediaFactory.MFCreateSample();
        Interlocked.Increment(ref _samplesCreated);
        sample.AddBuffer(buffer);
        sample.SampleTime = ptsTicks;
        sample.SampleDuration = durationTicks;
        return sample;
    }

    /// <summary>
    /// Сэмпл из куска чужого массива: кадры лежат в арене кольцевого буфера
    /// вплотную друг за другом, поэтому нужны и смещение, и длина.
    /// Копирует данные — нужен там, где исходный массив переиспользуется сразу
    /// после записи (звук в ReplaySaver, очередь ManualRecorder).
    /// </summary>
    public static IMFSample CreateSample(byte[] data, int offset, int length, long ptsTicks, long durationTicks)
    {
        // Округление ёмкости до степени двойки пробовали — стало ХУЖЕ: средний кадр
        // 78 КБ округлялся до 128 КБ, и прирост нативной памяти вырос с 1.3 до 1.7
        // размера клипа. Нативная куча удерживает сумму выделений, а не пик живых,
        // поэтому выделять надо ровно столько, сколько нужно.
        // Буфер отпускаем СРАЗУ после AddBuffer: MFCreateMemoryBuffer отдаёт ссылку
        // нам, AddBuffer добавляет свою, и дальше буфером владеет сэмпл. Раньше наша
        // ссылка оставалась висеть и снималась только финализатором. Во время записи
        // включён SustainedLowLatency, блокирующих сборок второго поколения нет —
        // то есть финализаторы не бегут, и нативная куча копила эти буферы до самой
        // остановки конвейера. На трёхминутном клипе это 360 кусков звука по 192 КБ,
        // около 70 МБ за каждое сохранение; ManualRecorder зовёт этот метод на
        // каждый кадр, и там счёт шёл на сотни мегабайт.
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(data, offset, ptr, length);
        buffer.Unlock();
        buffer.CurrentLength = length;

        var sample = MediaFactory.MFCreateSample();
        Interlocked.Increment(ref _samplesCreated);
        sample.AddBuffer(buffer);
        sample.SampleTime = ptsTicks;
        sample.SampleDuration = durationTicks;
        return sample;
    }
}
