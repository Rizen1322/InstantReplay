using System.Buffers;
using System.Collections.Concurrent;
using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Saving.Mp4;
using Aura.Core.Storage;

namespace Aura.Core.Saving;

/// <summary>
/// Обычная запись в файл («Начать запись»): подписывается на те же сжатые кадры
/// видео и готовые кадры AAC, что и кольцевой буфер, и пишет их во фрагментированный
/// MP4 на лету. RAM не расходуется — данные сразу уходят на диск.
///
/// ПРАВИЛА, выведенные из разбора «записал 10 минут — файла нет»:
///
/// 1. Ни одной записи на диск в потоках конвейера. Колбэки энкодера и микшера только
///    кладут копию данных в очередь, а файл пишет свой поток. Медленный диск не
///    останавливает ни выдачу кадров энкодером, ни звук.
///
/// 2. Файл фрагментированный (<see cref="FragmentedMp4Writer"/>): каждые две секунды
///    дописывается самодостаточный фрагмент. Упал процесс, пропало питание — файл
///    играется до последнего целого фрагмента. Предела в 4 ГБ, из-за которого
///    раньше запись резалась на части, у такого файла нет.
///
/// 3. Результат ПРОВЕРЯЕТСЯ: «сохранено» показывается только если файл закрыт и не пуст.
/// </summary>
public sealed class ManualRecorder : IDisposable
{
    public sealed record Result(bool Ok, int Seconds, string? Error, IReadOnlyList<string> Files);

    private const string PartSuffix = ".part";

    /// <summary>
    /// Ёмкость очереди писателя, в элементах. Это около пяти секунд видео и звука:
    /// хватает пережить подвисание диска, но не копить сотни мегабайт.
    /// </summary>
    private const int QueueCapacity = 1024;

    private readonly BlockingCollection<Item> _queue = new(QueueCapacity);
    private readonly Thread _writerThread;
    private readonly Func<byte[], Mp4VideoFormat> _videoFormat;
    private readonly IReadOnlyList<(AudioTrackKind Kind, Mp4AudioFormat Format)> _audio;
    private readonly string _filePath;
    private string _publishedPath;
    private readonly long _frameDurationTicks;

    private long _baseTicks = -1;
    private volatile bool _finished;
    private volatile string? _error;
    private long _droppedFrames;
    private volatile bool _droppingUntilKeyframe;

    private FileStream? _file;
    private FragmentedMp4Writer? _writer;

    public string FilePath => _publishedPath;

    /// <summary>
    /// Запись оборвалась сама (кончилось место, отказ диска). Приходит из потока
    /// пула один раз: движок по нему останавливает запись, чтобы кнопка не
    /// показывала «идёт запись», когда в файл уже ничего не пишется.
    /// </summary>
    public event Action<string>? Faulted;

    public DateTime StartedAt { get; } = DateTime.Now;

    public System.Diagnostics.Stopwatch Elapsed { get; } = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Файл теперь один при любой длине записи.</summary>
    public int PartCount => 1;

    /// <param name="videoFormat">Описание видеодорожки по первому ключевому кадру.</param>
    /// <param name="audio">Дорожки звука, которые кладём в файл, по порядку.</param>
    public ManualRecorder(string filePath, Func<byte[], Mp4VideoFormat> videoFormat,
        IReadOnlyList<(AudioTrackKind Kind, Mp4AudioFormat Format)> audio, long frameDurationTicks)
    {
        _filePath = filePath;
        _publishedPath = filePath;
        _videoFormat = videoFormat;
        _audio = audio;
        _frameDurationTicks = frameDurationTicks;

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "ManualRecorder.Writer",
            // Ниже конвейера: писать файл важно, но не ценой кадров.
            Priority = ThreadPriority.BelowNormal
        };
        _writerThread.Start();
        Log.Info("Recorder", $"Запись в файл начата: {filePath}");
    }

    // ---------------- Приём данных из конвейера (только очередь) ----------------

    public void OnFrame(EncodedFrame f)
    {
        if (_finished || _error is not null) return;

        if (Volatile.Read(ref _baseTicks) < 0)
        {
            // Ждём первый keyframe: с него начинается и файл, и отсчёт времени.
            // Отсчёт — от момента ПОКАЗА кадра, а не декодирования: писатель
            // начинает показ видео с первого кадра (edts), и звук должен ложиться
            // на ту же шкалу. От DTS звук с B-кадрами шёл бы раньше картинки на
            // их задержку.
            if (!f.IsKeyframe) return;
            Volatile.Write(ref _baseTicks, f.PtsTicks);
        }

        // Потеряв кадр, пропускаем остаток группы до следующего ключевого: кадры
        // между ключевыми описаны разницей с предыдущими, и без пропущенного плеер
        // показывал бы рассыпающуюся картинку две секунды.
        if (_droppingUntilKeyframe)
        {
            if (!f.IsKeyframe)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }
            _droppingUntilKeyframe = false;
        }

        // Буфер кадра живёт только на время события энкодера — копируем себе.
        var copy = ArrayPool<byte>.Shared.Rent(f.Length);
        Buffer.BlockCopy(f.Data, f.Offset, copy, 0, f.Length);
        if (!Enqueue(new Item(copy, f.Length, f.PtsTicks - _baseTicks, f.Dts - _baseTicks, -1, f.IsKeyframe)))
            _droppingUntilKeyframe = true;
    }

    public void OnAudio(AudioTrackKind kind, ReadOnlySpan<byte> frame, long ptsTicks)
    {
        if (_finished || _error is not null) return;
        long baseTicks = Volatile.Read(ref _baseTicks);
        if (baseTicks < 0) return;             // видео ещё не началось — звуку не от чего считать

        int track = -1;
        for (int i = 0; i < _audio.Count; i++)
            if (_audio[i].Kind == kind) { track = i; break; }
        if (track < 0) return;

        var copy = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(copy);
        Enqueue(new Item(copy, frame.Length, ptsTicks - baseTicks, 0, track, false));
    }

    private bool Enqueue(Item item, int timeoutMs = 0)
    {
        // В колбэке не ждём вообще: подвесить поток энкодера или микшера из-за
        // медленного диска — ровно та беда, от которой мы уходим.
        //
        // TryAdd ОБЯЗАН быть под try: между проверкой IsAddingCompleted и самим
        // вызовом писатель успевает выполнить CompleteAdding, и тогда TryAdd бросает.
        bool queued = false;
        try
        {
            queued = !_queue.IsAddingCompleted && _queue.TryAdd(item, timeoutMs);
        }
        catch (InvalidOperationException)
        {
            // Очередь закрыли прямо сейчас — записывать больше некуда, и это норма
        }

        if (queued) return true;

        ArrayPool<byte>.Shared.Return(item.Buffer);
        if (Interlocked.Increment(ref _droppedFrames) is 1 or 100 or 1000)
            Log.Warn("Recorder", $"Диск не успевает: очередь писателя переполнена, " +
                                 $"потеряно {Interlocked.Read(ref _droppedFrames)} сэмплов");
        return false;
    }

    // ---------------- Поток писателя ----------------

    private void WriterLoop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (_error is null) Write(item);
                }
                catch (IOException ex) when (ReplaySaver.DiskFull(ex))
                {
                    Fail("на диске закончилось место — запись остановлена, записанное сохранено");
                    Log.Error("Recorder", ex);
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                    Log.Error("Recorder", ex);
                }
                finally { ArrayPool<byte>.Shared.Return(item.Buffer); }
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Log.Error("Recorder", ex);
        }
        finally
        {
            Close();
        }
    }

    private void Write(Item item)
    {
        var data = item.Buffer.AsSpan(0, item.Length);
        if (item.Stream < 0)
        {
            if (_writer is null)
            {
                // Файл создаём на первом ключевом кадре: к нему энкодер уже отдал
                // заголовки кодека, из которых собирается описание дорожки.
                if (!item.Keyframe) return;
                Open(data.ToArray());
            }
            _writer!.WriteVideo(data, item.Pts, item.Dts, item.Keyframe);
        }
        else
        {
            _writer?.WriteAudio(item.Stream, data, item.Pts);
        }
    }

    private void Open(byte[] keyframe)
    {
        Mp4VideoFormat format = _videoFormat(keyframe);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _file = new FileStream(_filePath + PartSuffix, FileMode.Create, FileAccess.ReadWrite,
                               FileShare.Read, 1 << 20);
        _writer = new FragmentedMp4Writer(_file, format, _audio.Select(a => a.Format).ToList());
        Log.Info("Recorder", $"Контейнер: фрагментированный MP4, {format.Codec} {format.Width}x{format.Height}, " +
                             $"дорожек звука {_audio.Count}");
    }

    /// <summary>Дописать хвост, закрыть файл и опубликовать его под настоящим именем.</summary>
    private void Close()
    {
        var writer = _writer;
        var file = _file;
        _writer = null;
        _file = null;
        if (writer is null || file is null) return;

        bool closed = false;
        long bytes = 0;
        try
        {
            writer.Finish(_frameDurationTicks);
            file.Flush(flushToDisk: true);
            bytes = file.Length;
            closed = true;
        }
        catch (Exception ex)
        {
            // Фрагменты, которые уже на диске, играются и без хвоста: файл оставляем.
            Log.Error("Recorder", $"Хвост записи не дописался: {ex.Message}");
            try { bytes = file.Length; } catch { }
        }
        finally
        {
            writer.Dispose();
            file.Dispose();
        }

        if (bytes < 1024)
        {
            Fail("файл записи пуст");
            try { File.Delete(_filePath + PartSuffix); } catch { }
            return;
        }

        try
        {
            // Проверяем только готовые файлы: NextAvailablePath считает занятым и
            // имя с «.part», то есть наш собственный недописанный файл.
            _publishedPath = File.Exists(_filePath)
                ? FileNaming.NextAvailablePath(_filePath, candidate => File.Exists(candidate))
                : _filePath;
            File.Move(_filePath + PartSuffix, _publishedPath);
            Log.Info("Recorder", $"Файл закрыт{(closed ? "" : " без хвоста")}: {Path.GetFileName(_publishedPath)}, " +
                                 $"{bytes / (1024 * 1024)} МБ");
        }
        catch (Exception ex)
        {
            _publishedPath = _filePath + PartSuffix;
            Fail($"запись сохранена как «{_publishedPath}», переименовать не удалось: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        if (_error is not null) return;
        _error = message;
        if (!_finished)
        {
            var handler = Faulted;
            if (handler is not null) ThreadPool.QueueUserWorkItem(_ => handler(message));
        }
    }

    // ---------------- Завершение ----------------

    public Result Finish()
    {
        if (_finished) return new Result(_error is null, Seconds, _error, [FilePath]);
        _finished = true;

        _queue.CompleteAdding();
        if (!_writerThread.Join(TimeSpan.FromSeconds(60)))
            Fail("писатель не закончил за 60 секунд");

        int seconds = Seconds;
        bool exists = File.Exists(FilePath);
        if (_error is null)
            Log.Info("Recorder", $"Запись завершена: {FilePath} ({seconds} сек)");
        else
            Log.Error("Recorder", $"Запись завершилась с ошибкой ({_error}): {FilePath}");

        // Кончилось место посреди записи — записанное всё равно целое и открывается:
        // отдаём его как сохранённое, но с пояснением.
        return new Result(exists && (_error is null || _error.StartsWith("на диске", StringComparison.Ordinal)),
                          seconds, _error, exists ? [FilePath] : []);
    }

    private int Seconds => (int)Math.Round(Elapsed.Elapsed.TotalSeconds);

    public void Dispose()
    {
        Finish();
        // Писатель, не успевший за минуту, ещё читает очередь — освобождать её под ним нельзя.
        if (!_writerThread.IsAlive) _queue.Dispose();
    }

    private readonly struct Item(byte[] buffer, int length, long pts, long dts, int stream, bool keyframe)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
        public readonly long Pts = pts;
        public readonly long Dts = dts;
        public readonly int Stream = stream;
        public readonly bool Keyframe = keyframe;
    }
}
