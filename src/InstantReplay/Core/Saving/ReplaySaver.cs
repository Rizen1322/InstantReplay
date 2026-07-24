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

        using IMFSinkWriter writer = MfMp4Writer.Create(filePath);

        int videoStream = MfMp4Writer.AddPassthroughVideoStream(writer, videoType);
        var audioStreams = MfMp4Writer.AddAudioStreams(writer, trackMode, hasGame, hasMic);

        writer.BeginWriting();
        tOpen = sw.ElapsedMilliseconds;

        // Ноль времени клипа = pts первого видеокадра (keyframe)
        long baseTicks = video[0].PtsTicks;

        foreach (var f in video)
        {
            using var sample = MfMp4Writer.CreateSample(f.Data, f.Length, f.PtsTicks - baseTicks, f.DurationTicks);
            if (f.IsKeyframe) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
            writer.WriteSample(videoStream, sample);
        }
        tVideo = sw.ElapsedMilliseconds;

        foreach (var (index, selector) in audioStreams)
        {
            foreach (var block in audio)
            {
                long pts = block.PtsTicks - baseTicks;
                if (pts < 0) continue; // блоки до начала видео не пишем
                float[] pcm = selector(block);
                var bytes = MemoryMarshal.AsBytes<float>(pcm).ToArray();
                using var sample = MfMp4Writer.CreateSample(bytes, bytes.Length, pts, 100_000 /*10 мс*/);
                writer.WriteSample(index, sample);
            }
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
        double clipSeconds = (video[^1].PtsTicks - video[0].PtsTicks) / 10_000_000.0;
        string fps = clipSeconds > 0.5 ? $", реально {video.Count / clipSeconds:F1} fps" : "";
        long total = sw.ElapsedMilliseconds;
        long fileBytes = 0;
        try { fileBytes = new FileInfo(filePath).Length; } catch { }

        Log.Info("Saver", $"Сохранено: {filePath} ({video.Count} кадров за {clipSeconds:F1} с{fps}, " +
                          $"{audio.Count} аудиоблоков)");
        Log.Info("Saver", $"Запись файла заняла {total} мс " +
                          $"(открытие {tOpen}, видео {tVideo - tOpen}, аудио {tAudio - tVideo}, " +
                          $"финализация {total - tAudio}); {fileBytes / (1024 * 1024)} МБ, " +
                          $"{(total > 0 ? fileBytes / 1024.0 / 1024 / (total / 1000.0) : 0):F0} МБ/с");
    }
}
