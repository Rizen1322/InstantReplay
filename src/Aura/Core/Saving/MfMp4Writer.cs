using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Settings;

namespace Aura.Core.Saving;

/// <summary>
/// Общие куски для записи MP4 через SinkWriter: используются и мгновенным
/// сохранением повтора (ReplaySaver), и обычной записью в файл (ManualRecorder).
/// Видео всегда passthrough сжатого битстрима, аудио — AAC из float32 PCM.
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

    /// <summary>AAC-LC 48k stereo 192 kbps; на вход — float32 PCM из микшера.</summary>
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
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, SampleRate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, Channels);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32);
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, Channels * 4);
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, SampleRate * Channels * 4);
        writer.SetInputMediaType(idx, inType, null);
        return idx;
    }

    /// <summary>
    /// Аудиопотоки по режиму дорожек: список (индекс потока, селектор PCM из блока).
    /// </summary>
    public static List<(int Index, Func<AudioBlock, float[]> Selector)> AddAudioStreams(
        IMFSinkWriter writer, AudioTrackMode trackMode, bool hasGame, bool hasMic)
    {
        var streams = new List<(int, Func<AudioBlock, float[]>)>();
        bool wantGame = hasGame && trackMode is not AudioTrackMode.MicOnly;
        bool wantMic = hasMic && trackMode is not AudioTrackMode.GameOnly;
        if (!wantGame && !wantMic) return streams;

        if (trackMode == AudioTrackMode.Separate && wantGame && wantMic)
        {
            streams.Add((AddAacStream(writer), b => b.Game));
            streams.Add((AddAacStream(writer), b => b.Mic));
        }
        else
        {
            streams.Add((AddAacStream(writer), b =>
            {
                if (!wantMic) return b.Game;
                if (!wantGame) return b.Mic;
                var mixed = new float[b.Game.Length];
                for (int i = 0; i < mixed.Length; i++)
                    mixed[i] = Math.Clamp(b.Game[i] + b.Mic[i], -1f, 1f);
                return mixed;
            }
            ));
        }
        return streams;
    }

    public static IMFSample CreateSample(byte[] data, int length, long ptsTicks, long durationTicks)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(data, 0, ptr, length);
        buffer.Unlock();
        buffer.CurrentLength = length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = ptsTicks;
        sample.SampleDuration = durationTicks;
        return sample;
    }
}
