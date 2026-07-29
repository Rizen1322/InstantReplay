using System.Diagnostics;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Vortice.MediaFoundation;
using InstantReplay.Core.Engine;
using InstantReplay.Core.Hardware;
using InstantReplay.Core.Hotkeys;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Notifications;
using InstantReplay.Core.Settings;
using InstantReplay.Core.Storage;
using InstantReplay.Core.SystemIntegration;

namespace InstantReplay;

/// <summary>Статический контейнер сервисов приложения.</summary>
public static class Services
{
    public static SettingsManager Settings { get; } = new();
    public static StorageManager Storage { get; private set; } = null!;
    public static ReplayEngine Engine { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static NotificationService Notifications { get; private set; } = null!;
    public static UpdateService Updates { get; } = new();
    public static NvidiaDriverService Nvidia { get; } = new();
    public static DispatcherQueueHolder Dispatcher { get; private set; } = null!;

    public static void Init(DispatcherQueue uiQueue)
    {
        Dispatcher = new DispatcherQueueHolder(uiQueue);
        Storage = new StorageManager(Settings);
        Engine = new ReplayEngine(Settings, Storage);
        Hotkeys = new HotkeyService(Settings);
        Notifications = new NotificationService(Settings, Dispatcher);
    }
}

public partial class App : Application
{
    /// <summary>Имена объектов синхронизации одной копии приложения.</summary>
    private const string InstanceMutexName = @"Global\InstantReplay.SingleInstance";
    private const string ShowWindowEventName = @"Global\InstantReplay.ShowWindow";

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showWindowSignal;
    private MainWindow? _mainWindow;
    private TaskbarIcon? _tray;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log.Error("App", e.Exception); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Одна копия приложения (иначе конфликт хоткеев и двойной захват).
        // Вторая копия не умирает молча — она просит первую показать окно: запуск
        // ярлыка при уже работающем приложении раньше выглядел как «не запускается».
        _singleInstance = new Mutex(true, InstanceMutexName, out bool isNew);
        if (!isNew)
        {
            RequestExistingInstanceToShow();
            Environment.Exit(0);
            return;
        }

        Log.Init();
        Core.Interop.NativeMethods.timeBeginPeriod(1); // точный Sleep для конвейера записи и аудио
        // GPU-приоритет повыше: команды записи не ждут в очереди за игрой
        try
        {
            int st = Core.Interop.NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(
                Core.Interop.NativeMethods.GetCurrentProcess(),
                Core.Interop.NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
            Log.Info("App", st == 0 ? "GPU-приоритет процесса: HIGH"
                                    : $"GPU-приоритет не применился (0x{st:X8})");
        }
        catch { }
        Services.Settings.Load();
        Loc.Lang = Services.Settings.Current.Language;

        MediaFactory.MFStartup(); // Media Foundation — один раз на процесс

        Services.Init(DispatcherQueue.GetForCurrentThread());
        WireEvents();

        // Сверяем автозапуск с реестром: запись могла пропасть после переустановки
        // (установщик перезаписывает ветку Run) или смениться путь к exe.
        StartupManager.Reconcile(Services.Settings.Current.AutoStartWithWindows);

        Services.Hotkeys.Start();
        InitTray();

        StartShowWindowListener();

        bool minimized = Environment.GetCommandLineArgs().Contains("--minimized")
                         || Services.Settings.Current.StartMinimizedToTray;
        _mainWindow = new MainWindow();
        if (!minimized) _mainWindow.Activate();

        if (Services.Settings.Current.AutoStartReplayBuffer)
            SafeStartEngine();

        if (Services.Settings.Current.CheckForUpdates)
            _ = CheckUpdatesAsync();
    }

    /// <summary>Сигнал уже работающей копии: «покажи окно». Ошибки не важны — мы всё равно выходим.</summary>
    private static void RequestExistingInstanceToShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
                using (signal) signal.Set();
        }
        catch { }
    }

    /// <summary>
    /// Ждём сигнала от новых копий приложения в фоновом потоке. Именованное событие,
    /// а не оконное сообщение: работает при спрятанном окне и не зависит от того,
    /// создано ли оно вообще (запуск свёрнутым в трей).
    /// </summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"Сигнал показа окна недоступен: {ex.Message}");
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try { _showWindowSignal.WaitOne(); }
                catch { return; }
                ShowMainWindow();
            }
        })
        { IsBackground = true, Name = "ShowWindowSignal" };
        thread.Start();
    }

    private void WireEvents()
    {
        var n = Services.Notifications;
        var e = Services.Engine;

        // Превью кадра в уведомлении о сохранении берётся у работающего буфера
        n.PreviewSource = e.TryUseLiveFrame;

        // Уведомляем только о реальных вкл/выкл. Переход Saving→Running — служебный:
        // раньше он показывал «Instant Replay включен» сразу после «Сохранено» и перебивал его.
        EngineState prevState = EngineState.Stopped;
        e.StateChanged += _ => Services.Dispatcher.Enqueue(UpdateTray);
        e.RecordingChanged += _ => Services.Dispatcher.Enqueue(UpdateTray);
        e.StateChanged += st =>
        {
            if (st == EngineState.Running && prevState == EngineState.Stopped)
                n.Show(NotificationKind.ReplayOn, Loc.T("replay_on"));
            else if (st == EngineState.Stopped && prevState != EngineState.Stopped)
                n.Show(NotificationKind.Stopped, Loc.T("replay_off"));
            prevState = st;
        };
        e.ReplaySaved += (file, seconds) =>
        {
            n.Show(NotificationKind.Saved, Loc.T("saved", Loc.Duration(seconds)));
            AfterFileSaved(file);
        };
        e.SaveFailed += msg => n.Show(NotificationKind.Warning, Loc.T("save_failed") + ": " + msg);
        // Потеря GPU-устройства и восстановление после неё — пользователь должен знать,
        // что запись прерывалась, а не догадываться по дыре в клипе.
        e.Warning += msg => n.Show(NotificationKind.Warning, msg);
        e.RecordingChanged += rec =>
        {
            if (rec) n.Show(NotificationKind.Recording, Loc.T("rec_started"));
        };
        e.RecordingSaved += (file, seconds) =>
        {
            n.Show(NotificationKind.Saved, Loc.T("rec_saved", Loc.Duration(seconds)));
            AfterFileSaved(file);
        };

        Services.Hotkeys.HotkeyPressed += action => Services.Dispatcher.Enqueue(() =>
        {
            switch (action)
            {
                case HotkeyAction.SaveReplay: e.SaveReplay(); break;
                case HotkeyAction.SaveLast30: e.SaveReplay(30); break;
                case HotkeyAction.StartRecording: SafeStartRecording(); break;
                case HotkeyAction.StopRecording: e.StopRecordingToFile(); break;
                case HotkeyAction.ToggleInstantReplay: ToggleEngine(); break;
                case HotkeyAction.Screenshot: _ = TakeScreenshotAsync(); break;
                case HotkeyAction.OpenFolder: OpenRecordingsFolder(); break;
            }
        });

        // Автозапуск с Windows — применяем при изменении настроек
        Services.Settings.Changed += g =>
        {
            if (g is "" or "system")
                StartupManager.SetEnabled(Services.Settings.Current.AutoStartWithWindows);
            if (g is "" or "ui")
                Loc.Lang = Services.Settings.Current.Language;
        };
    }

    /// <summary>После сохранения файла: опционально открыть папку с выделенным файлом.</summary>
    private static void AfterFileSaved(string file)
    {
        if (!Services.Settings.Current.OpenFolderAfterSave) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static void SafeStartRecording()
    {
        try { Services.Engine.StartRecordingToFile(); }
        catch (Exception ex)
        {
            Services.Notifications.Show(NotificationKind.Warning, ex.Message);
        }
    }

    /// <summary>Скриншот текущего монитора захвата → PNG в папку записей.</summary>
    public static async Task TakeScreenshotAsync()
    {
        var s = Services.Settings.Current;
        try
        {
            string game = Core.GameDetection.GameDetector.DetectForegroundGame();
            string dir = s.GroupByGame ? Path.Combine(s.SaveRootPath, game) : s.SaveRootPath;
            string file = Path.Combine(dir, $"screenshot {DateTime.Now:yyyy-MM-dd - HH-mm-ss}.png");
            // Кадр берём у работающего буфера, если он включён (см. ScreenshotService)
            await Core.Capture.ScreenshotService.CaptureAsync(
                s.MonitorIndex, file, s.RecordCursor, Services.Engine.TryUseLiveFrame);
            Services.Storage.RegisterSaved(file); // индекс папки записей
            Services.Notifications.Show(NotificationKind.Screenshot, Loc.T("shot_saved"));
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Services.Notifications.Show(NotificationKind.Warning, Loc.T("shot_failed"));
        }
    }

    private static async Task CheckUpdatesAsync()
    {
        var info = await Services.Updates.CheckAsync();
        if (info is null) return;
        Services.Notifications.Show(NotificationKind.Warning, Loc.T("update_found", info.Version));
    }

    private static void SafeStartEngine()
    {
        try { Services.Engine.Start(); }
        catch (Exception ex)
        {
            Services.Notifications.Show(NotificationKind.Warning, ex.Message);
        }
    }

    private static void ToggleEngine()
    {
        if (Services.Engine.State == EngineState.Stopped) SafeStartEngine();
        else Services.Engine.Stop();
    }

    public static void OpenRecordingsFolder()
    {
        string path = Services.Settings.Current.SaveRootPath;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ---------------- Трей ----------------
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _trayToggleItem;
    private Microsoft.UI.Xaml.Controls.MenuFlyoutItem? _traySaveItem;

    private void InitTray()
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        Microsoft.UI.Xaml.Controls.MenuFlyoutItem Add(string text, Action click)
        {
            var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = text };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }
        Add(Loc.T("tray_open"), ShowMainWindow);
        _trayToggleItem = Add(Loc.T("tray_toggle"), ToggleEngine);
        _traySaveItem = Add(Loc.T("tray_save"), () => Services.Engine.SaveReplay());
        Add(Loc.T("tray_screenshot"), () => _ = TakeScreenshotAsync());
        Add(Loc.T("tray_folder"), OpenRecordingsFolder);
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        Add(Loc.T("tray_exit"), ExitApp);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Instant Replay",
            ContextFlyout = menu,
            // Unpackaged WinUI: всплывающее меню должно жить в отдельном окне,
            // иначе клики по пунктам не доходят (PopupMenu требует XamlRoot пакета)
            ContextMenuMode = ContextMenuMode.SecondWindow,
            IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/tray.ico"))
        };
        _tray.LeftClickCommand = new RelayCmd(ShowMainWindow);
        _tray.ForceCreate();
        UpdateTray();
    }

    /// <summary>
    /// Трей отражает реальное состояние: подсказка при наведении и текст пунктов
    /// меню. Раньше и то, и другое было статичным — понять, пишется ли буфер,
    /// можно было только открыв окно.
    /// </summary>
    private void UpdateTray()
    {
        if (_tray is null) return;
        var e = Services.Engine;
        bool running = e.State != EngineState.Stopped;

        _tray.ToolTipText = Loc.T(e.IsRecordingToFile ? "tip_recording"
                                 : running ? "tip_buffering" : "tip_off");
        if (_trayToggleItem is not null)
            _trayToggleItem.Text = Loc.T(running ? "tray_turn_off" : "tray_turn_on");
        if (_traySaveItem is not null)
            _traySaveItem.IsEnabled = e.State == EngineState.Running;
    }

    private void ShowMainWindow()
    {
        Services.Dispatcher.Enqueue(() =>
        {
            _mainWindow ??= new MainWindow();
            _mainWindow.AppWindow.Show(); // окно было спрятано AppWindow.Hide() — одного Activate мало
            _mainWindow.Activate();

            // Свёрнутое/спрятанное окно после Activate остаётся за активным окном
            // (обычно за игрой) — поднимаем его явно.
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                Core.Interop.NativeMethods.ShowWindow(hwnd, Core.Interop.NativeMethods.SW_RESTORE);
                Core.Interop.NativeMethods.SetForegroundWindow(hwnd);
            }
            catch { }
        });
    }

    public void ExitApp()
    {
        Services.Engine.Dispose();
        Services.Hotkeys.Dispose();
        _tray?.Dispose();
        try { MediaFactory.MFShutdown(); } catch { }
        Core.Interop.NativeMethods.timeEndPeriod(1);
        Environment.Exit(0);
    }

    private sealed class RelayCmd(Action action) : System.Windows.Input.ICommand
    {
        // Команда всегда доступна, поэтому событие никого не уведомляет —
        // пустые аксессоры вместо поля, чтобы не было предупреждения CS0067.
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => action();
    }
}
