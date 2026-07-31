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
    public static UiDispatcher Ui { get; private set; } = null!;

    public static void Init()
    {
        Ui = new UiDispatcher(Application.Current.Dispatcher);
        Storage = new StorageManager(Settings);
        Engine = new ReplayEngine(Settings, Storage);
        Hotkeys = new HotkeyService(Settings);
        Notifications = new NotificationService(Settings, Ui);
        DriverWatch = new DriverWatch(Settings, Nvidia);
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

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("App", args.Exception);
            args.Handled = true;
        };

        Log.Init();
        Core.Interop.NativeMethods.timeBeginPeriod(1); // точный Sleep для конвейера записи и звука
        RaiseGpuPriority();

        Services.Settings.Load();
        Loc.Lang = Services.Settings.Current.Language;
        ApplyTheme(Services.Settings.Current.Theme);

        MediaFactory.MFStartup(); // Media Foundation — один раз на процесс

        Services.Init();
        Views.ClipCommands.Register();
        WireEvents();
        GuardCodec();

        StartupManager.Reconcile(Services.Settings.Current.AutoStartWithWindows);
        Services.Hotkeys.Start();
        InitTray();
        StartShowWindowListener();

        // В режиме --dev окно показываем всегда: иначе с включённым «стартовать
        // свёрнутым в трей» отладочная копия молча уходит в трей.
        bool minimized = !dev &&
            (e.Args.Contains("--minimized") || Services.Settings.Current.StartMinimizedToTray);
        _main = new MainWindow();
        int pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length) _main.StartPage = e.Args[pageArg + 1];
        MainWindow = _main;
        if (!minimized) _main.Show();

        if (Services.Settings.Current.AutoStartReplayBuffer) SafeStartEngine();
        if (Services.Settings.Current.CheckForUpdates) _ = CheckUpdatesAsync();

        Services.DriverWatch.UpdateFound += version => Services.Notifications.Show(
            NotificationKind.Warning, Loc.T("driver_found", version));
        Services.DriverWatch.Start();
    }

    /// <summary>
    /// Кодек, который система не сможет упаковать в MP4, молча превращает запись
    /// в пустоту: буфер пишется, а каждое сохранение падает. Уводим на рабочий
    /// кодек сразу при запуске и говорим об этом вслух.
    /// </summary>
    private static void GuardCodec()
    {
        var settings = Services.Settings.Current;
        if (Core.Encoding.VideoEncoder.CanSaveToMp4(settings.Codec)) return;

        var fallback = Core.Settings.VideoCodec.H264;
        try
        {
            var (_, codecs) = Core.Encoding.VideoEncoder.ProbeSupport();
            if (codecs.Contains(Core.Settings.VideoCodec.HEVC)) fallback = Core.Settings.VideoCodec.HEVC;
        }
        catch { }

        Log.Warn("App", $"{settings.Codec} нельзя сохранить в MP4 на этой Windows — переключаю на {fallback}");
        settings.Codec = fallback;
        Services.Settings.Save("video");
        Services.Notifications.Show(NotificationKind.Warning, "AV1 здесь не сохраняется",
                                    $"Переключил на {(fallback == Core.Settings.VideoCodec.HEVC ? "HEVC" : "H.264")}");
    }

    /// <summary>GPU-приоритет повыше: команды записи не ждут в очереди за игрой.</summary>
    private static void RaiseGpuPriority()
    {
        try
        {
            int st = Core.Interop.NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(
                Core.Interop.NativeMethods.GetCurrentProcess(),
                Core.Interop.NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
            Log.Info("App", st == 0 ? "GPU-приоритет процесса: HIGH" : $"GPU-приоритет не применился (0x{st:X8})");
        }
        catch { }
    }

    // ---------------- Тема ----------------

    /// <summary>Смена палитры на лету: подменяем первый словарь в MergedDictionaries.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };
        var uri = new Uri(dark ? "Theme/Palette.Dark.xaml" : "Theme/Palette.Light.xaml", UriKind.Relative);
        var dictionaries = Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = uri };
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

        EngineState prev = EngineState.Stopped;
        engine.StateChanged += _ => Services.Ui.Enqueue(UpdateTray);
        engine.RecordingChanged += _ => Services.Ui.Enqueue(UpdateTray);
        engine.StateChanged += state =>
        {
            if (state == EngineState.Running && prev == EngineState.Stopped)
                n.Show(NotificationKind.ReplayOn, "Повтор включён");
            else if (state == EngineState.Stopped && prev != EngineState.Stopped)
                n.Show(NotificationKind.Stopped, "Повтор выключен");
            prev = state;
        };

        // Сохранение показывается в два этапа: снимок буфера — уже гарантия клипа,
        // диск догоняет в фоне, и карточка дорисовывается по факту записи файла.
        engine.ReplayCaptured += _ => n.ShowSaving("Сохраняю повтор…");
        engine.ReplaySaved += (file, seconds) =>
        {
            n.CompleteSaving("Повтор сохранён", $"{Loc.Duration(seconds)} · {Describe(file)}");
            AfterFileSaved(file);
            Views.ClipCommands.NotifyLibraryChanged();
        };
        engine.SaveFailed += msg => n.Show(NotificationKind.Warning, "Не удалось сохранить", msg);
        engine.Warning += msg => n.Show(NotificationKind.Warning, msg);

        engine.RecordingChanged += rec => { if (rec) n.Show(NotificationKind.Recording, "Запись началась"); };
        engine.RecordingSaved += (file, seconds) =>
        {
            n.Show(NotificationKind.Saved, "Запись сохранена", $"{Loc.Duration(seconds)} · {Describe(file)}");
            AfterFileSaved(file);
            Views.ClipCommands.NotifyLibraryChanged();
        };

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
                case HotkeyAction.OpenFolder: OpenRecordingsFolder(); break;
            }
        });

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

    private static void AfterFileSaved(string file)
    {
        if (!Services.Settings.Current.OpenFolderAfterSave) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch { }
    }

    // ---------------- Действия ----------------

    public static void SafeStartEngine()
    {
        try { Services.Engine.Start(); }
        catch (Exception ex) { Services.Notifications.Show(NotificationKind.Warning, "Не удалось включить повтор", ex.Message); }
    }

    public static void SafeStartRecording()
    {
        try { Services.Engine.StartRecordingToFile(); }
        catch (Exception ex) { Services.Notifications.Show(NotificationKind.Warning, "Не удалось начать запись", ex.Message); }
    }

    public static void ToggleEngine()
    {
        if (Services.Engine.State == EngineState.Stopped) SafeStartEngine();
        else Services.Engine.Stop();
    }

    public static async Task TakeScreenshotAsync()
    {
        var s = Services.Settings.Current;
        try
        {
            string game = Core.GameDetection.GameDetector.DetectForegroundGame();
            string dir = s.GroupByGame ? Path.Combine(s.SaveRootPath, game) : s.SaveRootPath;
            string file = Path.Combine(dir, $"screenshot {DateTime.Now:yyyy-MM-dd - HH-mm-ss}.png");

            await Task.Run(() => Core.Capture.ScreenshotService.CaptureAsync(
                s.MonitorIndex, file, s.RecordCursor, Services.Engine.TryUseLiveFrame));
            Services.Storage.RegisterSaved(file);
            Services.Notifications.Show(NotificationKind.Screenshot, "Скриншот сохранён", Describe(file));
            Services.Ui.Enqueue(Views.ClipCommands.NotifyLibraryChanged);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Services.Notifications.Show(NotificationKind.Warning, "Не удалось сделать скриншот");
        }
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
                                    "Обновить — на вкладке «Приложение»");
    }

    // ---------------- Трей ----------------

    private void InitTray()
    {
        var menu = new ContextMenu { Style = (Style)Resources["TrayMenu"] };
        MenuItem Add(string header, Action click)
        {
            var item = new MenuItem { Header = header, Style = (Style)Resources["TrayMenuItem"] };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }

        Add("Открыть Aura", ShowMainWindow);
        _trayToggle = Add("Включить повтор", ToggleEngine);
        _traySave = Add("Сохранить повтор", () => Services.Engine.SaveReplay());
        Add("Скриншот", () => _ = TakeScreenshotAsync());
        Add("Папка с записями", OpenRecordingsFolder);
        menu.Items.Add(new Separator { Margin = new Thickness(8, 4, 8, 4) });
        Add("Выход", ExitApp);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Aura",
            ContextMenu = menu,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico")))
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

        _tray.ForceCreate();
        UpdateTray();
    }

    /// <summary>Трей показывает реальное состояние: подсказка и пункты меню.</summary>
    private void UpdateTray()
    {
        if (_tray is null) return;
        var engine = Services.Engine;
        bool running = engine.State != EngineState.Stopped;

        _tray.ToolTipText = engine.IsRecordingToFile ? "Aura — идёт запись"
                          : running ? "Aura — повтор пишется" : "Aura — выключено";
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
    });

    public void ExitApp()
    {
        Services.Engine.Dispose();
        Services.Hotkeys.Dispose();
        _tray?.Dispose();
        try { MediaFactory.MFShutdown(); } catch { }
        Core.Interop.NativeMethods.timeEndPeriod(1);
        Environment.Exit(0);
    }
}
