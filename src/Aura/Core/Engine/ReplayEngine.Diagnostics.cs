using Vortice.MediaFoundation;
using Aura.Core.Audio;
using Aura.Core.Buffering;
using Aura.Core.Capture;
using Aura.Core.Encoding;
using Aura.Core.GameDetection;
using Aura.Core.Logging;
using Aura.Core.Saving;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Engine;

/// <summary>
/// Часть <see cref="ReplayEngine"/>. Диагностика: поминутная сводка конвейера, память процесса, посекундная
/// диагностика провалов и вотчдог тишины захвата. Всё здесь только ЧИТАЕТ состояние
/// конвейера — кроме вотчдога и пробы, которые при доказанном голодании захвата
/// сами запускают пересборку через OnCaptureFailed.
///
/// Движок разнесён по файлам по смыслу, без изменения поведения: те же поля, те же
/// замки, те же методы. Раньше это был один файл на 2200 строк, где сохранение,
/// диагностика и восстановление захвата шли вперемешку.
/// </summary>
public sealed partial class ReplayEngine
{
    private long _lastNoSlot;
    private long _lastLate;

    // Раз в минуту — здоровье конвейера в лог: по этим цифрам видно, ГДЕ теряются
    // кадры (дропы очереди = не успевает энкодер; низкий submit = не успевает захват).
    private System.Threading.Timer? _statsTimer;
    private long _lastSkippedBeforeConvert;
    private long _lastSubmitted, _lastEncoded, _lastDropped, _lastDiscardedDuplicates,
                 _lastSuppressedDuplicates, _lastDuplicated, _lastReceived, _lastAccepted;
    private long _lastRequests, _lastPacerBlocked;

    private void StartStatsTimer()
    {
        _lastSubmitted = _lastEncoded = _lastDropped = _lastDiscardedDuplicates =
            _lastSuppressedDuplicates = _lastDuplicated = _lastReceived = _lastAccepted = 0;
        _lastRequests = _lastPacerBlocked = _lastSkippedBeforeConvert = 0;
        _statsWindowStart = DateTime.UtcNow;
        _statsTimer?.Dispose();
        _statsTimer = new System.Threading.Timer(_ => DumpStats("за минуту"),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private DateTime _statsWindowStart = DateTime.UtcNow;

    /// <summary>
    /// Счётчики конвейера с прошлой выгрузки в лог. Зовётся раз в минуту, а ещё
    /// при сохранении повтора и остановке буфера: короткие сеансы (записал 20 секунд,
    /// сохранил, выключил) до минутного тика не доживали, и разбирать провал fps
    /// было не по чему.
    /// </summary>
    private void DumpStats(string label)
    {
        var enc = _encoder;
        var cap = _capture;
        if (enc is null || State == EngineState.Stopped) return;

        long skipped = Interlocked.Read(ref enc.FramesSkippedBeforeConvert);
        long s = enc.FramesSubmitted, e = enc.FramesEncoded,
             d = enc.FramesDroppedRealQueue,
             discardedDuplicates = enc.FramesDiscardedDuplicates,
             suppressedDuplicates = enc.FramesSuppressedDuplicates,
             dup = enc.FramesDuplicated;
        long req = enc.InputRequests, blocked = enc.PacerBlocked;
        long rcv = cap?.FramesReceived ?? 0, acc = cap?.FramesAccepted ?? 0;
        double seconds = Math.Max((DateTime.UtcNow - _statsWindowStart).TotalSeconds, 0.001);
        if (seconds < 2) return; // только что выгружали — нечего показывать

        // fps по каждой стадии: сразу видно, кто именно не дотягивает до настроенного.
        string captureName = _captureBackend == CaptureBackend.Wgc ? "WGC" : "DDA";
        Log.Info("Engine", $"Конвейер {label} ({seconds:F0} с): {captureName} {rcv - _lastReceived}/{acc - _lastAccepted} " +
            $"(получено/принято), захвачено {s - _lastSubmitted}, " +
            $"дубликатов {dup - _lastDuplicated}, закодировано {e - _lastEncoded}, " +
            $"дропнуто реальных {d - _lastDropped}, убрано старых дублей " +
            $"{discardedDuplicates - _lastDiscardedDuplicates}, подавлено дублей " +
            $"{suppressedDuplicates - _lastSuppressedDuplicates} " +
            $"(буфер {(int)BufferedDuration.TotalSeconds} сек) | " +
            $"fps: {captureName} {(rcv - _lastReceived) / seconds:F1}, подано {(s - _lastSubmitted + dup - _lastDuplicated) / seconds:F1}, " +
            $"закодировано {(e - _lastEncoded) / seconds:F1}, запросов MFT {(req - _lastRequests) / seconds:F1}" +
            $", пресет {enc.QualityPreset}, кадров внутри MFT до {Interlocked.Exchange(ref enc.MaxInFlight, 0)}" +
            (blocked > _lastPacerBlocked ? $"; пейсер молчал {blocked - _lastPacerBlocked} раз (давление очереди/MFT)" : "") +
            (skipped > _lastSkippedBeforeConvert
                ? $"; не преобразовано на забитой очереди {skipped - _lastSkippedBeforeConvert}"
                : "") +
            (Interlocked.Exchange(ref enc.MaxPtsLeadTicks, long.MinValue) is long maxLead && maxLead != long.MinValue
                ? $"; время кадра от захвата {Interlocked.Exchange(ref enc.MinPtsLeadTicks, long.MaxValue) / 10_000.0:+0.0;-0.0}..{maxLead / 10_000.0:+0.0;-0.0} мс"
                : "") +
            (enc.FramesDroppedLate > _lastLate
                ? $"; опоздавших за повторы кадров {enc.FramesDroppedLate - _lastLate}"
                : "") +
            (enc.FramesDroppedNoSlot > _lastNoSlot
                ? $"; нет свободной текстуры пула {enc.FramesDroppedNoSlot - _lastNoSlot} раз"
                : ""));
        _lastNoSlot = enc.FramesDroppedNoSlot;
        _lastLate = enc.FramesDroppedLate;

        // Видеопамять: превышение бюджета означает вытеснение текстур в оперативную
        // память через шину, и тогда застревает всё, что трогает GPU — и захват, и
        // кодирование разом. По одним лишь fps эту причину от прочих не отличить.
        if (cap is not null && GpuInfo.Usage(cap.D3DDevice) is { } vram)
        {
            string verdict = vram.UsedMb > vram.BudgetMb ? " — БЮДЖЕТ ПРЕВЫШЕН" : "";
            Log.Info("Engine", $"Видеопамять: занято {vram.UsedMb} из {vram.BudgetMb} МБ бюджета{verdict}");

        }

        // Где именно уходит бюджет кадра (16.7 мс при 60 fps)
        string probe = Diagnostics.PipelineProbe.TakeReport();
        if (probe.Length > 0) Log.Info("Engine", probe);

        string gpu = _gpuTimer?.TakeReport() ?? "";
        if (gpu.Length > 0) Log.Info("Engine", gpu);

        LogMemory();

        _lastSubmitted = s; _lastEncoded = e; _lastDropped = d;
        _lastSkippedBeforeConvert = skipped;
        _lastDiscardedDuplicates = discardedDuplicates;
        _lastSuppressedDuplicates = suppressedDuplicates;
        _lastDuplicated = dup;
        _lastReceived = rcv; _lastAccepted = acc;
        _lastRequests = req; _lastPacerBlocked = blocked;
        _statsWindowStart = DateTime.UtcNow;
    }

    /// <summary>
    /// Вернуть память системе после остановки.
    ///
    /// Пока идёт запись, включён SustainedLowLatency — сборщик избегает блокирующих
    /// сборок второго поколения, чтобы не давать пауз в конвейере. Плата за это:
    /// массивы кадров (каждый крупнее порога больших объектов) освобождаются, но
    /// куча под них не сжимается и системе не возвращается — процесс продолжает
    /// занимать гигабайты уже после выключения буфера.
    ///
    /// После остановки торопиться некуда: сжимаем кучу больших объектов один раз.
    /// В фоне, потому что на многогигабайтной куче это заметная пауза, а зовут нас
    /// из потока интерфейса.
    /// </summary>
    private static void ReleaseMemory() => Task.Run(() =>
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            long gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long priv = self.PrivateMemorySize64;
            static string Mb(long b) => $"{b / (1024 * 1024)} МБ";
            Log.Info("Engine", $"Память возвращена: живых объектов {Mb(GC.GetTotalMemory(false))}, " +
                               $"коммит сборщика {Mb(gcCommitted)}, нативная ~{Mb(Math.Max(0, priv - gcCommitted))}, " +
                               $"частная всего {Mb(priv)}");
            Diagnostics.MemoryMap.Log("после остановки");
        }
        catch (Exception ex) { Log.Warn("Engine", $"Сжатие кучи: {ex.Message}"); }
    });

    /// <summary>
    /// Кто занимает память. Буфер видео — это занятая часть арены, звук считается
    /// по числу блоков. Разница с памятью процесса — нативная часть: текстуры D3D,
    /// внутренние буферы Media Foundation, WPF и страницы, которые Windows ещё не
    /// забрала обратно.
    /// </summary>
    private void LogMemory()
    {
        long ring = _videoBuffer.TotalBytes;
        long audio = _audioBuffer.TotalBytes;
        long heap = GC.GetTotalMemory(false);
        long working = 0, priv = 0;
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            working = self.WorkingSet64;
            priv = self.PrivateMemorySize64;
        }
        catch { }

        // Сколько памяти держит закоммиченной сам сборщик мусора. Ключевая цифра:
        // разница между ней и частной памятью процесса — это нативная часть
        // (D3D, Media Foundation, WPF, драйвер). Без этого разделения спор
        // «кто занял гигабайт» не решается.
        long gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;

        static string Mb(long bytes) => $"{bytes / (1024 * 1024)} МБ";
        // Частная память — то, что процесс закоммитил и обязан освободить сам;
        // рабочий набор (его показывает диспетчер задач) система урезает по своему
        // усмотрению, поэтому судить по нему нельзя.
        // Коммит сборщика — состояние НА МОМЕНТ ПОСЛЕДНЕЙ СБОРКИ, а не на сейчас.
        // При SustainedLowLatency блокирующие сборки второго поколения подавлены,
        // поэтому число может быть старым, и тогда «нативная» вбирает в себя рост
        // управляемой кучи. Счётчик сборок показывает, обновилось ли оно вообще.
        int gen2 = GC.CollectionCount(2);

        Log.Info("Engine", $"Память: буфер видео {Mb(ring)}, звук {Mb(audio)}, " +
                           $"живых объектов {Mb(heap)}, коммит сборщика {Mb(gcCommitted)} (сборок gen2 {gen2}), " +
                           $"нативная ~{Mb(Math.Max(0, priv - gcCommitted))}, " +
                           $"частная всего {Mb(priv)}, рабочий набор {Mb(working)}");
        LogMemoryBreakdown();
    }

    /// <summary>
    /// Подробная раскладка памяти раз в минуту: что из занятого — полезные кадры
    /// повтора, что — запас и накладные расходы, сколько держат сборщик, DLL и
    /// видеокарта. Цель — отличить «так устроено» от «есть что ужать».
    /// </summary>
    private void LogMemoryBreakdown()
    {
        static string Mb(long bytes) => $"{bytes / (1024 * 1024)} МБ";
        try
        {
            var v = _videoBuffer.GetMemoryStats();
            int seconds = (int)(_videoBuffer.MaxDurationTicks / 10_000_000);
            Log.Info("Memory",
                $"Повтор: кадры {Mb(v.PayloadBytes)} (из них последние {seconds} с — {Mb(v.NeededBytes)}, " +
                $"запас сверх длины {Mb(v.PayloadBytes - v.NeededBytes)}), с довесками кольца {Mb(v.UsedBytes)}, " +
                $"у сохраняемого снимка {Mb(v.SnapshotBytes)}; " +
                $"{(v.OnDisk ? "на диске" : "в RAM")} выдано {Mb(v.ResidentBytes)} ({v.ResidentChunks} блоков по 16 МБ), " +
                $"накладные {Mb(v.ResidentBytes - v.PayloadBytes - v.SnapshotBytes)}, резерв адресов {Mb(v.ReservedAddressBytes)}; " +
                $"пакетов {v.Packets}, ключевых {v.Keyframes}, мест в кольце записей {v.RingSlots}. " +
                $"Звук: занято {Mb(_audioBuffer.TotalBytes)} из выделенных {Mb(_audioBuffer.CapacityBytes)}");

            var gc = GC.GetGCMemoryInfo();
            var gens = gc.GenerationInfo;
            long loh = gens.Length > 3 ? gens[3].SizeAfterBytes : 0;
            long poh = gens.Length > 4 ? gens[4].SizeAfterBytes : 0;
            var map = Diagnostics.MemoryMap.Breakdown();
            int threads = 0, handles = 0;
            long working = 0, priv = 0;
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                threads = self.Threads.Count;
                handles = self.HandleCount;
                working = self.WorkingSet64;
                priv = self.PrivateMemorySize64;
            }
            catch { }
            Log.Info("Memory",
                $"Процесс: рабочий набор {Mb(working)}, частная {Mb(priv)}; закоммичено: частные области {Mb(map.Private)} " +
                $"({map.PrivateRegions} шт), образы DLL {Mb(map.Image)}, отображения {Mb(map.Mapped)}; " +
                $"сборщик: живых {Mb(GC.GetTotalMemory(false))}, куча {Mb(gc.HeapSizeBytes)}, LOH {Mb(loh)}, POH {Mb(poh)}, " +
                $"фрагментация {Mb(gc.FragmentedBytes)}, коммит {Mb(gc.TotalCommittedBytes)}; " +
                $"потоков {threads}, дескрипторов {handles}; LibVLC {(Diagnostics.MemoryMap.IsModuleLoaded("libvlc.dll") ? "загружен" : "не загружен")}");

            Log.Info("Memory",
                $"Видеокарта: {Diagnostics.GpuResourceLedger.Summary()}; кодирование — {_encoder?.ResourceSummary() ?? "нет"}");
        }
        catch (Exception ex) { Log.Warn("Memory", $"Раскладка памяти недоступна: {ex.Message}"); }
    }

    // Вотчдог захвата: если WGC замолчал надолго (монитор выключился по AFK, сон,
    // сброс драйвера) — сессия захвата может умереть насовсем. Буфер при этом жив
    // (пейсер дублирует последний кадр), но реальная картинка не вернётся сама.
    // Каждые 5 сек проверяем приток кадров; тишина >15 сек запускает общую
    // generation-safe пересборку и при необходимости смену backend.
    private System.Threading.Timer? _watchdog;
    private long _wdLastReceived = -1;
    private DateTime _wdLastActivity = DateTime.UtcNow;
    private bool _wdEpisodeLogged; // логируем только начало эпизода тишины, не каждые 15 сек
    private bool _wdSilenceLogged; // ранняя запись с обстановкой — один раз на эпизод
    private double _wdLastRate;    // кадров в секунду в последнем живом окне

    // ---------------- Посекундная диагностика провалов ----------------
    //
    // Поминутной сводки мало: она усредняет провал вместе с нормальной работой и
    // не даёт отличить три разных болезни друг от друга —
    //   A) кончается видеопамять: бюджет падает, кадры перестают приходить И кодироваться;
    //   B) молчит захват: бюджет в норме, кадры не приходят, очередь пуста;
    //   C) не тянет энкодер: кадры приходят, очередь полна, запросов MFT мало.
    // Поэтому пока идёт провал, пишем строку раз в секунду, а в норме молчим.

    private System.Threading.Timer? _probeTimer;
    private long _probeRecv, _probeEnc, _probeReq, _probeDrop, _probeDup;
    private long _probeLastTimestamp;
    private int _probeRunning;
    private bool _probeEpisode;
    private int _probeQuiet;

    /// <summary>Ниже этого числа ЗАКОДИРОВАННЫХ кадров в секунду считаем, что идёт провал.</summary>
    private const int ProbeFpsFloor = 45;

    /// <summary>
    /// Короче этого окна замер выбрасываем целиком.
    ///
    /// ЗАЧЕМ. Таймер зовут раз в секунду, но после долгой паузы процесса (просадка в
    /// подкачку, блокирующая сборка мусора) система отдаёт накопившиеся срабатывания
    /// подряд. Второе приходит через миллисекунду после первого, и деление на это
    /// окно превращает единичные приращения счётчиков в бессмыслицу. Живой пример:
    ///
    ///     23:10:36.264 [Probe] получено 4, закодировано 9, запросов 8, дублей 6
    ///     23:10:36.264 [Probe] получено 0, закодировано 0, запросов 1311, дублей 2623
    ///
    /// Во второй строке — три реальных кадра, поделённые на миллисекунду. Такая строка
    /// не просто бесполезна: она попадает ровно в тот момент, когда читают лог из-за
    /// настоящего сбоя, и уводит расследование в сторону.
    /// </summary>
    private const double ProbeMinWindowSeconds = 0.25;

    private void StartCaptureProbe()
    {
        _probeEpisode = false;
        _probeQuiet = 0;
        _probeRecv = _probeEnc = _probeReq = _probeDrop = _probeDup = 0;
        _probeLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _probeRunning, 0);
        _probeTimer?.Dispose();
        _probeTimer = new System.Threading.Timer(_ => Probe(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Probe()
    {
        // System.Threading.Timer допускает reentrancy: под нагрузкой следующий tick
        // может прийти до завершения предыдущего и дважды сдвинуть общие счётчики.
        if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0) return;
        try { ProbeCore(); }
        finally { Volatile.Write(ref _probeRunning, 0); }
    }

    private void ProbeCore()
    {
        var cap = _capture;
        var enc = _encoder;
        long generation = Interlocked.Read(ref _captureGeneration);
        CaptureBackend backend = _captureBackend;
        if (cap is null || enc is null || !_pipelineOpen || _stopRequested) return;

        try
        {
            // Длину окна меряем ПЕРВЫМ делом и на слишком коротком окне выходим,
            // НЕ тронув ни одного счётчика: иначе приращения этого миллисекундного
            // огрызка пропали бы из следующего, честного замера (см. ProbeMinWindowSeconds).
            long sampleTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double sampleSeconds = (sampleTimestamp - _probeLastTimestamp) /
                                   (double)System.Diagnostics.Stopwatch.Frequency;
            if (sampleSeconds < ProbeMinWindowSeconds) return;
            _probeLastTimestamp = sampleTimestamp;

            long recv = cap.FramesReceived, encoded = enc.FramesEncoded;
            long req = enc.InputRequests, drop = enc.FramesDroppedRealQueue, dup = enc.FramesDuplicated;

            long dRecv = recv - _probeRecv, dEnc = encoded - _probeEnc, dReq = req - _probeReq;
            long dDrop = drop - _probeDrop, dDup = dup - _probeDup;
            _probeRecv = recv; _probeEnc = encoded; _probeReq = req; _probeDrop = drop; _probeDup = dup;

            static long PerSecond(long delta, double seconds) =>
                (long)Math.Round(Math.Max(0, delta) / seconds);
            long fpsRecv = PerSecond(dRecv, sampleSeconds);
            long fpsEnc = PerSecond(dEnc, sampleSeconds);
            long fpsReq = PerSecond(dReq, sampleSeconds);
            long fpsDrop = PerSecond(dDrop, sampleSeconds);
            long fpsDup = PerSecond(dDup, sampleSeconds);

            bool gameForeground = _gameCaptureRecovery.Target is not null;
            var health = _captureHealth.Observe(new CaptureHealthSample(
                    backend,
                    _settings.Current.Fps,
                    (int)Math.Clamp(fpsRecv, 0, int.MaxValue),
                    (int)Math.Clamp(fpsEnc, 0, int.MaxValue),
                    (int)Math.Clamp(fpsDup, 0, int.MaxValue),
                    gameForeground,
                    DateTimeOffset.UtcNow - _pipelineStartedAt),
                DateTimeOffset.UtcNow);

            if (health.SwitchBackend)
            {
                string metrics = $"{health.Reason}; получено {fpsRecv}, закодировано {fpsEnc}, дублей {fpsDup} кадр/с";
                OnCaptureFailed(new CaptureFailure(
                    CaptureFailureKind.BackendStalled,
                    new InvalidOperationException("WGC capture starvation"), metrics,
                    generation, ActiveCaptureTarget()?.Revision ?? 0), generation);
                return;
            }

            // Смотрим ТОЛЬКО на кодирование: именно оно попадает в файл. Низкий
            // приток от WGC сам по себе нормален — на малоподвижной картинке система
            // отдаёт меньше кадров, а пейсер добивает сетку дубликатами, и запись
            // остаётся ровной. По прежнему порогу «или захват, или кодирование»
            // диагностика срабатывала 725 раз за день на совершенно здоровой работе
            // и топила в себе настоящие провалы.
            bool bad = fpsEnc < ProbeFpsFloor;

            // Хвост после восстановления: по нему видно, что именно поднялось первым
            if (!bad && _probeEpisode && ++_probeQuiet > 3) { _probeEpisode = false; _probeQuiet = 0; return; }
            if (!bad && !_probeEpisode) return;
            if (bad) _probeQuiet = 0;

            if (!_probeEpisode)
            {
                _probeEpisode = true;
                Log.Warn("Probe", "Провал записи — посекундная диагностика (кадры/с: получено, закодировано, " +
                                  "запросов MFT, дублей, дропов | backend/broker/cursor | очередь | видеопамять)");
            }

            string vram = GpuInfo.Usage(cap.D3DDevice) is { } v
                ? $"{v.UsedMb}/{v.BudgetMb} МБ"
                : "нет данных";
            var broker = _frameBroker?.GetDiagnostics(cap.InvalidCursorShapes)
                ?? new Diagnostics.CaptureBrokerDiagnostics(
                    generation, 0, 0, 0, 0, 0, cap.InvalidCursorShapes);
            long now100Nanoseconds = (long)(sampleTimestamp *
                (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            long frameAgeMilliseconds = Diagnostics.CaptureBrokerDiagnostics.AgeMilliseconds(
                now100Nanoseconds, broker.LatestTimestamp);

            string captureLabel = backend switch
            {
                CaptureBackend.Wgc => "WGC-monitor",
                CaptureBackend.WgcWindow => "WGC-window",
                CaptureBackend.DesktopDuplication => "DDA",
                CaptureBackend.MinecraftOpenGl => "OpenGL-game",
                _ => backend.ToString()
            };
            string routeDetails = "";
            if (cap is MinecraftGameCaptureSource minecraft)
            {
                Diagnostics.CaptureRouteProbeDiagnostics route = minecraft.GetRouteDiagnostics();
                captureLabel = route.Label;
                routeDetails = $"/epoch {route.RouteEpoch}/hook {route.HookState}:{route.HookError}/" +
                               $"heartbeat {route.HookHeartbeatAgeMilliseconds} мс/" +
                               $"issued {route.FramesIssued}/mapped {route.FramesMapped}/" +
                               $"published {route.FramesPublished}/rejected {route.FramesRejected}/" +
                               $"uploaded {route.FramesUploaded}";
            }
            GameCaptureTarget? target = ActiveCaptureTarget() ?? _gameCaptureRecovery.Target;
            string targetLabel = target is GameCaptureTarget current
                ? $"{current.ExecutableName}/hwnd 0x{current.Hwnd.ToInt64():X}/" +
                  $"pid {current.ProcessId}/r{current.Revision}"
                : "нет";
            CaptureEpisode episode = _gameCaptureRecovery.Episode;
            long holdStarted = Interlocked.Read(ref _windowHoldStartedTimestamp);
            long holdMilliseconds = holdStarted == 0 ? 0 : ElapsedMilliseconds(holdStarted);

            Log.Info("Probe", $"получено {fpsRecv}, закодировано {fpsEnc}, запросов {fpsReq}, " +
                              $"дублей {fpsDup}, дропов {fpsDrop} | {captureLabel}/gen {broker.Generation}{routeDetails} " +
                              $"target {targetLabel}/quarantine {episode.Quarantines} | " +
                              $"broker {broker.FramesPublished}/drop {broker.FramesDroppedNoSlot}/" +
                              $"reject {broker.FramesRejected}/" +
                              $"age {frameAgeMilliseconds} мс | cursor rev {broker.CursorRevision}/" +
                              $"invalid {broker.InvalidCursorShapes} | DDA storms {_ddaStormCount}/" +
                              $"window retries {_windowRetryCount}/hold {holdMilliseconds} мс | " +
                              $"очередь {enc.QueueDepth}/{enc.MaxQueue} " +
                              $"(пул {enc.PoolSlots}) | VRAM {vram}");
        }
        catch (Exception ex) { Log.Warn("Probe", $"Диагностика прервана: {ex.Message}"); }
    }

    private int _wedgeCount;
    private long _wdEncoded;
    private DateTime _wdEncodedAt;

    /// <summary>Порог молчания энкодера, после которого конвейер считается вставшим.</summary>
    private const double EncoderWedgeSeconds = 4;

    /// <summary>
    /// Встал ли конвейер целиком. Главный признак жизни — закодированные кадры:
    /// после первого кадра пейсер добивает частоту дубликатами, и энкодер выдаёт
    /// ровно fps кадров в секунду ВСЕГДА — на любом захвате, на статичном экране,
    /// под любой игрой. Ноль за несколько секунд бывает только при взаимной
    /// блокировке на устройстве видеокарты (NVENC, захват и копии кадров делят
    /// один замок). Раньше такой конвейер стоял до перезапуска программы: на DDA
    /// сторожа не было вовсе, а на WGC он смотрел только на кадры захвата.
    /// </summary>
    private bool EncoderWedged(out double stuck)
    {
        stuck = 0;
        var encoder = _encoder;
        if (encoder is null || !_encodedStreamReady) { _wdEncoded = -1; return false; }

        long encoded = Interlocked.Read(ref encoder.FramesEncoded);
        var now = DateTime.UtcNow;
        // Пока не было ни одного кадра, дубликатам не с чего браться: DDA на
        // статичном экране законно молчит с самого старта.
        if (encoded == 0 || encoded != _wdEncoded)
        {
            _wdEncoded = encoded;
            _wdEncodedAt = now;
            return false;
        }
        stuck = (now - _wdEncodedAt).TotalSeconds;
        if (stuck < EncoderWedgeSeconds) return false;
        _wdEncodedAt = now;   // один эпизод — одна пересборка
        return true;
    }

    private bool _wdStaticLogged;
    private DateTime? _wdEvidenceSince;
    private (int X, int Y) _wdCursor;
    private uint _wdInputTick;

    private static (int X, int Y) CursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (int.MinValue, int.MinValue);

    private static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Time; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private void StartCaptureWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;

        // DDA не присылает кадры, пока изображение рабочего стола не меняется —
        // это штатное поведение AcquireNextFrame, а не зависание. Тишину захвата
        // для него не проверяем, но живость энкодера — да (см. EncoderWedged).
        bool checkCaptureSilence = _capture is not DesktopDuplicationSource;

        _wdLastReceived = -1;
        _wdLastRate = 0;
        _wdLastActivity = DateTime.UtcNow;
        _wdEvidenceSince = null;
        _wdStaticLogged = false;
        _wdCursor = CursorPosition();
        _wdInputTick = LastInputTick();
        _wdEncoded = -1;
        _wdEncodedAt = DateTime.UtcNow;
        _watchdog = new System.Threading.Timer(_ =>
        {
            var cap = _capture;
            if (cap is null || !_pipelineOpen || _stopRequested) return;

            if (EncoderWedged(out double stuck))
            {
                // Встал прямой NVENC: пишем, на каком вызове, и до перезапуска
                // программы кодируем через MFT — новая сессия NVENC рядом с
                // зависшей не оживёт.
                if (_encoder is { DirectNvenc: true } wedgedEncoder)
                {
                    Log.Error("Engine", $"NVENC STALL: энкодер молчит {stuck:F1} с. Состояние NVENC:\n{wedgedEncoder.NvencTrace()}");
                    VideoEncoder.DirectNvencDisabled = true;
                    Interlocked.Increment(ref NvencStats.FatalErrors);
                }
                // Второй эпизод за сессию — видеокарта в этом процессе уже не
                // оправится: каждая следующая пересборка вставала бы снова и
                // оставляла брошенный конвейер (память и крутящийся в драйвере
                // поток). Раньше так набегало 12 пересборок, 6 ГБ и 100% процессора.
                if (Interlocked.Increment(ref _wedgeCount) > 1)
                {
                    Log.Error("Engine", $"Энкодер снова встал ({stuck:F1} с без кадров) — видеокарта не отвечает, " +
                                        "повтор выключаю, чтобы не копить брошенные конвейеры");
                    _ = Task.Run(() =>
                    {
                        try { Stop(); } catch (Exception ex) { Log.Error("Engine", ex); }
                        Warning?.Invoke("Запись остановлена: видеокарта перестала отвечать. Перезапустите Aura.");
                    });
                    return;
                }
                Log.Error("Engine", $"Энкодер не выдал ни одного кадра {stuck:F1} с при живом конвейере — " +
                                    "взаимная блокировка на видеокарте, пересобираю конвейер");
                long wedgedGeneration = Interlocked.Read(ref _captureGeneration);
                // Встал энкодер, а не захват: пересобираем на том же захвате
                OnCaptureFailed(new CaptureFailure(
                    CaptureFailureKind.DeviceLost,
                    new InvalidOperationException($"энкодер молчит {stuck:F0} с"),
                    "конвейер встал",
                    wedgedGeneration,
                    ActiveCaptureTarget()?.Revision ?? 0), wedgedGeneration);
                return;
            }
            if (!checkCaptureSilence) return;

            long received = cap.FramesReceived;
            if (received != _wdLastReceived)
            {
                // Запоминаем темп последнего живого окна: по нему и судим, тишина
                // это поломка или законное затишье (см. ниже).
                var now = DateTime.UtcNow;
                double window = (now - _wdLastActivity).TotalSeconds;
                if (_wdLastReceived >= 0 && window > 0.5)
                    _wdLastRate = (received - _wdLastReceived) / window;

                _wdLastReceived = received;
                _wdLastActivity = now;
                _wdEpisodeLogged = false;
                _wdSilenceLogged = false;
                _wdStaticLogged = false;
                _wdEvidenceSince = null;
                _wdCursor = CursorPosition();
                _wdInputTick = LastInputTick();
                return;
            }
            double silent = (DateTime.UtcNow - _wdLastActivity).TotalSeconds;

            // Порог пересборки зависит от того, ЧТО мы снимаем.
            //
            // Игра показывает кадры непрерывно, поэтому её молчание дольше пяти
            // секунд — это уже сломанный захват, и ждать пятнадцать значит подарить
            // буферу десять секунд пустоты. На рабочем столе всё наоборот: WGC
            // отдаёт кадры по композиции, а если на экране ничего не меняется,
            // композиции может не быть вовсе. Там короткий порог давал бы ложные
            // пересборки на ровном месте.
            var target = ActiveCaptureTarget();

            // Судим по ТЕМПУ последнего живого окна, а не по тому, что снимаем.
            //
            // WGC отдаёт кадры по композиции рабочего стола. Если на экране ничего
            // не меняется, композиции может не быть вовсе, и молчание там законно —
            // короткий порог давал бы пересборки на ровном месте. Но если секунду
            // назад шло шестьдесят кадров в секунду, а теперь ноль, это поломка,
            // и ждать пятнадцать секунд значит подарить буферу столько же пустоты.
            //
            // Отдельный случай — физически выключенный монитор. Событие питания при
            // этом не приходит вовсе: для Windows это отключение дисплея от шины,
            // а не погашенный экран. В логе такой эпизод выглядел как ровные 60 fps
            // и сразу за ними тишина.
            // Окно игры рисует кадры непрерывно: его молчание — уже поломка.
            const double rebuildAfter = 3;
            if (target is null)
            {
                // РАБОЧИЙ СТОЛ. WGC присылает кадры только когда экран меняется, и
                // молчание статичного экрана — норма, а не поломка. Раньше тишина
                // после живого потока через 3 секунды пересобирала конвейер и
                // переводила захват на Desktop Duplication: на каждой паузе у экрана.
                // Теперь поломкой считается только молчание, когда экран ТОЧНО
                // должен был измениться: WGC рисует курсор в кадр, и сдвиг мыши
                // обязан дать кадр. Если курсор не записывается, признак — ввод с
                // клавиатуры. Выключенный монитор без ввода тоже молчит законно;
                // вернётся человек, сдвинет мышь — и если WGC не ожил, пересоберём.
                bool cursorComposed = _settings.Current.RecordCursor;
                bool cursorMoved = CursorPosition() != _wdCursor;
                bool inputHappened = LastInputTick() != _wdInputTick;
                bool evidence = cursorComposed ? cursorMoved : inputHappened && !cursorMoved;
                if (!evidence)
                {
                    _wdEvidenceSince = null;
                    if (silent >= 3 && !_wdStaticLogged)
                    {
                        _wdStaticLogged = true;
                        Log.Info("Engine", "WGC молчит: экран не меняется — это норма, пейсер держит частоту дубликатами");
                    }
                    return;
                }
                _wdEvidenceSince ??= DateTime.UtcNow;
                silent = (DateTime.UtcNow - _wdEvidenceSince.Value).TotalSeconds;
            }

            // Ранняя запись в лог: она не чинит захват, но без неё причина эпизода
            // терялась. Пересборка стирает и очередь, и состояние источника, то есть
            // всё, по чему потом можно было бы понять, что именно встало.
            if (silent >= 3 && !_wdSilenceLogged)
            {
                _wdSilenceLogged = true;
                string vram = GpuInfo.Usage(cap.D3DDevice) is { } usage
                    ? $"{usage.UsedMb}/{usage.BudgetMb} МБ"
                    : "не читается";
                string captureName = _captureBackend == CaptureBackend.Wgc ? "WGC" : "DDA";
                Log.Warn("Engine", $"Захват молчит {silent:F1} с: backend {captureName}, " +
                                   $"цель {(target is null ? "рабочий стол" : $"окно 0x{target.Value.Hwnd:X}/r{target.Value.Revision}")}, " +
                                   $"получено всего {received}, темп до тишины {_wdLastRate:F0} кадр/с, " +
                                   $"очередь энкодера {_encoder?.QueueDepth ?? -1}, " +
                                   $"видеопамять {vram}; пересборка через {rebuildAfter - silent:F1} с");
            }

            if (silent < rebuildAfter) return;

            _wdLastActivity = DateTime.UtcNow;
            _wdSilenceLogged = false;
            _wdEvidenceSince = null;
            if (!_wdEpisodeLogged)
            {
                _wdEpisodeLogged = true;
                Log.Warn("Engine", $"Захват молчит >{rebuildAfter:F0} сек — пересобираю видеоконвейер");
            }
            long generation = Interlocked.Read(ref _captureGeneration);
            var stalled = new InvalidOperationException(
                $"WGC не присылает кадры больше {rebuildAfter:F0} секунд");
            OnCaptureFailed(new CaptureFailure(
                CaptureFailureKind.BackendStalled,
                stalled,
                "WGC: поток кадров остановился",
                generation,
                target?.Revision ?? 0), generation);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
}
