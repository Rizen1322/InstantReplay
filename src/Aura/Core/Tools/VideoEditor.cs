using Aura.Core.Logging;
using Aura.Core.Storage;
using Windows.Media.Editing;
using Windows.Storage;

namespace Aura.Core.Tools;

/// <summary>
/// Экспорт фрагмента из встроенного редактора.
///
/// Точный режим использует общий монтажный конвейер и встроенные кодеки.
/// Временный файл публикуется только после успешного рендера:
/// оборванный экспорт никогда не появляется в библиотеке как готовый клип.
/// </summary>
public static class VideoEditor
{
    internal static Task PrepareMixedAudioAsync(string input, string output, int tracks, string? ffmpeg, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            Saving.Mp4.Mp4Trim.MixAudio(input, output, audio => Audio.AudioMixdown.Mix(audio, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ffmpeg is not null)
        {
            File.Delete(output);
            await Ffmpeg.MixAudioAsync(ffmpeg, input, output, tracks, ct);
        }
    }, ct);

    internal static uint WindowsAudioTrackIndex(string input, int count, int requested)
    {
        if (count <= 1) return 0;
        requested = Math.Clamp(requested, 0, count - 1);
        using var file = File.OpenRead(input);
        // Проверено звуком: Windows разворачивает дорожки MP4, но сохраняет порядок MKV.
        bool mp4 = Saving.Mp4.Mp4Keyframes.ReadTopLevel(file, "ftyp") is not null;
        return (uint)(mp4 ? count - 1 - requested : requested);
    }

    public static string CreateOutputPath(string input)
    {
        string desired = Path.Combine(
            Path.GetDirectoryName(input)!,
            Path.GetFileNameWithoutExtension(input) + " (фрагмент).mp4");
        return FileNaming.NextAvailablePath(desired, File.Exists);
    }

    public static async Task<TimeSpan> ReadDurationAsync(string input)
    {
        var file = await StorageFile.GetFileFromPathAsync(input);
        var clip = await MediaClip.CreateFromFileAsync(file);
        return clip.OriginalDuration;
    }

    /// <summary>
    /// Вырезать фрагмент своими силами, без перекодирования и без кодеков
    /// (<see cref="Saving.Mp4.Mp4Trim"/>): работает для H.264, HEVC и AV1 на любой
    /// Windows. <paramref name="audioTrackIndex"/> -1 — все дорожки: при
    /// <paramref name="mixAudio"/> они сводятся в одну (звук перекодируется, видео нет),
    /// иначе остаются отдельными.
    /// </summary>
    public static async Task<string> TrimLosslessAsync(
        string input,
        TimeSpan start,
        TimeSpan end,
        int audioTrackIndex = -1,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        bool mixAudio = false)
    {
        ValidateRange(start, end);
        string output = CreateOutputPath(input);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(output)!,
            ".aura-edit-" + Guid.NewGuid().ToString("N") + ".part.mp4");

        Log.Info("Editor", $"Вырезка без перекодирования {Path.GetFileName(input)}: {start} — {end}, " +
                           $"audio={audioTrackIndex}{(mixAudio ? ", сведение" : "")}");
        try
        {
            await Task.Run(() => Saving.Mp4.Mp4Trim.Cut(
                input, tempPath, start.TotalSeconds, end.TotalSeconds, audioTrackIndex,
                p => progress?.Report(p), ct, mixAudio ? Audio.AudioMixdown.Mix : null), ct);
            if (File.Exists(output)) output = CreateOutputPath(input);
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, output);
            progress?.Report(1);
            return output;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static async Task<string> TrimPreciseAsync(
        string input,
        TimeSpan start,
        TimeSpan end,
        int audioTrackIndex = -1,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? ffmpeg = null)
    {
        ValidateRange(start, end);
        var info = await VideoAssembly.ReadInfoAsync(input, ct);
        if (end > info.Duration) end = info.Duration;
        ValidateRange(start, end);
        int track = audioTrackIndex < 0 || info.AudioTracks == 0 ? -1 :
            Math.Clamp(audioTrackIndex, 0, info.AudioTracks - 1);
        Log.Info("Editor", $"Точный экспорт {Path.GetFileName(input)}: {start} — {end}");
        return await VideoAssembly.RenderAsync([new VideoSegment(input, start, end, track)],
            CreateOutputPath(input), ffmpeg, progress: progress, ct: ct);
    }

    internal static void ValidateRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end <= start || end - start < TimeSpan.FromMilliseconds(50))
            throw new ArgumentOutOfRangeException(nameof(end), "фрагмент должен быть длиннее 0,05 секунды");
    }
}
