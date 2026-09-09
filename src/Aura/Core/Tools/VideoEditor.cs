using Aura.Core.Logging;
using Aura.Core.Storage;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Aura.Core.Tools;

/// <summary>
/// Экспорт фрагмента из встроенного редактора.
///
/// Точный режим использует штатный Windows Media Editing и поэтому работает без
/// сторонних программ. Временный файл публикуется только после успешного рендера:
/// оборванный экспорт никогда не появляется в библиотеке как готовый клип.
/// </summary>
public static class VideoEditor
{
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

    public static async Task<string> TrimPreciseAsync(
        string input,
        TimeSpan start,
        TimeSpan end,
        int audioTrackIndex = -1,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ValidateRange(start, end);

        string output = CreateOutputPath(input);
        string tempPath = Path.Combine(
            Path.GetDirectoryName(output)!,
            ".aura-edit-" + Guid.NewGuid().ToString("N") + ".part.mp4");

        try
        {
            var inputFile = await StorageFile.GetFileFromPathAsync(input);
            var clip = await MediaClip.CreateFromFileAsync(inputFile);
            if (end > clip.OriginalDuration) end = clip.OriginalDuration;
            ValidateRange(start, end);

            clip.TrimTimeFromStart = start;
            clip.TrimTimeFromEnd = clip.OriginalDuration - end;

            bool mixAll = audioTrackIndex < 0;
            if (!mixAll && clip.EmbeddedAudioTracks.Count > 0)
            {
                int requested = Math.Clamp(audioTrackIndex, 0, clip.EmbeddedAudioTracks.Count - 1);
                // MediaClip перечисляет embedded-аудио в обратном порядке относительно
                // MP4 stream index (проверено на двухтональном файле). UI и ffmpeg идут
                // в естественном порядке контейнера, поэтому здесь разворачиваем индекс.
                clip.SelectedEmbeddedAudioTrackIndex = (uint)(clip.EmbeddedAudioTracks.Count - 1 - requested);
            }

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            // MediaClip проигрывает только выбранную встроенную дорожку. У Aura
            // режим «раздельно» создаёт две (игра + микрофон), поэтому остальные
            // добавляем как фоновые и теми же IN/OUT. Точный экспорт сводит их в
            // слышимый микс вместо тихой потери микрофона.
            for (int i = 0; mixAll && i < clip.EmbeddedAudioTracks.Count; i++)
            {
                if (i == clip.SelectedEmbeddedAudioTrackIndex) continue;
                var audio = BackgroundAudioTrack.CreateFromEmbeddedAudioTrack(clip.EmbeddedAudioTracks[i]);
                TimeSpan audioEnd = end > audio.OriginalDuration ? audio.OriginalDuration : end;
                if (start >= audioEnd) continue;
                audio.TrimTimeFromStart = start;
                audio.TrimTimeFromEnd = audio.OriginalDuration - audioEnd;
                composition.BackgroundAudioTracks.Add(audio);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
            var temp = await folder.CreateFileAsync(
                Path.GetFileName(tempPath), CreationCollisionOption.ReplaceExisting);

            var operation = composition.RenderToFileAsync(
                temp,
                MediaTrimmingPreference.Precise,
                composition.CreateDefaultEncodingProfile());
            operation.Progress = (_, value) => progress?.Report(value / 100.0);
            using var cancel = ct.Register(operation.Cancel);

            Log.Info("Editor", $"Точный экспорт {Path.GetFileName(input)}: {start} — {end}");
            TranscodeFailureReason result = await operation;
            ct.ThrowIfCancellationRequested();
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Windows Media вернул ошибку: {result}");

            // Другое окно редактора могло закончить экспорт с тем же именем,
            // пока шёл рендер. Проверяем коллизию прямо перед публикацией.
            if (File.Exists(output)) output = CreateOutputPath(input);
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

    internal static void ValidateRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end <= start || end - start < TimeSpan.FromMilliseconds(50))
            throw new ArgumentOutOfRangeException(nameof(end), "фрагмент должен быть длиннее 0,05 секунды");
    }
}
