using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Aura.Core.Engine;
using Aura.Core.Settings;
using Aura.Views;

namespace Aura;

public partial class MainWindow : Window
{
    /// <summary>Ниже этой ширины боковая колонка сворачивается в иконки.</summary>
    private const double CompactWidth = 880;

    private readonly Dictionary<string, PageBase> _pages = new();

    /// <summary>С какого раздела открыться (аргумент --page для отладки вёрстки).</summary>
    public string StartPage { get; set; } = "overview";

    /// <summary>
    /// Закрытие окна прячет его в трей, а не уничтожает.
    ///
    /// Крестик в шапке приложения и так звал Hide(), но у окна есть и другие пути
    /// закрытия — Alt+F4, системное меню, «Закрыть» на панели задач. По ним окно
    /// закрывалось по-настоящему, приложение продолжало жить в трее, а следующий
    /// клик по иконке звал Show() у закрытого окна и ронял процесс:
    /// «Cannot set Visibility or call Show after a Window has closed».
    ///
    /// Выйти по-настоящему можно через меню трея — оно зовёт App.ExitApp,
    /// который завершает процесс, не полагаясь на закрытие окна.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
    private PageBase? _current;
    private string _currentKey = "";

    public MainWindow()
    {
        InitializeComponent();

        SizeChanged += (_, _) => UpdateCompact();
        SourceInitialized += (_, _) => RoundCorners();
        StateChanged += (_, _) => UpdateMaximizedPadding();

        ApplyUiScale(Services.Settings.Current.UiScale);

        Services.Engine.StateChanged += _ => Services.Ui.Enqueue(SyncMaster);
        Services.Settings.Changed += group => Services.Ui.Enqueue(() =>
        {
            if (group is "" or "ui") ApplyUiScale(Services.Settings.Current.UiScale);
        });

        Loaded += (_, _) =>
        {
            Navigate(StartPage);
            SyncMaster();
            _ = CheckUpdateAsync();
        };
    }

    // ---------------- Окно ----------------

    /// <summary>Скруглённые углы Windows 11 (на Windows 10 вызов просто игнорируется).</summary>
    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            Core.Interop.NativeMethods.DwmSetWindowAttribute(
                hwnd, Core.Interop.NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }
    }

    /// <summary>В развёрнутом окне рамка уезжает за экран — компенсируем полями.</summary>
    private void UpdateMaximizedPadding() =>
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

    private void Toolbar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Maximize_Click(sender, e); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Крестик прячет окно в трей: запись продолжается, приложение живёт дальше.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------------- Масштаб и компактность ----------------

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    private void UpdateCompact()
    {
        bool compact = ActualWidth / UiScale.ScaleX < CompactWidth;
        if (compact == IsCompact) return;
        IsCompact = compact;

        Side.BeginAnimation(WidthProperty, new DoubleAnimation(compact ? 64 : 236, TimeSpan.FromSeconds(0.32))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        // В свёрнутой колонке поля ужимаются: иначе переключатель шириной 44 px
        // не помещался в 64 px колонки и обрезался краем окна.
        SideContent.Margin = compact ? new Thickness(8, 14, 8, 10) : new Thickness(12, 14, 12, 12);
        MasterCard.Padding = compact ? new Thickness(2) : new Thickness(11, 10, 11, 10);
        UpdateCardBox.Padding = compact ? new Thickness(6) : new Thickness(10, 8, 10, 8);
        Dispatcher.BeginInvoke(MoveIndicator, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Масштаб интерфейса. 0 — подобрать по монитору: на Full HD обычный,
    /// на 2K крупнее, на 4K ещё крупнее. В WPF это масштаб корневого элемента,
    /// поэтому меняется всё сразу и ничего не разъезжается.
    /// </summary>
    public void ApplyUiScale(double scale)
    {
        double value = scale > 0 ? scale : AutoScale();
        UiScale.ScaleX = UiScale.ScaleY = value;
        UpdateCompact();
    }

    private static double AutoScale()
    {
        double height = SystemParameters.PrimaryScreenHeight;   // уже с учётом системного DPI
        return height switch
        {
            >= 1900 => 1.35,   // 4K без масштабирования Windows
            >= 1300 => 1.15,   // 2K
            <= 800  => 0.9,    // ноутбучные 1366×768 и подобные
            _       => 1.0
        };
    }

    // ---------------- Навигация ----------------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) Navigate(key);
    }

    public void Navigate(string key)
    {
        // Отметку синхронизируем ДО раннего выхода: разделы лежат в двух панелях,
        // это две независимые группы переключателей, и нажатие в одной не снимает
        // отметку в другой. Если уйти отсюда раньше, подсветка останется на чужом.
        SyncNavChecks(key);
        if (_currentKey == key) { MoveIndicator(); return; }

        if (!_pages.TryGetValue(key, out var page))
        {
            try
            {
                page = key switch
                {
                    "overview" => new OverviewPage(),
                    "clips" => new ClipsPage(),
                    "capture" => new CapturePage(),
                    "keys" => new KeysPage(),
                    "files" => new FilesPage(),
                    "app" => new AppSettingsPage(),
                    "system" => new SystemPage(),
                    _ => new OverviewPage()
                };
            }
            catch (Exception ex)
            {
                // Раздел не должен падать молча в пустой экран: пишем в лог и
                // показываем, что именно сломалось.
                Core.Logging.Log.Error("UI", ex);
                page = new BrokenPage(key, ex);
            }
            _pages[key] = page;
        }

        _current?.OnHidden();
        _current = page;
        _currentKey = key;

        PageHost.Content = page;
        ToolTitle.Text = page.Title;
        ToolActions.Children.Clear();
        foreach (var action in page.ToolbarActions) ToolActions.Children.Add(action);
        Scroll.ScrollToTop();
        HideApplyBar();
        page.OnShown();

        // Страница въезжает снизу с лёгким проявлением
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.28)));
        PageShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.42))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        SyncNavChecks(key);
        Dispatcher.BeginInvoke(MoveIndicator, System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateToolbarTitle();
    }

    private void SyncNavChecks(string key)
    {
        foreach (var button in NavButtons())
            button.IsChecked = (string?)button.Tag == key;
    }

    private IEnumerable<RadioButton> NavButtons() =>
        Nav1.Children.OfType<RadioButton>().Concat(Nav2.Children.OfType<RadioButton>());

    /// <summary>Подсветка активного раздела переезжает, а не мигает.</summary>
    private void MoveIndicator()
    {
        foreach (var (panel, indicator, shift) in new[]
                 {
                     (Nav1, Indicator1, Indicator1Shift),
                     (Nav2, Indicator2, Indicator2Shift)
                 })
        {
            var active = panel.Children.OfType<RadioButton>().FirstOrDefault(b => b.IsChecked == true);
            if (active is null)
            {
                // Именно анимацией: локальное значение анимация бы перекрыла
                indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.15)));
                continue;
            }

            double y = active.TranslatePoint(new Point(0, 0), panel).Y;
            if (indicator.Opacity < 0.5)
            {
                shift.Y = y;
                indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
            }
            else
            {
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(y, TimeSpan.FromSeconds(0.4))
                    { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } });
            }
        }
    }

    // ---------------- Панель несохранённых изменений ----------------

    private Action? _applyAction, _revertAction;

    /// <summary>Показать плашку «есть несохранённое» внизу окна.</summary>
    public void ShowApplyBar(string text, Action apply, Action revert)
    {
        ApplyText.Text = text;
        _applyAction = apply;
        _revertAction = revert;

        if (ApplyBar.Visibility == Visibility.Visible) return;
        ApplyBar.Visibility = Visibility.Visible;
        ApplyBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.25)));
        ApplyShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromSeconds(0.45))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
    }

    public void HideApplyBar()
    {
        _applyAction = _revertAction = null;
        if (ApplyBar.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.2));
        fade.Completed += (_, _) => { if (ApplyBar.Opacity < 0.05) ApplyBar.Visibility = Visibility.Collapsed; };
        ApplyBar.BeginAnimation(OpacityProperty, fade);
        ApplyShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(90, TimeSpan.FromSeconds(0.3)));
    }

    private void ApplyOk_Click(object sender, RoutedEventArgs e) => _applyAction?.Invoke();
    private void ApplyRevert_Click(object sender, RoutedEventArgs e) => _revertAction?.Invoke();

    // ---------------- Шапка ----------------

    private void Scroll_Changed(object sender, ScrollChangedEventArgs e) => UpdateToolbarTitle();

    /// <summary>Заголовок в шапке проявляется, когда крупный ушёл за верхний край.</summary>
    private void UpdateToolbarTitle()
    {
        bool show = Scroll.VerticalOffset > 40;
        double target = show ? 1 : 0;
        if (Math.Abs(ToolTitle.Opacity - target) < 0.01) return;

        ToolTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(0.25)));
        Toolbar.BorderBrush = show ? (Brush)FindResource("SepBrush") : Brushes.Transparent;
    }

    // ---------------- Главный переключатель ----------------

    private void Master_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Engine.State == EngineState.Stopped) App.SafeStartEngine();
        else Services.Engine.Stop();
        SyncMaster();
    }

    private void SyncMaster()
    {
        bool on = Services.Engine.State != EngineState.Stopped;
        MasterSwitch.IsChecked = on;
        MasterState.Text = on ? "Включён" : "Выключен";
    }

    // ---------------- Обновление ----------------

    private async Task CheckUpdateAsync()
    {
        if (!Services.Settings.Current.CheckForUpdates) return;
        try
        {
            var info = await Services.Updates.CheckAsync();
            if (info is null) return;
            UpdateTitle.Text = $"Версия {info.Version}";
            UpdateCard.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void Update_Click(object sender, RoutedEventArgs e) => Navigate("app");
}
