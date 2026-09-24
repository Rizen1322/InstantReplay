using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Saving.Mp4;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Saving;

/// <summary>
/// Сохранение повтора в MP4: снимок буфера (готовое видео и готовый AAC) просто
/// раскладывается в файл своим мультиплексором (<see cref="Mp4ProgressiveWriter"/>).
///
/// Здесь нет ни одного кодека и ни одного объекта Media Foundation: раньше звук
/// перекодировался в AAC прямо во время сохранения, писатель MF держал сэмплы,
/// арену приходилось закреплять, а темп подачи — сдерживать через
/// недокументированную статистику писателя. Теперь сохранение — это чтение
/// памяти и последовательная запись на диск.
/// </summary>
public static class ReplaySaver
{
    /// <summary>Какие дорожки кладём в файл при данном режиме и том, что есть в снимке.</summary>
    public static List<AudioTrackKind> TracksFor(AudioTrackMode mode, bool hasGame, bool hasMic)
    {
        bool wantGame = hasGame && mode is not AudioTrackMode.MicOnly;
        bool wantMic = hasMic && mode is not AudioTrackMode.GameOnly;
        if (!wantGame && !wantMic) return [];
        if (mode == AudioTrackMode.Separate && wantGame && wantMic)
            return [AudioTrackKind.Game, AudioTrackKind.Mic];
        return [!wantMic ? AudioTrackKind.Game
              : !wantGame ? AudioTrackKind.Mic
              : AudioTrackKind.Mixed];
    }

    /// <summary>
    /// Записать клип. Пишется в соседний «.part» и переименовывается только после
    /// успешной записи: незавершённый файл не носит имени клипа и не попадает в
    /// библиотеку. Возвращает путь опубликованного файла.
    /// </summary>
    public static string Save(
        string filePath,
        VideoSnapshot video,
        Mp4VideoFormat videoFormat,
        AudioSnapshot audio,
        IReadOnlyList<(AudioTrackKind Kind, Mp4AudioFormat Format)> audioTracks,
        Action<double>? progress = null,
        bool gentleIo = false)
    {
        if (video.Count == 0) throw new InvalidOperationException("Видеобуфер пуст");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long baseTicks = video[0].PtsTicks;
        double clipSeconds = (video[video.Count - 1].PtsTicks - baseTicks) / 10_000_000.0;

        var inputs = new List<Mp4AudioInput>();
        int audioFrames = 0;
        foreach (var (kind, format) in audioTracks)
        {
            var track = audio[kind];
            if (track is null || track.Frames.Count == 0) continue;
            inputs.Add(new Mp4AudioInput(format, track.Frames, track.FirstPtsTicks - baseTicks));
            audioFrames += track.Frames.Count;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string partPath = filePath + ".part";
        long fileBytes;
        bool written = false;
        try
        {
            using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                                             1 << 20, FileOptions.SequentialScan))
            using (var stream = new GentleWriteStream(file, gentleIo))
            {
                fileBytes = Mp4ProgressiveWriter.Write(stream, videoFormat, new SnapshotSource(video),
                                                       inputs, progress);
                stream.Flush();
            }
            written = true;
        }
        catch (IOException ex) when (DiskFull(ex))
        {
            throw new InsufficientDiskSpaceException(
                $"Клип не поместился на диск: закончилось место в {Path.GetPathRoot(filePath)}. " +
                "Освободите место или выберите другую папку записей.");
        }
        finally
        {
            if (!written)
            {
                try { File.Delete(partPath); }
                catch (Exception ex) { Log.Warn("Saver", $"Не удалось убрать незавершённый файл: {ex.Message}"); }
            }
        }

        string published = Publish(partPath, filePath);
        long ms = sw.ElapsedMilliseconds;
        string fps = clipSeconds > 0.5 ? $", реально {video.Count / clipSeconds:F1} fps" : "";
        Log.Info("Saver", $"Сохранено: {published} ({video.Count} кадров за {clipSeconds:F1} с{fps}, " +
                          $"звук: {inputs.Count} дорожк., {audioFrames} кадров AAC); " +
                          $"{fileBytes / (1024 * 1024)} МБ за {ms} мс" +
                          (ms > 0 ? $", {fileBytes / 1024.0 / 1024 / (ms / 1000.0):F0} МБ/с" : ""));
        return published;
    }

    internal static bool DiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x70 /* ERROR_DISK_FULL */ or 0x27 /* ERROR_HANDLE_DISK_FULL */;

    private static string Publish(string partPath, string filePath)
    {
        try
        {
            File.Move(partPath, filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            string recovered = FileNaming.NextAvailablePath(filePath, File.Exists);
            try
            {
                File.Move(partPath, recovered);
                Log.Warn("Saver", $"Имя клипа оказалось занято ({ex.Message}); сохранено как {recovered}");
                return recovered;
            }
            catch (Exception recoveryEx)
            {
                Log.Error("Saver", $"Клип записан, но не опубликован: {recoveryEx.Message}. " +
                                   $"Файл остался как {partPath}");
                throw new IOException(
                    $"Клип записан, но не опубликован. Готовый файл сохранён как «{partPath}».", recoveryEx);
            }
        }
    }

    /// <summary>Кадры снимка для мультиплексора: время от начала клипа.</summary>
    private sealed class SnapshotSource(VideoSnapshot snapshot) : IMp4VideoSource
    {
        private readonly long _base = snapshot[0].PtsTicks;

        public int Count => snapshot.Count;
        public ReadOnlySpan<byte> Data(int index) => snapshot[index].Span;
        public long Pts(int index) => snapshot[index].PtsTicks - _base;
        public long Dts(int index) => snapshot[index].DtsTicks - _base;
        public bool IsKeyframe(int index) => snapshot[index].IsKeyframe;
        public long LastDuration => Math.Max(1, snapshot[snapshot.Count - 1].DurationTicks);
    }

    /// <summary>
    /// Запись с пониженным приоритетом ввода-вывода, пока идёт игра.
    ///
    /// Залп в сотни мегабайт на диск не должен отбирать ввод-вывод у игры, поэтому
    /// поток сохранения уходит в фоновый режим Windows. Но если запись затянулась
    /// (под нагрузкой фоновый приоритет может растянуть её в разы), режим снимается
    /// и файл дописывается в полную силу: клип важнее пары секунд чужой подгрузки.
    /// Режим снимается и в Dispose — поток вернётся в пул без него.
    /// </summary>
    private sealed class GentleWriteStream : Stream
    {
        private const long BudgetMs = 1500;

        private readonly Stream _inner;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private bool _gentle;

        public GentleWriteStream(Stream inner, bool gentle)
        {
            _inner = inner;
            _gentle = BackgroundIoScope.EnterIf(gentle);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            if (_gentle && _clock.ElapsedMilliseconds > BudgetMs)
            {
                _gentle = false;
                if (BackgroundIoScope.ReleaseForCurrentThread())
                    Log.Info("Saver", $"Запись затянулась ({_clock.ElapsedMilliseconds} мс) — " +
                                      "фоновый режим ввода-вывода снят, дописываем в полную силу");
            }
        }

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing) BackgroundIoScope.ReleaseForCurrentThread();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
