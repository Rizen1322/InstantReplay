using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
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
        using IMFAttributes attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        if (disableThrottling) attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        return MediaFactory.MFCreateSinkWriterFromURL(filePath, null, attrs);
    }

    /// <summary>Видеопоток без перекодирования: input type == output type == сжатый.</summary>
    public static int AddPassthroughVideoStream(IMFSinkWriter writer, IMFMediaType videoType)
    {
        int index = writer.AddStream(videoType);
        writer.SetInputMediaType(index, videoType, null);
        return index;
    }

    /// <summary>AAC-LC 48k stereo 192 kbps; на вход — PCM16 из микшера.</summary>
    public static int AddAacStream(IMFSinkWriter writer)
    {
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
        outType.Set(MediaTypeAttributeKeys.AudioNumChannels, Channels);
        outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
        outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000); // 192 kbps
        int idx = writer.AddStream(outType);

        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, Channels);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, Channels * 2);
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, SampleRate * Channels * 2);
        writer.SetInputMediaType(idx, inType, null);
        return idx;
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
        bool wantGame = hasGame && trackMode is not AudioTrackMode.MicOnly;
        bool wantMic = hasMic && trackMode is not AudioTrackMode.GameOnly;
        if (!wantGame && !wantMic) return streams;

        if (trackMode == AudioTrackMode.Separate && wantGame && wantMic)
        {
            streams.Add((AddAacStream(writer), AudioTrackKind.Game));
            streams.Add((AddAacStream(writer), AudioTrackKind.Mic));
        }
        else
        {
            streams.Add((AddAacStream(writer),
                !wantMic ? AudioTrackKind.Game
                : !wantGame ? AudioTrackKind.Mic
                : AudioTrackKind.Mixed));
        }
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
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
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
