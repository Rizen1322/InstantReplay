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

        Log.Init();

        // Ловим падения ВСЕХ потоков, а не только интерфейсного. Конвейер записи
        // живёт в своих потоках (события MFT, питатель, пейсер, писатель файла), и
        // необработанное исключение в любом из них убивает процесс мгновенно — без
        // единой строки в логе, потому что обычные записи уходят через фоновую
        // очередь и не успевают дойти до диска. Отсюда были «молчаливые» падения.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal("App", $"Исключение в потоке интерфейса: {args.Exception}");
            args.Handled = true; // интерфейс переживёт, движок продолжит писать
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal("App", $"Необработанное исключение{(args.IsTerminating ? " (процесс завершается)" : "")}: " +
                             $"{args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal("App", $"Исключение в фоновой задаче: {args.Exception}");
            args.SetObserved();
        };
        Core.Interop.NativeMethods.timeBeginPeriod(1); // точный Sleep для конвейера записи и звука
        RaiseGpuPriority();

        Services.Settings.Load();
        Loc.Lang = Services.Settings.Current.Language;
        // --theme dark|deep|light|system — примерить тему, не трогая настройки.
        // Рядом с --page и --dev: примерка оформления не должна переписывать
        // settings.json пользователя.
        var theme = ThemeArg(e.Args) ?? Services.Settings.Current.Theme;
        ApplyTheme(theme);

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

        // Значки под тему — здесь, а не в ApplyTheme выше: там окна ещё не было,
        // а кнопку на панели задач рисует именно иконка окна.
        ApplyThemeIcons(theme);

        if (!minimized) _main.Show();
        ShowChangelogIfUpdated();

        if (Services.Settings.Current.AutoStartReplayBuffer) SafeStartEngine();
        if (Services.Settings.Current.CheckForUpdates) _ = CheckUpdatesAsync();

        Services.DriverWatch.UpdateFound += version => Services.Notifications.Show(
            NotificationKind.Warning, Loc.T("driver_found", version));
        Services.DriverWatch.Start();

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
        }
        catch (Exception ex) { Log.Warn("App", $"Уборка кэша: {ex.Message}"); }
    });

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
        string file = theme == AppTheme.Deep ? "tray-deep.ico" : "tray.ico";
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
            AppTheme.Deep => "Theme/Palette.Deep.xaml",
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
            Views.ClipCommands.NotifyLibraryChanged();
        };
        engine.SaveFailed += msg => n.Show(NotificationKind.Warning, "Не удалось сохранить", msg);
        engine.Warning += msg => n.Show(NotificationKind.Warning, msg);

        engine.RecordingChanged += rec => { if (rec) n.Show(NotificationKind.Recording, "Запись началась"); };
        engine.RecordingSaved += (file, seconds) =>
        {
            n.Show(NotificationKind.Saved, "Запись сохранена", $"{Loc.Duration(seconds)} · {Describe(file)}");
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
                case HotkeyAction.ScreenshotRegion: _ = TakeRegionScreenshotAsync(); break;
                case HotkeyAction.OpenFolder: OpenRecordingsFolder(); break;
            }
        });

        // Итог работы оверлея выделения: уведомления показывает приложение, а не окно —
        // оно к моменту показа уже закрыто.
        Views.RegionCaptureWindow.Saved += (file, copied) =>
            n.Show(NotificationKind.Screenshot,
                file is null ? "Скриншот в буфере обмена" : "Скриншот сохранён",
                file is null ? null : (copied ? "и скопирован" : Describe(file)));
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
            s.LastSeenVersion = current.ToString(3);
            Services.Settings.Save("app");
            return;
        }
        if (last >= current) return;

        var entries = Core.SystemIntegration.Changelog.Between(last, current);
        if (entries.Count == 0)
        {
            s.LastSeenVersion = current.ToString(3);
            Services.Settings.Save("app");
            return;
        }

        // Окна нет — ничего не отмечаем и ждём: проверку повторит ShowMainWindow,
        // когда человек откроет приложение. Подписываться на события этого окна
        // бесполезно: показ спрятанного окна пересоздаёт его заново (см. IsClosed),
        // и обработчик остался бы на выброшенном экземпляре.
        if (_main is null || !_main.IsVisible) return;

        Services.Ui.Enqueue(() => PresentChangelog(entries, current));
    }

    private static void PresentChangelog(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries, Version current)
    {
        Log.Info("App", $"Показываю список изменений до версии {current}");
        try { Views.Dialogs.ShowChangelog(entries); }
        catch (Exception ex) { Log.Warn("App", $"Список изменений: {ex.Message}"); }

        // Отметку ставим после показа: если диалог не открылся, человек не должен
        // потерять список из-за нашей ошибки.
        Services.Settings.Current.LastSeenVersion = current.ToString(3);
        Services.Settings.Save("app");
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
        if (!Core.Capture.ScreenCaptureFactory.UsesWgc) return;      // без WGC рамки нет вовсе
        if (Core.Capture.CaptureAccess.BorderlessGranted) return;

        Services.Notifications.Show(NotificationKind.Warning, "Windows рисует рамку записи",
            "Разрешите захват без рамки в параметрах конфиденциальности");

        var s = Services.Settings.Current;
        if (s.BorderlessPermissionAsked) return;
        s.BorderlessPermissionAsked = true;
        Services.Settings.Save("system");
        Core.Capture.CaptureAccess.OpenPermissionSettings();
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

            await Task.Run(() => Core.Capture.ScreenshotService.CaptureAsync(
                s.MonitorIndex, file, s.RecordCursor, Services.Engine.TryUseLiveFrame));
            Services.Notifications.Show(NotificationKind.Screenshot, "Скриншот сохранён", Describe(file));
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
            await Views.RegionCaptureWindow.ShowForAsync(
                s.MonitorIndex, s.RecordCursor, Services.Engine.TryUseLiveFrame, s.ScreenshotFolder);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
            Services.Notifications.Show(NotificationKind.Warning, "Не удалось открыть выделение области");
        }
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
                                    "Обновить — на вкладке «Приложение»");
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
