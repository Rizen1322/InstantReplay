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
        if (!string.IsNullOrWhiteSpace(saved) &&
            string.Equals(Path.GetFileName(saved), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
            ExecutableTrust.IsProtected(saved)) return Path.GetFullPath(saved);

        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (ExecutableTrust.IsProtected(bundled)) return bundled;

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
                    if (ExecutableTrust.IsProtected(candidate)) return Path.GetFullPath(candidate);
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (ExecutableTrust.IsProtected(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }

        foreach (string candidate in TypicalLocations())
            if (ExecutableTrust.IsProtected(candidate)) return Path.GetFullPath(candidate);

        return null;
    }

    private static IEnumerable<string> TypicalLocations()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? @"C:\ProgramData\chocolatey",
            "bin", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "scoop", "shims", "ffmpeg.exe");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> EncoderCache = new(StringComparer.OrdinalIgnoreCase);

    private static string[] EncodersOf(string ffmpeg)
    {
        ffmpeg = Path.GetFullPath(ffmpeg);
        return EncoderCache.GetOrAdd(ffmpeg, path =>
        {
            try
            {
                var psi = new ProcessStartInfo(ExecutableTrust.RequireProtected(path))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-encoders");
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить ffmpeg");
                // Читаем оба потока сразу и параллельно. Синхронный ReadToEnd до
                // WaitForExit встаёт намертво, если ffmpeg заполнил буфер трубы и
                // ждёт, пока мы его вычитаем, — классическая взаимная блокировка.
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                    // Дочитываем ограниченно: после Kill трубы закрываются, но если
                    // процесс так и не умер, ждать чтения бесконечно нельзя.
                    try { Task.WaitAll(new[] { stdout, stderr }, 2000); } catch { }
                    // Половина списка энкодеров хуже, чем никакого: пусть сработает
                    // общий catch с логом, а не выбор энкодера по обрывку ответа.
                    throw new TimeoutException("ffmpeg не ответил за 5 секунд");
                }
                // WaitForExit(int) не ждёт асинхронных чтений — строки берём из задач.
                string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                return output.Split('\n');
            }
            catch (Exception ex)
            {
                Log.Info("Ffmpeg", "Не удалось прочитать список энкодеров: " + ex.Message);
                return [];
            }
        });
    }

    private static bool Has(string ffmpeg, string encoder) =>
        EncodersOf(ffmpeg).Any(line => line.Contains(encoder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Выбрать аппаратный H.264-энкодер, который реально есть в этом ffmpeg.
    /// NVENC быстрее программного x264 в разы; при отсутствии — libx264/mpeg4.
    /// </summary>
    public static string SelectVideoEncoder(string ffmpeg)
    {
        foreach (string encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            if (Has(ffmpeg, encoder)) return encoder;
        return Has(ffmpeg, "libx264") ? "libx264" : "mpeg4";
    }

    /// <summary>Запасной программный энкодер для повторной попытки без GPU.</summary>
    public static string SelectSoftwareEncoder(string ffmpeg) => Has(ffmpeg, "libx264") ? "libx264" : "mpeg4";

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

        var psi = new ProcessStartInfo(ExecutableTrust.RequireProtected(ffmpeg))
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(ffmpeg)),
            Environment = { ["PATH"] = Path.GetDirectoryName(Path.GetFullPath(ffmpeg)) + Path.PathSeparator + System.Environment.SystemDirectory },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-hide_banner");
        // ffmpeg по умолчанию слушает stdin (клавиша «q» и прочее). У процесса без
        // консоли stdin — унаследованный дескриптор приложения, и чтение из него
        // может встать намертво: ffmpeg ждёт ввода, мы ждём ffmpeg.
        psi.ArgumentList.Add("-nostdin");
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
    /// Быстро вырезать диапазон без перекодирования видео. Граница попадает на
    /// ближайший ключевой кадр — это ограничение stream-copy. Одна выбранная
    /// аудиодорожка тоже копируется; режим «вместе» сводит дорожки в AAC.
    /// </summary>
    public static async Task<string> TrimLosslessAsync(
        string ffmpeg, string input, TimeSpan start, TimeSpan end,
        int audioTrackIndex = -1, int audioTrackCount = 1, CancellationToken ct = default)
    {
        VideoEditor.ValidateRange(start, end);
        string output = VideoEditor.CreateOutputPath(input);
        string partPath = Path.Combine(
            Path.GetDirectoryName(output)!,
            ".aura-edit-" + Guid.NewGuid().ToString("N") + ".part.mp4");

        var psi = new ProcessStartInfo(ExecutableTrust.RequireProtected(ffmpeg))
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(ffmpeg)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-hide_banner");
        // ffmpeg по умолчанию слушает stdin (клавиша «q» и прочее). У процесса без
        // консоли stdin — унаследованный дескриптор приложения, и чтение из него
        // может встать намертво: ffmpeg ждёт ввода, мы ждём ffmpeg.
        psi.ArgumentList.Add("-nostdin");
        // Копия видео может начаться только с ключевого кадра, и ffmpeg с -ss перед
        // входом берёт его ДО точки старта, а звук режет ровно по точке: в начале
        // фрагмента была секунда-другая картинки без звука. Поэтому старт ставим
        // на сам ключевой кадр (чуть после него, чтобы поиск попал именно в него),
        // и звук идёт с того же места.
        TimeSpan from = start;
        if (Saving.Mp4.Mp4Keyframes.StartFor(Saving.Mp4.Mp4Keyframes.Read(input),
                start.TotalSeconds, end.TotalSeconds) is double key)
            from = TimeSpan.FromSeconds(key) + TimeSpan.FromMilliseconds(1);
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(Seconds(from));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Seconds(end - from));

        bool mixAudio = audioTrackIndex < 0 && audioTrackCount > 1;
        if (mixAudio)
        {
            string inputs = string.Concat(Enumerable.Range(0, audioTrackCount).Select(i => $"[0:a:{i}]"));
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(MixFilter(inputs, audioTrackCount));
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[aout]");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy");
            // Сведение дорожек математически требует нового аудиопотока. Видео —
            // самая тяжёлая часть — по-прежнему копируется бит-в-бит.
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("256k");
        }
        else
        {
            int selected = Math.Max(0, audioTrackIndex);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"0:a:{selected}?");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        }
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-map_chapters"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-avoid_negative_ts"); psi.ArgumentList.Add("make_zero");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(partPath);

        Log.Info("Editor", $"Быстрый экспорт {Path.GetFileName(input)}: {start} — {end} " +
                           $"(с ключевого кадра {from}), audio={audioTrackIndex}");
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

    /// <summary>Свести все дорожки стороннего видео в один m4a для предпросмотра в редакторе.</summary>
    public static async Task MixAudioAsync(string ffmpeg, string input, string output, int tracks, CancellationToken ct)
    {
        if (tracks < 2) throw new ArgumentOutOfRangeException(nameof(tracks));
        if (File.Exists(output)) throw new IOException("Звуковой файл уже существует.");
        string part = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".aura-audio-{Guid.NewGuid():N}.m4a");
        ffmpeg = ExecutableTrust.RequireProtected(ffmpeg);
        var psi = new ProcessStartInfo(ffmpeg)
        {
            WorkingDirectory = Path.GetDirectoryName(ffmpeg), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true,
            Environment = { ["PATH"] = Path.GetDirectoryName(ffmpeg) + Path.PathSeparator + System.Environment.SystemDirectory }
        };
        string labels = string.Concat(Enumerable.Range(0, tracks).Select(i => $"[0:a:{i}]"));
        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-n", "-i", input, "-filter_complex", MixFilter(labels, tracks),
                     "-map", "[aout]", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k", "-f", "ipod", part })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
            string error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.Join(" ", error.Split('\n').TakeLast(4)));
            ct.ThrowIfCancellationRequested();
            File.Move(part, output);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await errors; } catch (IOException) { }
            throw;
        }
        finally { try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Сведение дорожек в одну без потери громкости, но и без перегруза.
    ///
    /// amix с normalize=0 просто складывает дорожки. Запись Aura держит каждую
    /// дорожку у самого потолка (лимитер при захвате), поэтому громкий момент игры
    /// плюс голос в микрофон давали в сумме до +3 дБ сверх нуля: при воспроизведении
    /// всё выше срезалось, и в редакторе хрипели и игра, и голос, хотя в самом файле
    /// дорожки чистые. Лимитер после сведения мягко прижимает только такие пики.
    /// </summary>
    internal static string MixFilter(string inputs, int trackCount) =>
        $"{inputs}amix=inputs={trackCount}:duration=longest:normalize=0," +
        "alimiter=limit=0.97:attack=5:release=50:level=disabled[aout]";

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
