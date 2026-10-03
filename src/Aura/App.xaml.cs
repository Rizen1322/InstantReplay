using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Vortice.MediaFoundation;
using Aura.Core.Engine;
using Aura.Core.Hardware;
using Aura.Core.Hotkeys;
using Aura.Core.Logging;
using Aura.Core.Notifications;
using Aura.Core.Settings;
using Aura.Core.Storage;
using Aura.Core.SystemIntegration;

namespace Aura;

/// <summary>Сервисы приложения одним статическим контейнером.</summary>
public static class Services
{
    public static SettingsManager Settings { get; } = new();
    public static StorageManager Storage { get; private set; } = null!;
    public static ReplayEngine Engine { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static NotificationService Notifications { get; private set; } = null!;
    public static UpdateService Updates { get; } = new();
    public static NvidiaDriverService Nvidia { get; } = new();
    public static DriverWatch DriverWatch { get; private set; } = null!;

    /// <summary>Блокировка сеанса, погасший экран и сон: на это время повтор молчит.</summary>
    public static SystemActivityWatcher Activity { get; private set; } = null!;
    public static UiDispatcher Ui { get; private set; } = null!;

    public static void Init()
    {
        Ui = new UiDispatcher(Application.Current.Dispatcher);
        Storage = new StorageManager(Settings);
        Engine = new ReplayEngine(Settings, Storage);
        Hotkeys = new HotkeyService(Settings);
        Notifications = new NotificationService(Settings, Ui);
        DriverWatch = new DriverWatch(Settings, Nvidia);
        Activity = new SystemActivityWatcher(
            Engine.SuspendForSystem,
            Engine.ResumeAfterSystem,
            Engine.RebuildAfterDisplayChange);
    }
}

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\Aura.SingleInstance";
    private const string ShowWindowEventName = @"Global\Aura.ShowWindow";

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showWindowSignal;
    private MainWindow? _main;
    private TaskbarIcon? _tray;
    private MenuItem? _trayToggle, _traySave;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Одна копия: вторая не умирает молча, а просит первую показать окно.
        // --dev поднимает отдельную копию рядом с рабочей (проверка вёрстки).
        bool dev = e.Args.Contains("--dev");
        _singleInstance = new Mutex(true, dev ? InstanceMutexName + ".dev" : InstanceMutexName, out bool isNew);
        if (!isNew && !dev)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
                    using (signal) signal.Set();
            }
            catch { }
            Environment.Exit(0);
            return;
        }

        // --dev --capture wgc|dda: источник захвата для проверки. Переменная окружения
        // INSTANTREPLAY_CAPTURE до копии с правами администратора не доходит.
        int captureArg = Array.IndexOf(e.Args, "--capture");
        if (dev && captureArg >= 0 && captureArg + 1 < e.Args.Length)
            Environment.SetEnvironmentVariable("INSTANTREPLAY_CAPTURE", e.Args[captureArg + 1]);

        // Копия --dev пишет в свой файл: она работает рядом с обычной, а лог открыт
        // на общий доступ — в один файл две копии писали бы вперемешку.
        Log.Init(fileSuffix: dev ? "-dev" : "");

        // Ловим падения ВСЕХ потоков, а не только интерфейсного. Конвейер записи
        // живёт в своих потоках (события MFT, питатель, пейсер, писатель файла), и
        // необработанное исключение в любом из них убивает процесс мгновенно — без
        // единой строки в логе, потому что обычные записи уходят через фоновую
        // очередь и не успевают дойти до диска. Отсюда были «молчаливые» падения.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("App", $"Исключение в потоке интерфейса: {args.Exception}");
            args.Handled = true; // интерфейс переживёт, движок продолжит писать
            // ...но человек должен об этом узнать. Гасить всё молча означало «кнопка
            // нажимается и ничего не делает»: пользователь считает, что приложение
            // сломалось, и не догадывается заглянуть в лог. Показываем не чаще раза
            // в полминуты, иначе повторяющаяся ошибка завалит экран уведомлениями.
            ReportUiFailure(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal("App", $"Необработанное исключение{(args.IsTerminating ? " (процесс завершается)" : "")}: " +
                             $"{args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal("App", $"Исключение в фоновой задаче: {args.Exception}");
            args.SetObserved();
        };
        // timeBeginPeriod(1) на весь процесс больше не нужен: потоки, которым важна
        // точность (пейсер энкодера, захват DDA, микшер звука), ждут на собственном
        // высокоточном таймере (Core/Interop/PreciseTimer.cs). Глобальный 1-мс тик
        // держал систему в режиме повышенного энергопотребления даже тогда, когда
        // повтор выключен и приложение просто лежит в трее.
        RaiseGpuPriority();

        Services.Settings.Load();
        // Отладочная копия со своей папкой данных пишет только туда же. Если её
        // settings.json пропал, настройки по умолчанию указали бы на настоящую
        // папку записей человека, и тестовые ролики смешались бы с его клипами.
        if (e.Args.Contains("--dev") && e.Args.Contains("--data-dir"))
        {
            string sandbox = Path.Combine(SettingsManager.Dir, "out");
            var current = Services.Settings.Current;
            if (!current.SaveRootPath.StartsWith(SettingsManager.Dir, StringComparison.OrdinalIgnoreCase) ||
                !current.ScreenshotFolder.StartsWith(SettingsManager.Dir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(sandbox);
                current.SaveRootPath = sandbox;
                current.ScreenshotFolder = sandbox;
                current.AutoStartReplayBuffer = false;
                Log.Info("App", $"Отладочная копия: записи только в {sandbox}");
            }
        }
        Loc.Lang = Services.Settings.Current.Language;
        // --theme dark|deep|light|system — примерить тему, не трогая настройки.
        // Рядом с --page и --dev: примерка оформления не должна переписывать
        // settings.json пользователя.
        var theme = ThemeArg(e.Args) ?? Services.Settings.Current.Theme;
        ApplyTheme(theme);

        MediaFactory.MFStartup(); // Media Foundation — один раз на процесс
        // Файл буфера на диске удаляет сама система при закрытии, но после отказа
        // питания он может остаться — убираем хвосты прошлых запусков.
        Core.Buffering.ReplayVideoBuffer.DiskDirectory = Path.Combine(SettingsManager.Dir, "ReplayBuffer");
        _ = Task.Run(() => Core.Buffering.FileArenaStorage.CleanupStale(Core.Buffering.ReplayVideoBuffer.DiskDirectory));

        Services.Init();
        Views.ClipCommands.Register();
        WireEvents();
        Views.ReplayFilmstrip.Start();
        GuardCodec();

        // Отладочная копия автозапуск не трогает: раньше каждый запуск с --dev
        // переписывал задачу Планировщика на свой путь, и после перезагрузки
        // стартовала копия из папки сборки вместо установленной Aura.
        if (!dev) StartupManager.Reconcile(Services.Settings.Current.AutoStartWithWindows);
        // Снимок вёрстки и самопроверка идут рядом с настоящей Aura: перехватывать
        // её сочетания клавиш им нельзя.
        bool headless = e.Args.Contains("--snapshot") || e.Args.Contains("--selftest-record") ||
                        e.Args.Contains("--selftest-replay") || e.Args.Contains("--whatsnew-test") ||
                        e.Args.Contains("--editor-seek-test");
        if (!(dev && headless)) Services.Hotkeys.Start();
        else Services.Notifications.Muted = true;

        // Приложение живёт в трее, но выходить обязано только по своей команде.
        // Иначе, если значок не создался, а окно спрятано, WPF гасит процесс сам
        // по правилу «закрылось последнее окно» — именно так оно и исчезало.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Значок в трее — удобство, а не условие работы: хоткеи и запись живут
        // без него. Упасть здесь означало оборвать весь остаток запуска.
        try { InitTray(); }
        catch (Exception ex) { Log.Error("App", $"Значок в трее не создан: {ex.Message}"); }
        StartShowWindowListener();

        // В режиме --dev окно показываем всегда: иначе с включённым «стартовать
        // свёрнутым в трей» отладочная копия молча уходит в трей.
        bool minimized = !dev &&
            (e.Args.Contains("--minimized") || Services.Settings.Current.StartMinimizedToTray);
        _main = new MainWindow();
        int pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length) _main.StartPage = e.Args[pageArg + 1];
        MainWindow = _main;

        // Значки под тему — здесь, а не в ApplyTheme выше: там окна ещё не было,
        // а кнопку на панели задач рисует именно иконка окна.
        ApplyThemeIcons(theme);

        // --selftest-record N: записать N секунд в файл без повтора и выйти. Только
        // для проверки конвейера записи без рук (--dev и своя папка данных).
        int selfTestArg = Array.IndexOf(e.Args, "--selftest-record");
        if (dev && selfTestArg >= 0 && selfTestArg + 1 < e.Args.Length &&
            int.TryParse(e.Args[selfTestArg + 1], out int selfTestSeconds))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                // Из потока интерфейса, как при нажатии кнопки: из пула потоков
                // (MTA) проверка не ловила ошибки, которые бывают только в STA.
                Current.Dispatcher.Invoke(SafeStartRecording);
                Log.Info("SelfTest", $"запись идёт: {Services.Engine.IsRecordingToFile}, повтор: {Services.Engine.ReplayActive}, " +
                                     $"конвейер: {Services.Engine.State}");
                if (e.Args.Contains("--selftest-replay-during"))
                {
                    await Task.Delay(selfTestSeconds * 500);
                    Current.Dispatcher.Invoke(SafeStartEngine);
                    Log.Info("SelfTest", $"включил повтор посреди записи: повтор {Services.Engine.ReplayActive}");
                    await Task.Delay(selfTestSeconds * 500);
                }
                else if (e.Args.Contains("--selftest-replay-toggle"))
                {
                    // Повтор включили и выключили посреди записи: запись обязана идти дальше
                    await Task.Delay(selfTestSeconds * 333);
                    Current.Dispatcher.Invoke(ToggleEngine);
                    Log.Info("SelfTest", $"повтор включён: {Services.Engine.ReplayActive}");
                    await Task.Delay(selfTestSeconds * 333);
                    Current.Dispatcher.Invoke(ToggleEngine);
                    Current.Dispatcher.Invoke(() => Services.Engine.SaveReplay());
                    Log.Info("SelfTest", $"повтор выключен: повтор {Services.Engine.ReplayActive}, " +
                                         $"запись идёт {Services.Engine.IsRecordingToFile}");
                    await Task.Delay(selfTestSeconds * 333);
                }
                else if (e.Args.Contains("--selftest-reconfig"))
                {
                    // Контроллер нагрузки NVENC: посреди записи снять второй проход, потом AQ
                    await Task.Delay(selfTestSeconds * 333);
                    Services.Engine.ReconfigureNvencForTest(0, true);
                    await Task.Delay(selfTestSeconds * 333);
                    Services.Engine.ReconfigureNvencForTest(0, false);
                    await Task.Delay(selfTestSeconds * 334);
                }
                else if (e.Args.Contains("--selftest-discard"))
                {
                    // Выход ради обновления: запись выбрасывается, файла быть не должно
                    await Task.Delay(selfTestSeconds * 1000);
                    Current.Dispatcher.Invoke(() => Services.Engine.DiscardRecordingForExit());
                    Log.Info("SelfTest", $"запись выброшена: запись идёт {Services.Engine.IsRecordingToFile}");
                    Current.Dispatcher.Invoke(ExitApp);
                    return;
                }
                else if (e.Args.Contains("--selftest-format-change"))
                {
                    // Смена разрешения посреди записи: запись продолжается новым файлом
                    await Task.Delay(selfTestSeconds * 500);
                    Services.Settings.Update(s => s.VerticalResolution = s.VerticalResolution == 720 ? 1080 : 720, "video");
                    Log.Info("SelfTest", $"сменил разрешение: запись идёт {Services.Engine.IsRecordingToFile}, " +
                                         $"конвейер {Services.Engine.State}");
                    await Task.Delay(selfTestSeconds * 500);
                }
                else await Task.Delay(selfTestSeconds * 1000);
                string? file = Current.Dispatcher.Invoke(() => Services.Engine.StopRecordingToFile(wait: true));
                await Task.Delay(1500);
                Log.Info("SelfTest", $"файл: {file}; конвейер после записи: {Services.Engine.State}");
                Current.Dispatcher.Invoke(ExitApp);
            });
        }

        // --selftest-replay N: включить повтор, через N секунд сохранить клип и выйти
        int replayTestArg = Array.IndexOf(e.Args, "--selftest-replay");
        if (dev && replayTestArg >= 0 && replayTestArg + 1 < e.Args.Length &&
            int.TryParse(e.Args[replayTestArg + 1], out int replayTestSeconds))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                Current.Dispatcher.Invoke(SafeStartEngine);
                await Task.Delay(replayTestSeconds * 1000);
                // Продолжение не в потоке сохранения: иначе выход ждал бы сохранение,
                // которое само ждёт выход
                var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Services.Engine.ReplaySaved += (file, _) => saved.TrySetResult(file);
                Services.Engine.SaveFailed += msg => saved.TrySetResult("ошибка: " + msg);
                Current.Dispatcher.Invoke(() => Services.Engine.SaveReplay());
                string result = await Task.WhenAny(saved.Task, Task.Delay(30_000)) == saved.Task ? saved.Task.Result : "таймаут";
                Log.Info("SelfTest", $"повтор сохранён: {result}");
                Current.Dispatcher.Invoke(ExitApp);
            });
        }

        // --editor-seek-test файл папка: перемотки в редакторе за пределами экрана
        int seekTest = Array.IndexOf(e.Args, "--editor-seek-test");
        if (dev && seekTest >= 0 && seekTest + 2 < e.Args.Length)
        {
            string clip = e.Args[seekTest + 1], outDir = e.Args[seekTest + 2];
            Services.Ui.Enqueue(() =>
            {
                try { Views.ClipEditorWindow.RunSeekSelfTest(clip, outDir); }
                catch (Exception ex) { Log.Error("SelfTest", ex); }
                ExitApp();
            });
            return;
        }

        // --whatsnew-test: проиграть ролик «что нового» за пределами экрана без звука
        if (dev && e.Args.Contains("--whatsnew-test"))
        {
            Services.Ui.Enqueue(() =>
            {
                if (Views.WhatsNewVideo.FileFor(Core.SystemIntegration.UpdateService.CurrentVersion) is { } clip)
                {
                    var started = DateTime.UtcNow;
                    var w = Views.WhatsNewVideo.Create(clip, offscreenTest: true);
                    w.ShowDialog();
                    Log.Info("SelfTest", $"ролик закрыт через {(DateTime.UtcNow - started).TotalSeconds:F1} с");
                }
                else Log.Info("SelfTest", "ролика нет");
                ExitApp();
            });
            return;
        }

        // --snapshot файл.png — снимок вёрстки без экрана: окно открывается за его
        // пределами и без фокуса (не мешает игре или работе), рисуется в картинку,
        // и приложение закрывается. Только вместе с --dev.
        int snapArg = Array.IndexOf(e.Args, "--snapshot");
        if (dev && snapArg >= 0 && snapArg + 1 < e.Args.Length)
        {
            int clipArg = Array.IndexOf(e.Args, "--editor");
            Window target = clipArg >= 0 && clipArg + 1 < e.Args.Length
                ? Views.ClipEditorWindow.CreateForSnapshot(e.Args[clipArg + 1])
                : e.Args.Contains("--toast") ? SnapshotToast()
                : e.Args.Contains("--whatsnew") && Views.WhatsNewVideo.FileFor(Core.SystemIntegration.UpdateService.CurrentVersion) is { } whatsNew
                    ? Views.WhatsNewVideo.Create(whatsNew)
                : e.Args.Contains("--changelog")
                    ? Views.Dialogs.CreateChangelogWindow(Core.SystemIntegration.Changelog.Entries.Take(1).ToList())
                    : _main;
            SnapshotAndExit(target, e.Args[snapArg + 1]);
            return;
        }

        // Самопроверки (--selftest-*) идут без окон: их запускают, пока человек
        // может быть в игре, и ни главное окно, ни список изменений не должны
        // всплывать у него на экране.
        if (!(dev && headless))
        {
            if (!minimized) _main.Show();
            ShowChangelogIfUpdated();
        }

        if (Services.Settings.Current.AutoStartReplayBuffer) SafeStartEngine();
        if (Services.Settings.Current.CheckForUpdates) _ = CheckUpdatesAsync();

        Services.DriverWatch.UpdateFound += version => Services.Notifications.Show(
            NotificationKind.Warning, Loc.T("driver_found", version));
        Services.DriverWatch.Start();
        Services.Activity.Start();

        CleanLeftovers();
    }

    /// <summary>
    /// Разовая уборка при запуске: кэш миниатюр от записей, которых больше нет.
    ///
    /// Его не чистил вообще никто — ни при автоочистке по лимиту папки, ни при
    /// удалении файлов из проводника. За полгода игры это сотни мегабайт кадров
    /// от клипов, удалённых давным-давно. Панорама делает то же самое при каждом
    /// открытии, но открывают её не все, поэтому один заход при старте — в фоне
    /// и с самым низким приоритетом, чтобы не мешать разгону конвейера записи.
    /// </summary>
    private static void CleanLeftovers() => Task.Run(() =>
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            var s = Services.Settings.Current;
            Core.Library.ClipThumbnails.PruneOrphans(
                Core.Library.ClipLibrary.ScanAll(s.SaveRootPath, s.ScreenshotFolder));
            PruneUnfinishedFiles(s.SaveRootPath);
        }
        catch (Exception ex) { Log.Warn("App", $"Уборка кэша: {ex.Message}"); }
    });

    /// <summary>
    /// Разобрать недописанные файлы, оставшиеся от прерванных сохранений.
    ///
    /// Клип и части обычной записи пишутся как «.mp4.part» и переименовываются
    /// только после успешной финализации. Недописанный клип повтора без оглавления
    /// не открывается ничем, его удаляем. А запись в файл идёт фрагментами и после
    /// падения или отключения питания играется до последнего целого фрагмента:
    /// раньше её тоже удаляли, и терялось ровно то, ради чего фрагменты и нужны.
    /// Теперь такая запись восстанавливается как «… (восстановлено).mp4».
    /// </summary>
    private static void PruneUnfinishedFiles(string root)
    {
        if (!Directory.Exists(root)) return;

        int removed = 0;
        long freed = 0;
        var recovered = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories))
            try
            {
                // Свежий .part может принадлежать идущему прямо сейчас сохранению
                var info = new FileInfo(file);
                if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;

                string? saved = null;
                try { saved = Core.Saving.Mp4.Mp4Defragment.RecoverPart(file); }
                catch (Exception ex) { Log.Warn("App", $"Запись «{info.Name}» не восстановлена: {ex.Message}"); }
                if (saved is not null)
                {
                    Services.Storage.RegisterSaved(saved);
                    recovered.Add(saved);
                    Log.Info("App", $"Восстановлена запись после сбоя: {saved}");
                    continue;
                }
                if (!File.Exists(file)) continue;
                freed += info.Length;
                File.Delete(file);
                removed++;
            }
            catch { /* занят или недоступен — попробуем в следующий раз */ }

        if (removed > 0)
            Log.Info("App", $"Убрано незавершённых файлов: {removed} ({freed / (1024 * 1024)} МБ)");
        if (recovered.Count > 0)
        {
            Services.Notifications.Show(NotificationKind.Info,
                recovered.Count == 1 ? "Восстановлена запись после сбоя" : $"Восстановлено записей после сбоя: {recovered.Count}",
                Path.GetFileName(recovered[0]));
            Views.ClipCommands.NotifyLibraryChanged();
        }
    }

    /// <summary>
    /// Кодек, который система не сможет упаковать в MP4, молча превращает запись
    /// в пустоту: буфер пишется, а каждое сохранение падает. Уводим на рабочий
    /// кодек сразу при запуске и говорим об этом вслух.
    /// </summary>
    private static void GuardCodec()
    {
        var settings = Services.Settings.Current;
        if (Core.Encoding.HardwareEncoders.CanSaveToMp4(settings.Codec)) return;

        var fallback = Core.Settings.VideoCodec.H264;
        try
        {
            var (_, codecs) = Core.Encoding.HardwareEncoders.ProbeSupport();
            if (codecs.Contains(Core.Settings.VideoCodec.HEVC)) fallback = Core.Settings.VideoCodec.HEVC;
        }
        catch { }

        Log.Warn("App", $"{settings.Codec} нельзя сохранить в MP4 на этой Windows — переключаю на {fallback}");
        Services.Settings.Update(s => s.Codec = fallback, "video");
        Services.Notifications.Show(NotificationKind.Warning, "AV1 здесь не сохраняется",
                                    $"Переключил на {(fallback == Core.Settings.VideoCodec.HEVC ? "HEVC" : "H.264")}");
    }

    /// <summary>Включено ли аппаратное планирование GPU (HAGS); null — не узнали.</summary>
    public static bool? HagsEnabled { get; private set; }

    /// <summary>
    /// GPU-приоритет повыше: команды записи не ждут в очереди за игрой.
    ///
    /// Как у OBS: REALTIME, если HAGS выключен. Под Split Fiction (видеокарта 95%)
    /// с HIGH кадры ждали в очереди Aura до двух секунд, хотя сам NVENC был занят
    /// на треть: наши копии кадра стояли на видеокарте за игрой. При включённом HAGS
    /// остаёмся на HIGH: у NVIDIA описаны зависания NVENC при REALTIME вместе с HAGS.
    /// </summary>
    private static void RaiseGpuPriority()
    {
        try
        {
            HagsEnabled = Core.Hardware.GpuScheduling.HagsEnabled();
            bool realtime = HagsEnabled == false;
            int st = Core.Interop.NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(
                Core.Interop.NativeMethods.GetCurrentProcess(),
                realtime ? Core.Interop.NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME
                         : Core.Interop.NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
            string hags = HagsEnabled switch { true => "HAGS включён", false => "HAGS выключен", _ => "HAGS не определён" };
            Log.Info("App", st == 0 ? $"GPU-приоритет процесса: {(realtime ? "REALTIME" : "HIGH")} ({hags})"
                                    : $"GPU-приоритет не применился (0x{st:X8}, {hags})");
        }
        catch { }
    }

    // ---------------- Тема ----------------

    /// <summary>
    /// Кадр нужного размера из .ico под выбранную тему.
    ///
    /// Размер приходится выбирать руками: BitmapImage для .ico отдаёт ПЕРВЫЙ кадр
    /// в файле, а не подходящий по размеру. У зелёных значков первым лежит 16×16 —
    /// и кнопка на панели задач получала иконку в 16 px, которую Windows рисует
    /// мелко по центру, не растягивая. Отсюда и разный размер у тем.
    /// </summary>
    private static BitmapSource? ThemeIcon(AppTheme theme, int wanted)
    {
        const string file = "tray.ico";
        try
        {
            var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", file));
            var frames = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;

            // Ближайший кадр НЕ МЕНЬШЕ нужного (уменьшать можно, увеличивать — мыло),
            // а если таких нет — самый крупный из имеющихся.
            return frames.Where(f => f.PixelWidth >= wanted).OrderBy(f => f.PixelWidth).FirstOrDefault()
                   ?? frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn("App", $"Значок {file}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Значок под выбранную тему в обоих местах, где его видно.
    ///
    /// Их именно два, и берутся они из разных источников. Значок в трее — это
    /// IconSource у TaskbarIcon. А кнопка на панели задач и уголок заголовка берут
    /// ИКОНКУ ОКНА, и пока она не задана, Windows подставляет иконку exe из
    /// ApplicationIcon — та вшита при компиляции и остаётся зелёной при любой теме.
    /// Поэтому окну значок присваиваем явно.
    /// </summary>
    public void ApplyThemeIcons(AppTheme theme)
    {
        // Трею хватает мелкого кадра (16–24 px в области уведомлений), кнопке на
        // панели задач нужен крупный: 32 px при обычном масштабе и 64 при 200%.
        if (_tray is not null && ThemeIcon(theme, 32) is { } trayIcon) _tray.IconSource = trayIcon;
        if (_main is not null && ThemeIcon(theme, 64) is { } windowIcon) _main.Icon = windowIcon;
    }

    /// <summary>Нарисовать окно в PNG за пределами экрана и выйти (проверка вёрстки).</summary>
    private static Window SnapshotToast()
    {
        var toast = new Notifications.ToastWindow();
        toast.PrepareForSnapshot(new Notifications.ToastContent(
            "Повтор сохранён, 3:00", "Counter-Strike 2, 214 МБ",
            Current.TryFindResource("Ico.Save") as System.Windows.Media.Geometry,
            (System.Windows.Media.Brush)Current.FindResource("AccentBrush"), Hint: "F9"));
        return toast;
    }

    private static void SnapshotAndExit(Window window, string path)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        if (window is Aura.MainWindow or Views.ClipEditorWindow)
        {
            window.Width = 1180;
            window.Height = 760;
        }
        window.Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var root = (FrameworkElement)window.Content;
                // --scale 2 рисует снимок в двойном разрешении (для роликов и превью)
                var args = Environment.GetCommandLineArgs();
                int scaleArg = Array.IndexOf(args, "--scale");
                double scale = scaleArg >= 0 && scaleArg + 1 < args.Length &&
                               double.TryParse(args[scaleArg + 1], System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out var k) ? k : 1;
                var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale),
                                                    96 * scale, 96 * scale,
                                                    System.Windows.Media.PixelFormats.Pbgra32);
                var canvas = new System.Windows.Media.DrawingVisual();
                using (var dc = canvas.RenderOpen())
                {
                    dc.DrawRectangle((System.Windows.Media.Brush)window.Background, null,
                                     new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null,
                                     new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }
                bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(path);
                encoder.Save(file);
            }
            catch (Exception ex) { Log.Error("App", $"Снимок вёрстки: {ex}"); }
            Environment.Exit(0);
        };
        timer.Start();
    }

    private static AppTheme? ThemeArg(string[] args)
    {
        int i = Array.IndexOf(args, "--theme");
        if (i < 0 || i + 1 >= args.Length) return null;
        return Enum.TryParse<AppTheme>(args[i + 1], ignoreCase: true, out var theme) ? theme : null;
    }

    public static void ApplyTheme(AppTheme theme)
    {
        string file = theme switch
        {
            AppTheme.Dark => "Theme/Palette.Dark.xaml",
            AppTheme.Light => "Theme/Palette.Light.xaml",
            _ => IsSystemDark() ? "Theme/Palette.Dark.xaml" : "Theme/Palette.Light.xaml"
        };
        var uri = new Uri(file, UriKind.Relative);
        var dictionaries = Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = uri };

        // Значки живут вне словарей ресурсов — меняем их руками
        (Current as App)?.ApplyThemeIcons(theme);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int?) == 0;
        }
        catch { return true; }
    }

    // ---------------- События конвейера ----------------

    private void WireEvents()
    {
        var n = Services.Notifications;
        var engine = Services.Engine;

        n.PreviewSource = engine.TryUseLiveFrame;

        engine.StateChanged += _ => Services.Ui.Enqueue(UpdateTray);
        engine.RecordingChanged += _ => Services.Ui.Enqueue(UpdateTray);
        // Уведомляем о самом повторе, а не о конвейере: запись в файл без повтора
        // тоже поднимает конвейер, но «Повтор включён» при этом было бы враньём.
        bool wasActive = false;
        engine.StateChanged += state =>
        {
            bool active = engine.ReplayActive && state == EngineState.Running;
            if (active && !wasActive)
                n.Show(NotificationKind.ReplayOn, "Повтор включён");
            else if (!engine.ReplayActive && wasActive)
                n.Show(NotificationKind.Stopped, "Повтор выключен");
            if (active || !engine.ReplayActive) wasActive = active;
        };

        // Сохранение показывается в два этапа: снимок буфера — уже гарантия клипа,
        // диск догоняет в фоне, и карточка дорисовывается по факту записи файла.
        engine.ReplayCaptured += _ => n.ShowSaving("Сохраняю повтор…");
        engine.ReplaySaved += (file, seconds) =>
        {
            n.CompleteSaving("Повтор сохранён", $"{Loc.Duration(seconds)} · {Describe(file)}");
            Views.ClipCommands.NotifyClipAdded(file);
        };
        engine.SaveFailed += msg => n.Show(NotificationKind.Warning, "Не удалось сохранить", msg);
        engine.Warning += msg => n.Show(NotificationKind.Warning, msg);

        engine.RecordingChanged += rec => { if (rec) n.Show(NotificationKind.Recording, "Запись началась"); };
        engine.RecordingPartSaved += file => Services.Ui.Enqueue(() => Views.ClipCommands.NotifyClipAdded(file));
        engine.RecordingSaved += (file, seconds) =>
        {
            n.Show(NotificationKind.Saved, "Запись сохранена", $"{Loc.Duration(seconds)} · {Describe(file)}");
            Views.ClipCommands.NotifyClipAdded(file);
        };

        Services.Hotkeys.PushToTalkChanged += (held, ticks) => engine.SetPushToTalk(held, ticks);
        Services.Hotkeys.HotkeyPressed += action => Services.Ui.Enqueue(() =>
        {
            switch (action)
            {
                case HotkeyAction.SaveReplay: engine.SaveReplay(); break;
                case HotkeyAction.SaveLast30: engine.SaveReplay(30); break;
                case HotkeyAction.StartRecording: SafeStartRecording(); break;
                case HotkeyAction.StopRecording: engine.StopRecordingToFile(); break;
                case HotkeyAction.ToggleInstantReplay: ToggleEngine(); break;
                case HotkeyAction.Screenshot: _ = TakeScreenshotAsync(); break;
                case HotkeyAction.ScreenshotRegion: _ = TakeRegionScreenshotAsync(); break;
                case HotkeyAction.OpenFolder: OpenRecordingsFolder(); break;
            }
        });

        // Итог работы оверлея выделения: уведомления показывает приложение, а не окно —
        // оно к моменту показа уже закрыто.
        Views.RegionCaptureWindow.Saved += (file, copied) => Services.Ui.Enqueue(() =>
        {
            n.Show(NotificationKind.Screenshot,
                file is null ? "Скриншот в буфере обмена" : "Скриншот сохранён",
                file is null ? null : (copied ? "и скопирован" : Describe(file)));
            if (file is not null) Views.ClipCommands.NotifyClipAdded(file);
        });
        Views.RegionCaptureWindow.Failed += ex =>
            n.Show(NotificationKind.Warning, "Не удалось сделать скриншот", ex.Message);

        Services.Settings.Changed += group =>
        {
            if (group is "" or "system")
                StartupManager.SetEnabled(Services.Settings.Current.AutoStartWithWindows);
            if (group is "" or "ui")
            {
                Loc.Lang = Services.Settings.Current.Language;
                ApplyTheme(Services.Settings.Current.Theme);
            }
        };
    }

    /// <summary>
    /// «Что нового» после обновления.
    ///
    /// Показывается один раз на версию и только тем, кто обновился: при первой в
    /// жизни установке в настройках ещё нет прошлой версии, и список изменений там
    /// был бы разговором ни о чём.
    ///
    /// Если приложение стартовало свёрнутым в трей (автозапуск вместе с Windows —
    /// обычно прямо в игру), диалог не всплывает поверх экрана: отметка о показе не
    /// ставится, и список дождётся момента, когда человек сам откроет окно.
    /// </summary>
    private void ShowChangelogIfUpdated()
    {
        var s = Services.Settings.Current;
        var current = Core.SystemIntegration.UpdateService.CurrentVersion;
        var last = Core.SystemIntegration.Changelog.Parse(s.LastSeenVersion);

        if (last is null)
        {
            // Первая установка: ролик «что нового» только для тех, кто обновился
            Views.WhatsNewVideo.DeleteAll();
            Services.Settings.Update(x => x.LastSeenVersion = current.ToString(3), "app");
            return;
        }
        if (last >= current)
        {
            Views.WhatsNewVideo.DeleteAll();   // уже показан или не нужен
            return;
        }

        var entries = Core.SystemIntegration.Changelog.Between(last, current);
        if (entries.Count == 0)
        {
            Services.Settings.Update(x => x.LastSeenVersion = current.ToString(3), "app");
            return;
        }

        // Окна нет — ничего не отмечаем и ждём: проверку повторит ShowMainWindow,
        // когда человек откроет приложение. Подписываться на события этого окна
        // бесполезно: показ спрятанного окна пересоздаёт его заново (см. IsClosed),
        // и обработчик остался бы на выброшенном экземпляре.
        if (_main is null || !_main.IsVisible) return;

        Services.Ui.Enqueue(() => PresentChangelog(entries, current));
    }

    /// <summary>
    /// Список уже на экране. Диалог модальный, но его собственный цикл сообщений
    /// продолжает крутить очередь диспетчера: пока человек читает, вторая копия
    /// приложения успевает попросить показать окно, и поверх открывался второй такой
    /// же список. Отметка о показе ставится только после закрытия, поэтому от неё
    /// защиты нет — нужен свой признак.
    /// </summary>
    private static bool _changelogVisible;

    private static void PresentChangelog(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries, Version current)
    {
        if (_changelogVisible) return;
        _changelogVisible = true;

        // Сначала ролик, если он приложен к этой версии, потом обычный список
        var lastSeen = Core.SystemIntegration.Changelog.Parse(Services.Settings.Current.LastSeenVersion) ?? new Version(0, 0);
        if (Views.WhatsNewVideo.FileBetween(lastSeen, current) is { } video)
        {
            try { Views.WhatsNewVideo.Show(video); }
            catch (Exception ex) { Log.Warn("App", $"Ролик «что нового»: {ex.Message}"); }
        }
        Views.WhatsNewVideo.DeleteAll();

        Log.Info("App", $"Показываю список изменений до версии {current}");
        try { Views.Dialogs.ShowChangelog(entries); }
        catch (Exception ex) { Log.Warn("App", $"Список изменений: {ex.Message}"); }
        finally { _changelogVisible = false; }

        // Отметку ставим после показа: если диалог не открылся, человек не должен
        // потерять список из-за нашей ошибки.
        Services.Settings.Update(s => s.LastSeenVersion = current.ToString(3), "app");
    }

    /// <summary>
    /// Вторая строка уведомления. Длинное название игры в карточку не влезает,
    /// поэтому оставляем только вес — размер важнее.
    /// </summary>
    private static string Describe(string file)
    {
        try
        {
            var info = new FileInfo(file);
            string game = info.Directory?.Name ?? "";
            string size = ByteSize.Format(info.Length);
            return game.Length is > 0 and <= 16 ? $"{game} · {size}" : size;
        }
        catch { return ""; }
    }

    // ---------------- Действия ----------------

    public static void SafeStartEngine()
    {
        try
        {
            Services.Engine.Start();
            AskBorderlessPermission();
        }
        catch (Core.Storage.InsufficientDiskSpaceException ex)
        {
            Services.Notifications.Show(NotificationKind.Warning, "Повтор не включён: нет места на диске", ex.Message);
        }
        catch (Exception ex) { Services.Notifications.Show(NotificationKind.Warning, "Не удалось включить повтор", ex.Message); }
    }

    /// <summary>
    /// Права на захват без рамки нет — просим его у пользователя.
    ///
    /// Своего диалога у системы для классических приложений нет: согласие заводится
    /// в состоянии «не решено», а решение принимается на странице параметров. Поэтому
    /// открываем её сами — но ровно один раз за всё время: отказ тоже ответ, и
    /// открывать «Параметры» при каждом включении повтора было бы навязчиво.
    /// </summary>
    private static void AskBorderlessPermission()
    {
        if (Services.Engine.ActiveCaptureBackend != Core.Capture.CaptureBackend.Wgc) return;
        if (Core.Capture.CaptureAccess.BorderlessGranted) return;

        Services.Notifications.Show(NotificationKind.Warning, "Windows рисует рамку записи",
            "Разрешите захват без рамки в параметрах конфиденциальности");

        if (Services.Settings.Current.BorderlessPermissionAsked) return;
        Services.Settings.Update(s => s.BorderlessPermissionAsked = true, "system");
        Core.Capture.CaptureAccess.OpenPermissionSettings();
    }

    public static void SafeStartRecording()
    {
        try { Services.Engine.StartRecordingToFile(); }
        catch (Core.Storage.InsufficientDiskSpaceException ex)
        {
            Services.Notifications.Show(NotificationKind.Warning, "Запись не начата: нет места на диске", ex.Message);
        }
        catch (Exception ex) { Services.Notifications.Show(NotificationKind.Warning, "Не удалось начать запись", ex.Message); }
    }

    public static void ToggleEngine()
    {
        // Переключатель трогает только повтор: идущая запись в файл продолжается
        // и при включении, и при выключении
        if (!Services.Engine.ReplayActive) SafeStartEngine();
        else Services.Engine.StopReplay();
    }

    /// <summary>
    /// Скриншот всего экрана. Складывается в отдельную папку скриншотов, а не к
    /// клипам: библиотека приложения — про видео, и картинки там только мешали
    /// подсчёту занятого места.
    /// </summary>
    public static async Task TakeScreenshotAsync()
    {
        var s = Services.Settings.Current;
        try
        {
            string file = Core.Capture.ScreenshotService.NextFilePath(s.ScreenshotFolder);
            var (monitor, live) = ScreenshotTarget();

            await Task.Run(() => Core.Capture.ScreenshotService.CaptureAsync(
                monitor, file, s.RecordCursor, live));
            Services.Notifications.Show(NotificationKind.Screenshot, "Скриншот сохранён", Describe(file));
            Services.Ui.Enqueue(() => Views.ClipCommands.NotifyClipAdded(file));
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Services.Notifications.Show(NotificationKind.Warning, "Не удалось сделать скриншот");
        }
    }

    /// <summary>Скриншот выделенной области: оверлей с рисованием, копированием и сохранением.</summary>
    public static async Task TakeRegionScreenshotAsync()
    {
        var s = Services.Settings.Current;
        try
        {
            var (monitor, live) = ScreenshotTarget(forRegion: true);
            await Views.RegionCaptureWindow.ShowForAsync(monitor, s.RecordCursor, live, s.ScreenshotFolder);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Services.Notifications.Show(NotificationKind.Warning, "Не удалось открыть выделение области");
        }
    }

    /// <summary>
    /// На каком мониторе снимать и можно ли взять готовый кадр у буфера записи.
    ///
    /// Снимаем там, где курсор: настройка записи привязана к игре, а скриншот делают
    /// на любом мониторе. Кадр у работающего буфера при этом годится только когда
    /// это ТОТ ЖЕ монитор — иначе в снимок попал бы соседний экран.
    /// </summary>
    private static (int Monitor, Core.Capture.LiveFrameProvider? Live) ScreenshotTarget(bool forRegion = false)
    {
        var s = Services.Settings.Current;
        int? underCursor = Core.Capture.MonitorLayout.IndexUnderCursor();
        int monitor = underCursor ?? s.MonitorIndex;
        bool live = monitor == s.MonitorIndex;
        if (!live)
            Log.Info("Screenshot", $"Снимок на мониторе {monitor}, запись идёт с {s.MonitorIndex} — " +
                                   "живой кадр не подходит, открываю свою сессию");
        if (!live) return (monitor, null);
        // Оверлею выделения курсор в картинке не нужен: настоящий курсор ходит поверх
        // замороженного экрана, и вшитый выглядел бы вторым.
        return (monitor, forRegion
            ? Services.Engine.TryUseLiveFrameWithoutCursor
            : Services.Engine.TryUseLiveFrame);
    }

    /// <summary>Папка со скриншотами — открывается из трея и со страницы настроек.</summary>
    public static void OpenScreenshotFolder()
    {
        string path = Services.Settings.Current.ScreenshotFolder;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenRecordingsFolder()
    {
        string path = Services.Settings.Current.SaveRootPath;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static async Task CheckUpdatesAsync()
    {
        var info = await Services.Updates.CheckAsync();
        if (info is null) return;
        Services.Notifications.Show(NotificationKind.Warning, $"Есть версия {info.Version}",
                                    "Обновить можно в разделе «Приложение»");
    }

    // ---------------- Трей ----------------

    private void InitTray()
    {
        var menu = new ContextMenu { Style = (Style)Resources["TrayMenu"] };

        // Иконка у каждого пункта — как в меню карточки записи: меню в трее
        // открывают на бегу, и по контуру нужный пункт находится быстрее, чем по
        // тексту. Цвет по умолчанию приглушённый, у «Выхода» — красный.
        MenuItem Add(string header, string icon, Action click, string brush = "Tx2Brush")
        {
            var item = new MenuItem
            {
                Header = header,
                Style = (Style)Resources["TrayMenuItem"],
                Icon = new Controls.Icon
                {
                    Data = (System.Windows.Media.Geometry)Resources[icon],
                    Size = 14,
                    Foreground = (System.Windows.Media.Brush)Resources[brush]
                }
            };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }

        Add("Открыть Aura", "Ico.Aura", ShowMainWindow);
        _trayToggle = Add("Включить повтор", "Ico.Rec", ToggleEngine);
        _traySave = Add("Сохранить повтор", "Ico.Save", () => Services.Engine.SaveReplay());
        Add("Скриншот", "Ico.Camera", () => _ = TakeScreenshotAsync());
        Add("Скриншот области", "Ico.Camera", () => _ = TakeRegionScreenshotAsync());
        Add("Папка с записями", "Ico.FolderOpen", OpenRecordingsFolder);
        menu.Items.Add(new Separator { Style = (Style)Resources["TrayMenuSeparator"] });
        Add("Выход", "Ico.X", ExitApp, "RecBrush");

        _tray = new TaskbarIcon
        {
            ToolTipText = "Aura",
            ContextMenu = menu,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMainWindow();

        // Меню открываем сами: у иконки в трее нет окна-владельца, и без
        // явного показа с курсора правый клик не давал вообще ничего.
        _tray.TrayRightMouseUp += (_, _) =>
        {
            UpdateTray();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            // Меню закрывается по клику мимо, только если владелец активен
            menu.Focus();
        };

        TryCreateTray(attempt: 1);
    }

    /// <summary>Сколько раз пробуем создать значок, прежде чем сдаться.</summary>
    private const int TrayAttempts = 8;

    /// <summary>
    /// Создать значок в трее, повторяя попытки с растущей паузой.
    ///
    /// ЗАЧЕМ. При автозапуске по входу в систему приложение стартует раньше
    /// оболочки: области уведомлений ещё нет, и Shell_NotifyIcon отвечает отказом —
    /// H.NotifyIcon превращает его в «TryCreate failed». Раньше это исключение
    /// вылетало из OnStartup и обрывало ВЕСЬ остаток запуска: ни окна, ни движка,
    /// процесс просто завершался. Explorer поднимается за секунды, поэтому
    /// достаточно подождать и попробовать снова.
    /// </summary>
    private void TryCreateTray(int attempt)
    {
        try
        {
            _tray!.ForceCreate();
            UpdateTray();
            if (attempt > 1) Log.Info("App", $"Значок в трее создан с попытки {attempt}");
            return;
        }
        catch (Exception ex)
        {
            if (attempt == 1)
                Log.Warn("App", $"Область уведомлений ещё не готова ({ex.Message}) — повторю попытки");

            if (attempt >= TrayAttempts)
            {
                Log.Error("App", $"Значок в трее так и не создался за {TrayAttempts} попыток — " +
                                 "приложение работает без него");
                return;
            }

            // 1, 2, 4, 8… но не дольше полуминуты между попытками
            var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1)));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TryCreateTray(attempt + 1);
            };
            timer.Start();
        }
    }

    /// <summary>Трей показывает реальное состояние: подсказка и пункты меню.</summary>
    private void UpdateTray()
    {
        if (_tray is null) return;
        var engine = Services.Engine;
        bool running = engine.ReplayActive;

        _tray.ToolTipText = engine.IsRecordingToFile ? "Aura: идёт запись"
                          : running ? "Aura: повтор пишется" : "Aura: выключено";
        if (_trayToggle is not null) _trayToggle.Header = running ? "Выключить повтор" : "Включить повтор";
        if (_traySave is not null) _traySave.IsEnabled = engine.State == EngineState.Running;
    }

    /// <summary>
    /// Ждём сигнала от новых копий приложения в фоне: именованное событие работает
    /// и при спрятанном окне, и когда окна ещё нет (запуск свёрнутым в трей).
    /// </summary>
    private void StartShowWindowListener()
    {
        try { _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName); }
        catch (Exception ex) { Log.Warn("App", $"Сигнал показа окна недоступен: {ex.Message}"); return; }

        new Thread(() =>
        {
            while (true)
            {
                try { _showWindowSignal.WaitOne(); } catch { return; }
                Services.Ui.Enqueue(ShowMainWindow);
            }
        })
        { IsBackground = true, Name = "ShowWindowSignal" }.Start();
    }

    public void ShowMainWindow() => Services.Ui.Enqueue(() =>
    {
        // Страховка на случай, если окно всё же оказалось закрытым: показать такое
        // нельзя, WPF бросает исключение прямо из обработчика сообщения окна — оно
        // не проходит через диспетчер и убивает процесс. Проверяем и пересоздаём.
        if (_main is not null && IsClosed(_main)) _main = null;

        _main ??= new MainWindow();
        MainWindow = _main;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();

        // Спрятанное окно после Activate остаётся за активным (обычно за игрой) —
        // поднимаем его явно.
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_main).Handle;
            Core.Interop.NativeMethods.ShowWindow(hwnd, Core.Interop.NativeMethods.SW_RESTORE);
            Core.Interop.NativeMethods.SetForegroundWindow(hwnd);
        }
        catch { }

        // Запуск свёрнутым в трей — обычное дело (автозапуск вместе с Windows), и
        // «что нового» ждёт здесь: показывать список поверх игры незачем.
        ShowChangelogIfUpdated();
    });

    /// <summary>Закрыто ли окно: у WPF нет публичного признака, спрашиваем по-другому.</summary>
    private static bool IsClosed(Window window)
    {
        try
        {
            // У закрытого окна источник представления уже уничтожен, а хендл сброшен.
            var source = System.Windows.PresentationSource.FromVisual(window);
            if (source is not null) return false;
            return new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero
                   && !window.IsLoaded;
        }
        catch { return true; }
    }

    /// <summary>Когда в последний раз показывали уведомление о сбое интерфейса.</summary>
    private static DateTime _lastUiFailureShown = DateTime.MinValue;

    /// <summary>
    /// Сказать пользователю, что действие не выполнилось, и куда смотреть.
    /// Само уведомление тоже может упасть — тогда остаёмся с записью в логе,
    /// но процесс не роняем: мы уже внутри обработчика последнего рубежа.
    /// </summary>
    private static void ReportUiFailure(Exception ex)
    {
        try
        {
            if ((DateTime.UtcNow - _lastUiFailureShown).TotalSeconds < 30) return;
            _lastUiFailureShown = DateTime.UtcNow;

            Services.Notifications.Show(NotificationKind.Warning,
                "Действие не выполнилось",
                $"{ex.Message} · подробности в логе (Настройки → Открыть логи)");
        }
        catch (Exception inner)
        {
            Log.Warn("App", $"Не удалось показать уведомление о сбое: {inner.Message}");
        }
    }

    /// <summary>
    /// Выход из приложения. Каждый шаг разбора — в своём try/catch.
    ///
    /// ЗАЧЕМ. Раньше обёрнут был только MFShutdown, а Engine.Dispose и Hotkeys.Dispose
    /// шли голыми. Исключение при остановке конвейера улетало в глобальный обработчик,
    /// тот ставил Handled = true, и до Environment.Exit(0) дело не доходило: приложение
    /// оставалось жить с полуразобранным движком, а «Выход» переставал работать вовсе.
    /// Выход обязан доводиться до конца, чем бы ни закончился любой отдельный шаг.
    /// </summary>
    public void ExitApp()
    {
        Step("настройки", () => Services.Settings.FlushDeferred());
        Step("события системы", () => Services.Activity.Dispose());
        Step("слежение за папкой", () => Services.Storage.Dispose());
        Step("движок", () => Services.Engine.Dispose());
        Step("хоткеи", () => Services.Hotkeys.Dispose());
        Step("значок в трее", () => _tray?.Dispose());
        Step("Media Foundation", () => MediaFactory.MFShutdown());
        Step("таймер", () => Core.Interop.NativeMethods.timeEndPeriod(1));

        // Environment.Exit убивает фоновый поток лога на месте — сначала даём ему
        // дописать хвост, иначе итоги остановки конвейера до диска не доезжают.
        Step("лог", Log.Shutdown);
        Environment.Exit(0);

        static void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("App", $"Выход, шаг «{what}»: {ex.Message}"); }
        }
    }
}
