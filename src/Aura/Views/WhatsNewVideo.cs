using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aura.Core.Logging;

namespace Aura.Views;

/// <summary>
/// Ролик «что нового», который показывается один раз: при первом открытии окна
/// Aura после обновления на версию, к которой он приложен. Не при самом обновлении:
/// у многих Aura после обновления сразу уходит в трей, и ролик никто бы не увидел.
///
/// Досмотреть обязательно: закрыть окно до конца ролика нельзя, громкость
/// регулируется. Если ролик не проигрывается (например, в Windows N без
/// медиакомпонентов), окно сразу разрешает закрыться, чтобы не запереть человека.
/// После показа файл удаляется: второй раз он не нужен и место занимает зря.
/// </summary>
public static class WhatsNewVideo
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>Ролик для этой версии; null — его нет.</summary>
    public static string? FileFor(Version version)
    {
        string path = Path.Combine(Folder, $"whatsnew-{version.ToString(3)}.mp4");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Удалить все ролики: показанный, старые и не нужные этой установке.</summary>
    public static void DeleteAll()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string file in Directory.EnumerateFiles(Folder, "whatsnew-*.mp4"))
                try { File.Delete(file); Log.Info("App", $"Ролик «что нового» удалён: {Path.GetFileName(file)}"); }
                catch (Exception ex) { Log.Warn("App", $"Ролик не удалён ({ex.Message}): {file}"); }
        }
        catch { }
    }

    /// <summary>Показать ролик модально и дождаться конца.</summary>
    public static void Show(string path)
    {
        var window = Create(path);
        Log.Info("App", $"Показываю ролик «что нового»: {Path.GetFileName(path)}");
        window.ShowDialog();
    }

    /// <summary>Окно ролика; отдельно от показа, чтобы снимать вёрстку в --dev.</summary>
    internal static Window Create(string path)
    {
        var app = Application.Current;
        var owner = app.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        bool canClose = false;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            Width = 1000,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null)
            try { window.Owner = owner; } catch { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; }

        var media = new MediaElement
        {
            Source = new Uri(path),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = 0.6
        };
        var video = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Black,
            ClipToBounds = true,
            Height = (1000 - 24 - 32) * 9.0 / 16,
            Child = media
        };

        // --- громкость ---
        var volumeIcon = new TextBlock
        {
            Text = "Громкость",
            Style = (Style)app.FindResource("RowSub"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = media.Volume,
            Width = 180,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        volume.ValueChanged += (_, e) => media.Volume = e.NewValue;

        var progress = new ProgressBar
        {
            Maximum = 1,
            Height = 4,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var button = new Button
        {
            Content = "Досмотри до конца",
            Style = (Style)app.FindResource("BtnPri"),
            MinWidth = 180,
            Height = 38,
            IsEnabled = false,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        button.Click += (_, _) => window.Close();

        void AllowClose(string label)
        {
            if (canClose) return;
            canClose = true;
            button.IsEnabled = true;
            button.Opacity = 1;
            button.Content = label;
            button.Focus();
        }

        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.Children.Add(volumeIcon);
        Grid.SetColumn(volume, 1);
        bar.Children.Add(volume);
        Grid.SetColumn(button, 2);
        bar.Children.Add(button);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(video);
        panel.Children.Add(progress);
        panel.Children.Add(bar);

        window.Content = new Border
        {
            Background = (Brush)app.FindResource("CanvasBrush"),
            BorderBrush = (Brush)app.FindResource("HairBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(12),
            Effect = (System.Windows.Media.Effects.Effect)app.FindResource("ToastShadow"),
            Child = panel
        };

        // Ни Esc, ни Alt+F4 не закрывают ролик раньше конца
        window.Closing += (_, e) => { if (!canClose) e.Cancel = true; };
        window.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };

        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        tick.Tick += (_, _) =>
        {
            if (media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan.TotalSeconds > 0)
                progress.Value = media.Position.TotalSeconds / media.NaturalDuration.TimeSpan.TotalSeconds;
        };

        media.MediaEnded += (_, _) =>
        {
            progress.Value = 1;
            AllowClose("Отлично");
        };
        media.MediaFailed += (_, e) =>
        {
            Log.Warn("App", $"Ролик «что нового» не проигрывается: {e.ErrorException?.Message}");
            AllowClose("Закрыть");
            window.Close();
        };

        // Страховка: ролик 20 секунд. Если конец так и не пришёл (плеер завис),
        // через минуту окно всё равно можно закрыть.
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        safety.Tick += (_, _) => { safety.Stop(); AllowClose("Закрыть"); };

        window.Loaded += (_, _) =>
        {
            window.Activate();
            window.Opacity = 0;
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.2)));
            media.Play();
            tick.Start();
            safety.Start();
        };
        window.Closed += (_, _) =>
        {
            tick.Stop();
            safety.Stop();
            media.Stop();
            media.Close();
        };

        return window;
    }
}
