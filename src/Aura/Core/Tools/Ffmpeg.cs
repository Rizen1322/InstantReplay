using System.Diagnostics;
using Aura.Core.Logging;
using Aura.Core.Storage;

namespace Aura.Core.Tools;

/// <summary>
/// Пережатие клипа под лимит вложения (Discord и подобное).
///
/// ffmpeg используется как необязательный движок для сжатия и быстрого монтажа
/// без перекодирования. Внешний интерфейс при этом не запускается.
/// </summary>
public static class Ffmpeg
{
    /// <summary>Найти ffmpeg.exe: рядом с LosslessCut, в PATH или там, где указали.</summary>
    public static string? Find(string? saved, string? losslessCutPath)
    {
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;

        // LosslessCut носит ffmpeg с собой — в своей папке или в resources\
        if (!string.IsNullOrWhiteSpace(losslessCutPath))
        {
            string? dir = Path.GetDirectoryName(losslessCutPath);
            if (dir is not null)
                foreach (string candidate in new[]
                         {
                             Path.Combine(dir, "ffmpeg.exe"),
                             Path.Combine(dir, "resources", "ffmpeg.exe"),
                             Path.Combine(dir, "resources", "app.asar.unpacked", "node_modules", "ffmpeg-static", "ffmpeg.exe"),
                         })
                    if (File.Exists(candidate)) return candidate;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Пережать клип так, чтобы файл влез в лимит.
    ///
    /// Битрейт считается от длительности, а разрешение опускается, если битрейта
    /// на исходное просто не хватит: 1440p на 400 кбит/с — это каша, 720p при том
    /// же весе смотрится нормально.
    /// </summary>
    public static async Task<string> CompressAsync(
        string ffmpeg, string input, TimeSpan duration, int targetMegabytes, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero) throw new InvalidOperationException("не удалось определить длительность клипа");

        const int audioKbps = 96;
        // 3% запаса на контейнер: mp4 сверху добавляет свои таблицы
        double totalKbps = targetMegabytes * 8.0 * 1024 * 0.97 / duration.TotalSeconds;
        int videoKbps = Math.Max(300, (int)(totalKbps - audioKbps));

        string scale = videoKbps switch
        {
            < 700 => "scale=-2:480",
            < 1500 => "scale=-2:720",
            < 3500 => "scale=-2:1080",
            _ => ""
        };

        string desiredOutput = Path.Combine(
            Path.GetDirectoryName(input)!,
            Path.GetFileNameWithoutExtension(input) + $" ({targetMegabytes} МБ).mp4");
        string output = FileNaming.NextAvailablePath(desiredOutput, File.Exists);
        string partPath = output + ".part";

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
        if (scale.Length > 0) { psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(scale); }
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
        psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add($"{videoKbps}k");
        psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add($"{(int)(videoKbps * 1.2)}k");
        psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add($"{videoKbps * 2}k");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{audioKbps}k");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        // У временного файла расширение .part, поэтому контейнер задаём явно.
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(partPath);

        Log.Info("Ffmpeg", $"Сжатие {Path.GetFileName(input)} → {videoKbps} кбит/с {scale}");

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить ffmpeg");
            string errors;
            try
            {
                errors = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                string tail = string.Join(" ", errors.Split('\n').TakeLast(3)).Trim();
                throw new InvalidOperationException($"ffmpeg вернул ошибку: {tail}");
            }

            long bytes = new FileInfo(partPath).Length;
            long limitBytes = targetMegabytes * 1024L * 1024L;
            if (bytes > limitBytes)
                throw new InvalidOperationException(
                    $"результат получился {ByteSize.Format(bytes)}, больше лимита {targetMegabytes} МБ");

            File.Move(partPath, output);
            return output;
        }
        catch
        {
            try { File.Delete(partPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Быстро вырезать диапазон без перекодирования. Граница попадает на ближайший
    /// ключевой кадр — это ограничение stream-copy, зато качество и дорожки остаются
    /// исходными, а экспорт обычно занимает секунды.
    /// </summary>
    public static async Task<string> TrimLosslessAsync(
        string ffmpeg, string input, TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        VideoEditor.ValidateRange(start, end);
        string output = VideoEditor.CreateOutputPath(input);
        string partPath = Path.Combine(
            Path.GetDirectoryName(output)!,
            ".aura-edit-" + Guid.NewGuid().ToString("N") + ".part.mp4");

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(Seconds(start));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Seconds(end - start));
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-map_chapters"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-avoid_negative_ts"); psi.ArgumentList.Add("make_zero");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(partPath);

        Log.Info("Editor", $"Lossless-экспорт {Path.GetFileName(input)}: {start} — {end}");
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить ffmpeg");
            string errors;
            try
            {
                errors = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                string tail = string.Join(" ", errors.Split('\n').TakeLast(4)).Trim();
                throw new InvalidOperationException($"ffmpeg вернул ошибку: {tail}");
            }
            if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
                throw new InvalidOperationException("ffmpeg создал пустой файл");

            if (File.Exists(output)) output = VideoEditor.CreateOutputPath(input);
            File.Move(partPath, output);
            return output;
        }
        catch
        {
            try { File.Delete(partPath); } catch { }
            throw;
        }
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
