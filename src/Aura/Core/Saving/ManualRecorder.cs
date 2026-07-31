using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Saving;

/// <summary>
/// Обычная запись в файл («Начать запись»): подписывается на те же сжатые кадры
/// и аудиоблоки, что и кольцевой буфер, но пишет их в MP4 на лету через SinkWriter.
/// RAM не расходуется — данные сразу уходят на диск.
///
/// ТРИ ПРАВИЛА, выведенные из разбора «записал 10 минут — файла нет»:
///
/// 1. Ни одного вызова Media Foundation в потоках конвейера. Раньше OnFrame звался
///    из event-потока энкодера, а OnAudio — из микшера (Priority=Highest) каждые
///    10 мс, и оба лезли в SinkWriter под общим локом. Любая задержка диска
///    останавливала выдачу кадров энкодером и подачу звука. Теперь колбэки только
///    кладут данные в очередь, а всю работу с MF делает свой поток.
///
/// 2. Звук пишется кусками по секунде, а не блоками по 10 мс. Скорость финализации
///    MP4 определяется ЧИСЛОМ сэмплов: на 10 минутах блоки по 10 мс дают 120 000
///    аудиосэмплов на две дорожки против 36 000 видеокадров, и каждый из них —
///    отдельный вызов AAC-энкодера в реальном времени. Ровно так же пишет ReplaySaver.
///
/// 3. Файл режется на части, не доходя до 4 ГБ (см. MaxSegmentBytes), и результат
///    ПРОВЕРЯЕТСЯ: ошибка Finalize больше не проглатывается, «сохранено» показывается
///    только если файл реально закрыт и не пуст.
/// </summary>
public sealed class ManualRecorder : IDisposable
{
    /// <summary>Итог записи: успех, длительность, файлы (частей может быть несколько).</summary>
    public sealed record Result(bool Ok, int Seconds, string? Error, IReadOnlyList<string> Files);

    /// <summary>
    /// Порог разрезания файла на части.
    ///
    /// ВРЕМЕННО ОТКЛЮЧЁН ради опыта. Причина деления — подозрение, что MP4-писатель
    /// Media Foundation не умеет файлы больше 4 ГБ: в разобранном файле на 1 ГБ он
    /// использует 32-битные формы (заголовок mdat и таблицы stco), а документация
    /// молчит о том, переключается ли он на 64-битные (largesize и co64), когда
    /// файл перерастает предел. Ровно на этот предел приходился исходный симптом
    /// «записал минут десять — файла нет»: при 50 Мбит/с 4 ГБ набегают на 11-й минуте.
    ///
    /// Опыт: записать больше 4 ГБ одним куском и разобрать результат. Целый файл —
    /// деление убираем совсем, битый — переходим на свой мультиплексор.
    /// Вернуть рабочее значение: 3_500L * 1024 * 1024.
    /// </summary>
    private const long MaxSegmentBytes = long.MaxValue;

    /// <summary>Глубина очереди к писателю: ~8 секунд видео. Дальше лучше дропнуть.</summary>
    private const int QueueCapacity = 512;

    /// <summary>Сколько 10-мс блоков копим в один аудиосэмпл (1 секунда).</summary>
    private const int BlocksPerChunk = 100;

    private readonly BlockingCollection<Item> _queue = new(QueueCapacity);
    private readonly Thread _writerThread;
    private readonly Func<IMFMediaType?> _videoType;
    private readonly AudioTrackMode _trackMode;
    private readonly bool _hasGame, _hasMic;
    private readonly string _firstFilePath;
    private readonly List<string> _files = [];

    private readonly object _audioSync = new();
    private List<(int Index, Func<AudioBlock, float[]> Selector)> _audioStreams = [];
    private float[][] _chunkBuf = [];
    private int[] _chunkFill = [];
    private long[] _chunkStart = [];
    private int _blockSamples;

    private long _baseTicks = -1;
    private volatile bool _finished;
    private volatile string? _error;
    private long _droppedFrames;

    public string FilePath => _files.Count > 0 ? _files[0] : _firstFilePath;
    public DateTime StartedAt { get; } = DateTime.Now;
    /// <summary>Сколько частей файла уже создано (для показа в интерфейсе).</summary>
    public int PartCount { get { lock (_files) return _files.Count; } }

    public ManualRecorder(string filePath, Func<IMFMediaType?> videoType,
        AudioTrackMode trackMode, bool hasGame, bool hasMic)
    {
        _firstFilePath = filePath;
        _videoType = videoType;
        _trackMode = trackMode;
        _hasGame = hasGame;
        _hasMic = hasMic;

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

    // ---------------- Приём данных из конвейера (только очередь, никакого MF) ----------------

    public void OnFrame(EncodedFrame f)
    {
        if (_finished || _error is not null) return;

        if (Volatile.Read(ref _baseTicks) < 0)
        {
            // Ждём первый keyframe: с него начинается и файл, и отсчёт времени.
            if (!f.IsKeyframe) return;
            Volatile.Write(ref _baseTicks, f.PtsTicks);
        }

        // Буфер кадра живёт только на время события энкодера — копируем себе
        // (100 КБ на кадр, 6 МБ/с). Копия короткоживущая: писатель вернёт её в пул
        // сразу после WriteSample, поэтому пул держит лишь несколько массивов.
        var copy = ArrayPool<byte>.Shared.Rent(f.Length);
        Buffer.BlockCopy(f.Data, f.Offset, copy, 0, f.Length);
        Enqueue(new Item(copy, f.Length, f.PtsTicks - _baseTicks, f.DurationTicks, -1, f.IsKeyframe));
    }

    public void OnAudio(AudioBlock block)
    {
        if (_finished || _error is not null) return;
        long baseTicks = Volatile.Read(ref _baseTicks);
        if (baseTicks < 0) return;             // видео ещё не началось — звуку не от чего считать
        long pts = block.PtsTicks - baseTicks;
        if (pts < 0) return;

        // Готовые куски кладём в очередь ПОСЛЕ выхода из лока: внутри Enqueue есть
        // ожидание места, а писатель под тем же локом открывает новую часть файла.
        List<Item>? ready = null;
        lock (_audioSync)
        {
            if (_chunkBuf.Length == 0) return; // писатель ещё не создал дорожки
            for (int s = 0; s < _chunkBuf.Length; s++)
            {
                if (_chunkFill[s] == 0) _chunkStart[s] = pts;
                _audioStreams[s].Selector(block).CopyTo(_chunkBuf[s].AsSpan(_chunkFill[s] * _blockSamples));
                if (++_chunkFill[s] >= BlocksPerChunk)
                    (ready ??= []).Add(TakeChunk(s));
            }
        }
        if (ready is not null) foreach (var item in ready) Enqueue(item);
    }

    /// <summary>Забрать накопленный кусок дорожки как элемент очереди. Вызывать под _audioSync.</summary>
    private Item TakeChunk(int s)
    {
        int fill = _chunkFill[s];
        _chunkFill[s] = 0;

        int floats = fill * _blockSamples;
        int byteCount = floats * sizeof(float);
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        MemoryMarshal.AsBytes<float>(_chunkBuf[s].AsSpan(0, floats)).CopyTo(bytes);
        return new Item(bytes, byteCount, _chunkStart[s], fill * 100_000L, s, false);
    }

    private void Enqueue(Item item)
    {
        // Ждём место в очереди недолго: подвесить поток энкодера или микшера
        // из-за медленного диска — ровно та беда, от которой мы уходим.
        if (_queue.IsAddingCompleted || !_queue.TryAdd(item, 250))
        {
            ArrayPool<byte>.Shared.Return(item.Buffer);
            if (Interlocked.Increment(ref _droppedFrames) is 1 or 100 or 1000)
                Log.Warn("Recorder", $"Диск не успевает: очередь писателя переполнена, " +
                                     $"потеряно {Interlocked.Read(ref _droppedFrames)} сэмплов");
        }
    }

    // ---------------- Поток писателя: единственный, кто трогает Media Foundation ----------------

    private IMFSinkWriter? _writer;
    private int _videoStream = -1;
    private long _segmentBytes;
    private long _segmentBase;   // pts начала текущей части (нули каждого файла)
    private int _segmentIndex;

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
                catch (Exception ex)
                {
                    Fail(ex.Message);
                    Log.Error("Recorder", ex);
                }
                finally { ArrayPool<byte>.Shared.Return(item.Buffer); }
            }
            FinalizeSegment();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Log.Error("Recorder", ex);
        }
    }

    private void Write(Item item)
    {
        if (item.Stream < 0)
        {
            // Файл создаём на первом keyframe: к этому моменту энкодер уже отдал
            // выходной тип С ЗАГОЛОВКАМИ кодека (SPS/PPS/VPS). Взяв тип раньше,
            // мы получали Finalize с MF_E_SINK_HEADERS_NOT_FOUND — файл не собирался.
            if (_writer is null)
            {
                if (!item.Keyframe) return;
                OpenSegment(item.Pts);
                if (_writer is null) return;
            }
            // Резать можно только по keyframe — иначе часть начнётся с «каши».
            else if (item.Keyframe && _segmentBytes >= MaxSegmentBytes)
            {
                FinalizeSegment();
                OpenSegment(item.Pts);
                if (_writer is null) return;
            }

            long pts = item.Pts - _segmentBase;
            if (pts < 0) return;
            using var sample = MfMp4Writer.CreateSample(item.Buffer, 0, item.Length, pts, item.Duration);
            if (item.Keyframe) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
            _writer.WriteSample(_videoStream, sample);
        }
        else
        {
            if (_writer is null || item.Stream >= _audioStreams.Count) return;
            long pts = item.Pts - _segmentBase;
            if (pts < 0) return;
            using var sample = MfMp4Writer.CreateSample(item.Buffer, 0, item.Length, pts, item.Duration);
            _writer.WriteSample(_audioStreams[item.Stream].Index, sample);
        }
        long before = _segmentBytes;
        _segmentBytes += item.Length;
        // Отметка перехода через 4 ГиБ — граница 32-битной адресации в MP4.
        // Нужна для опыта: по ней видно, в какой момент писатель либо перешёл
        // на 64-битные заголовки, либо начал портить файл.
        const long FourGiB = 4L * 1024 * 1024 * 1024;
        if (before < FourGiB && _segmentBytes >= FourGiB)
            Log.Info("Recorder", "Файл перешёл границу 4 ГиБ — дальше проверяем, справится ли MP4-писатель");
    }

    private void OpenSegment(long ptsBase)
    {
        string path = _segmentIndex == 0 ? _firstFilePath : PartPath(_firstFilePath, _segmentIndex + 1);
        var videoType = _videoType();
        if (videoType is null) { Fail("энкодер не отдал тип видеопотока"); return; }

        var writer = MfMp4Writer.Create(path);
        int videoStream = MfMp4Writer.AddPassthroughVideoStream(writer, videoType);
        var audioStreams = MfMp4Writer.AddAudioStreams(writer, _trackMode, _hasGame, _hasMic);
        writer.BeginWriting();

        _writer = writer;
        _videoStream = videoStream;
        _segmentBytes = 0;
        _segmentBase = ptsBase;
        _segmentIndex++;
        lock (_files) _files.Add(path);

        lock (_audioSync)
        {
            _audioStreams = audioStreams;
            if (_chunkBuf.Length != audioStreams.Count)
            {
                _blockSamples = AudioBlockSamples;
                _chunkBuf = new float[audioStreams.Count][];
                _chunkFill = new int[audioStreams.Count];
                _chunkStart = new long[audioStreams.Count];
                for (int s = 0; s < audioStreams.Count; s++)
                    _chunkBuf[s] = new float[BlocksPerChunk * _blockSamples];
            }
            else Array.Clear(_chunkFill); // копившееся для прошлой части не тащим в новую
        }

        if (_segmentIndex > 1) Log.Info("Recorder", $"Файл достиг предела MP4 — продолжаю в {Path.GetFileName(path)}");
    }

    /// <summary>10 мс × 48 кГц × 2 канала — размер одного блока микшера в сэмплах.</summary>
    private const int AudioBlockSamples = MfMp4Writer.SampleRate / 100 * MfMp4Writer.Channels;

    private void FinalizeSegment()
    {
        var writer = _writer;
        if (writer is null) return;
        _writer = null;

        string path = _files.Count > 0 ? _files[^1] : _firstFilePath;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            writer.Finalize();
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { }
            if (bytes < 1024) Fail($"файл {Path.GetFileName(path)} пуст после финализации");
            else Log.Info("Recorder", $"Часть закрыта: {Path.GetFileName(path)}, " +
                                      $"{bytes / (1024 * 1024)} МБ, финализация {sw.ElapsedMilliseconds} мс");
        }
        catch (Exception ex)
        {
            // Незакрытый MP4 (нет moov) не откроет ни один плеер — говорим об этом прямо,
            // а не рисуем «сохранено».
            Fail(ex.Message);
            Log.Error("Recorder", $"Финализация {Path.GetFileName(path)} не удалась: {ex}");
        }
        finally { writer.Dispose(); }
    }

    private void Fail(string message) => _error ??= message;

    private static string PartPath(string path, int part)
    {
        string dir = Path.GetDirectoryName(path)!;
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} (часть {part}){Path.GetExtension(path)}");
    }

    // ---------------- Завершение ----------------

    /// <summary>
    /// Закрывает файл(ы) и возвращает честный итог. Блокирует вызывающего до конца
    /// записи хвоста очереди — вызывать из фонового потока, не из UI.
    /// </summary>
    public Result Finish()
    {
        if (_finished) return new Result(_error is null, Seconds, _error, Files);
        _finished = true;

        var tail = new List<Item>();
        lock (_audioSync)
            for (int s = 0; s < _chunkFill.Length; s++)
                if (_chunkFill[s] > 0) tail.Add(TakeChunk(s));
        foreach (var item in tail) Enqueue(item);

        _queue.CompleteAdding();
        if (!_writerThread.Join(TimeSpan.FromSeconds(60)))
            Fail("писатель не закончил за 60 секунд");

        int seconds = Seconds;
        var files = Files;
        if (_error is null)
            Log.Info("Recorder", $"Запись завершена: {FilePath} ({seconds} сек, частей {files.Count})");
        else
            Log.Error("Recorder", $"Запись НЕ сохранена ({_error}): {FilePath}");

        return new Result(_error is null && files.Count > 0, seconds, _error, files);
    }

    private int Seconds => (int)Math.Round((DateTime.Now - StartedAt).TotalSeconds);
    private IReadOnlyList<string> Files { get { lock (_files) return _files.ToArray(); } }

    public void Dispose()
    {
        Finish();
        _queue.Dispose();
    }

    /// <summary>Единица очереди: буфер из пула + куда и когда его писать.</summary>
    private readonly struct Item(byte[] buffer, int length, long pts, long duration, int stream, bool keyframe)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
        public readonly long Pts = pts;
        public readonly long Duration = duration;
        /// <summary>-1 — видео, иначе индекс аудиодорожки.</summary>
        public readonly int Stream = stream;
        public readonly bool Keyframe = keyframe;
    }
}
