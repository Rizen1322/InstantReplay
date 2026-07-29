using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InstantReplay.Core.Engine;
using InstantReplay.Core.Storage;
using InstantReplay.Views;

namespace InstantReplay;

/// <summary>Ссылка на главное окно для диалогов/пикеров из страниц.</summary>
public static class WindowTracker
{
    public static Window? Main { get; set; }
}

public sealed partial class MainWindow : Window
{
    // Секунда, а не полсекунды: в шапке нет ничего, что меняется чаще (буфер считается
    // в целых секундах), а тик — это обход состояния движка и сравнение строк.
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _syncingSwitch;

    private static readonly Type[] Pages =
    [
        typeof(OverviewPage), typeof(RecordingPage), typeof(ClipsPage),
        typeof(KeysFilesPage), typeof(AppPage), typeof(HardwarePage)
    ];

    public MainWindow()
    {
        InitializeComponent();
        WindowTracker.Main = this;

        // Fluent: Mica-подложка, тема из настроек, кастомный титлбар
        SystemBackdrop = new MicaBackdrop();
        ApplyTheme();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 1000));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); } catch { }

        // Минимальный размер окна: уже — и шапка с кнопками/строки настроек перестают влезать.
        // (OverlappedPresenter.PreferredMinimumWidth появился только в WinAppSDK 1.7)
        InstallMinSizeHook();

        Tabs.SelectedItem = TabOverview;

        Services.Engine.StateChanged += _ => Services.Dispatcher.Enqueue(RefreshStatus);
        Services.Engine.RecordingChanged += _ => Services.Dispatcher.Enqueue(RefreshStatus);
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();

        // Закрытие окна = сворачивание в трей (движок продолжает писать буфер).
        // Таймер статуса при этом останавливаем: в трее он полсекунды за полсекундой
        // пересоздавал кисти и строки в никуда. Движок и запись это не затрагивает.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
            _statusTimer.Stop();
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_statusTimer.IsEnabled) return;
            RefreshStatus();
            _statusTimer.Start();
        };
    }

    // ---------------- Минимальный размер окна ----------------
    private const int MinWidthDip = 900, MinHeightDip = 560;
    private Core.Interop.NativeMethods.WndProcDelegate? _wndProc; // держим от GC
    private IntPtr _prevWndProc;

    private void InstallMinSizeHook()
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _wndProc = (h, msg, wParam, lParam) =>
        {
            if (msg == Core.Interop.NativeMethods.WM_GETMINMAXINFO)
            {
                var mmi = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<Core.Interop.NativeMethods.MINMAXINFO>(lParam);
                double scale = Core.Interop.NativeMethods.GetDpiForWindow(h) / 96.0;
                mmi.ptMinTrackSize.X = (int)(MinWidthDip * scale);
                mmi.ptMinTrackSize.Y = (int)(MinHeightDip * scale);
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
            }
            return Core.Interop.NativeMethods.CallWindowProcW(_prevWndProc, h, msg, wParam, lParam);
        };
        _prevWndProc = Core.Interop.NativeMethods.SetWindowLongPtrW(
            hwnd, Core.Interop.NativeMethods.GWLP_WNDPROC, _wndProc);
    }

    public void ApplyTheme()
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = Services.Settings.Current.Theme switch
            {
                Core.Settings.AppTheme.Light => ElementTheme.Light,
                Core.Settings.AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
    }

    // Кисти статуса создаются один раз. Раньше каждый тик таймера пересоздавал по две
    // SolidColorBrush и заново присваивал одни и те же строки — мусор в куче каждые
    // полсекунды всё время, пока открыто окно.
    private static readonly SolidColorBrush DotRunning = new(Color("#4CD964"));
    private static readonly SolidColorBrush DotRecording = new(Colors.OrangeRed);
    private static readonly SolidColorBrush DotOff = new(Colors.Gray);
    private static readonly SolidColorBrush RecDotIdle = new(Color("#B0B0B0"));

    // Последнее показанное состояние: обновляем элементы только на реальных изменениях
    private Brush? _shownStatusDot, _shownRecDot;
    private string _shownStatus = "", _shownDetail = "", _shownRecText = "";

    private void RefreshStatus()
    {
        var e = Services.Engine;
        bool running = e.State != EngineState.Stopped;
        bool recording = e.IsRecordingToFile;

        Brush dot = recording ? DotRecording : running ? DotRunning : DotOff;
        if (!ReferenceEquals(dot, _shownStatusDot))
        {
            StatusDot.Fill = dot;
            _shownStatusDot = dot;
        }

        string status = e.State switch
        {
            // Проценты вместо немой паузы: на длинном клипе сохранение занимает секунды,
            // и раньше было не понять, идёт ли оно вообще
            EngineState.Saving => $"Сохранение… {e.SaveProgress * 100:0}%",
            _ when recording => "Идёт запись",
            EngineState.Running => "Запись в буфер",
            _ => "Выключено"
        };
        if (status != _shownStatus)
        {
            StatusText.Text = status;
            _shownStatus = status;
        }

        string detail = "";
        if (running)
        {
            int sec = (int)e.BufferedDuration.TotalSeconds;
            string label = e.EncoderLabel is { Length: > 0 } l ? $"· {l} " : "";
            detail = $"{label}· буфер {FormatSec(sec)} ({ByteSize.Format(e.BufferedBytes)})";
        }
        if (detail != _shownDetail)
        {
            StatusDetail.Text = detail;
            _shownDetail = detail;
        }

        bool canSave = e.State == EngineState.Running;
        if (SaveReplayBtn.IsEnabled != canSave) SaveReplayBtn.IsEnabled = canSave;

        string recText = recording ? "Остановить запись" : "Начать запись";
        if (recText != _shownRecText)
        {
            RecBtnText.Text = recText;
            _shownRecText = recText;
        }

        Brush recDot = recording ? DotRecording : RecDotIdle;
        if (!ReferenceEquals(recDot, _shownRecDot))
        {
            RecDot.Fill = recDot;
            _shownRecDot = recDot;
        }

        if (BufferSwitch.IsOn != running)
        {
            _syncingSwitch = true;
            BufferSwitch.IsOn = running;
            _syncingSwitch = false;
        }
    }

    /// <summary>Переключить вкладку из кода (например «Вся панорама» на «Обзоре»).</summary>
    public void SelectTab(Type pageType)
    {
        int index = Array.IndexOf(Pages, pageType);
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedItem = Tabs.Items[index];
    }

    private static string FormatSec(int sec) =>
        sec >= 60 ? $"{sec / 60} мин {sec % 60} сек" : $"{sec} сек";

    private static Windows.UI.Color Color(string hex) =>
        Windows.UI.Color.FromArgb(255,
            System.Convert.ToByte(hex.Substring(1, 2), 16),
            System.Convert.ToByte(hex.Substring(3, 2), 16),
            System.Convert.ToByte(hex.Substring(5, 2), 16));

    // ---------------- Действия шапки ----------------

    private void Buffer_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSwitch) return;
        try
        {
            if (BufferSwitch.IsOn) Services.Engine.Start();
            else Services.Engine.Stop();
        }
        catch (Exception ex)
        {
            RefreshStatus();
            ShowError("Не удалось запустить запись", ex.Message);
        }
    }

    private void Rec_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Services.Engine.IsRecordingToFile) Services.Engine.StopRecordingToFile();
            else Services.Engine.StartRecordingToFile();
        }
        catch (Exception ex)
        {
            ShowError("Не удалось начать запись", ex.Message);
        }
        RefreshStatus();
    }

    private void SaveReplay_Click(object sender, RoutedEventArgs e) => Services.Engine.SaveReplay();

    private void ShowError(string title, string message)
    {
        _ = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0 && index < Pages.Length)
            ContentFrame.Navigate(Pages[index], null,
                new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
    }
}
