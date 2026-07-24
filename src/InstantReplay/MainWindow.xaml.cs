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
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _syncingSwitch;

    private static readonly Type[] Pages =
    [
        typeof(RecordingPage), typeof(AudioPage), typeof(HotkeysPage),
        typeof(StoragePage), typeof(AppPage), typeof(HardwarePage)
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

        Tabs.SelectedItem = TabRecording;

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

    private void RefreshStatus()
    {
        var e = Services.Engine;
        bool running = e.State != EngineState.Stopped;
        bool recording = e.IsRecordingToFile;

        StatusDot.Fill = new SolidColorBrush(
            recording ? Colors.OrangeRed :
            running ? Color("#4CD964") : Colors.Gray);
        StatusText.Text = e.State switch
        {
            _ when recording => "Идёт запись",
            EngineState.Running => "Запись в буфер",
            EngineState.Saving => "Сохранение…",
            _ => "Выключено"
        };

        if (running)
        {
            int sec = (int)e.BufferedDuration.TotalSeconds;
            string label = e.EncoderLabel is { Length: > 0 } l ? $"· {l} " : "";
            StatusDetail.Text = $"{label}· буфер {FormatSec(sec)} ({StorageManager.FormatBytes(e.BufferedBytes)})";
        }
        else StatusDetail.Text = "";

        SaveReplayBtn.IsEnabled = e.State == EngineState.Running;
        RecBtnText.Text = recording ? "Остановить запись" : "Начать запись";
        RecDot.Fill = new SolidColorBrush(recording ? Colors.OrangeRed
            : Color("#B0B0B0"));

        _syncingSwitch = true;
        BufferSwitch.IsOn = running;
        _syncingSwitch = false;
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

    private void Screenshot_Click(object sender, RoutedEventArgs e) => _ = App.TakeScreenshotAsync();

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
