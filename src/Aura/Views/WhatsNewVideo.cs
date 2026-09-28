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
    public static string? FileFor(Version version) => FileBetween(new Version(0, 0), version);

    /// <summary>
    /// Самый свежий ролик из версий после <paramref name="last"/> и не новее
    /// <paramref name="current"/>. Ролик едет и в следующих сборках: кто обновился
    /// сразу через несколько версий, тоже его увидит. null — роликов нет.
    /// </summary>
    public static string? FileBetween(Version last, Version current)
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            return Directory.EnumerateFiles(Folder, "whatsnew-*.mp4")
                .Select(f => (File: f, Version: Version.TryParse(
                    Path.GetFileNameWithoutExtension(f)["whatsnew-".Length..], out var v) ? v : null))
                .Where(x => x.Version is not null && x.Version > last && x.Version <= current)
                .OrderByDescending(x => x.Version)
                .Select(x => x.File)
                .FirstOrDefault();
        }
        catch { return null; }
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
        // LibVLC освобождается в фоне; файл можно удалить, только когда он отпущен
        if (window.Tag is Task released) released.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Окно ролика; отдельно от показа, чтобы снимать вёрстку в --dev.
    ///
    /// Играет LibVLC, как предпросмотр в редакторе, а не WPF MediaElement: тот
    /// работает только через Windows Media Player, которого нет в Windows LTSC и
    /// редакциях N («Windows Media Player version 10 or later is required»).
    /// Окно без прозрачности: VideoView из LibVLC это отдельное окно Win32, и в
    /// прозрачном окне WPF оно не рисуется.
    /// </summary>
    internal static Window Create(string path, bool offscreenTest = false)
    {
        var app = Application.Current;
        var owner = app.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        bool canClose = false;
        const double width = 1000;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)app.FindResource("CanvasBrush"),
            BorderBrush = (Brush)app.FindResource("HairBrush"),
            BorderThickness = new Thickness(1),
            ShowInTaskbar = owner is null,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null && !offscreenTest)
            try { window.Owner = owner; } catch { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        if (offscreenTest)
        {
            // Проверка в --dev: окно за пределами экрана, без звука и само закрывается
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
        }

        var view = new LibVLCSharp.WPF.VideoView
        {
            Height = (width - 2 - 32) * 9.0 / 16,
            Background = Brushes.Black
        };

        var volumeLabel = new TextBlock
        {
            Text = "Громкость",
            Style = (Style)app.FindResource("RowSub"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = offscreenTest ? 0 : 60,
            Width = 180,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var progress = new ProgressBar { Maximum = 1, Height = 4, Margin = new Thickness(0, 12, 0, 0) };
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
        bar.Children.Add(volumeLabel);
        Grid.SetColumn(volume, 1);
        bar.Children.Add(volume);
        Grid.SetColumn(button, 2);
        bar.Children.Add(button);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(view);
        panel.Children.Add(progress);
        panel.Children.Add(bar);
        window.Content = panel;

        // Ни Esc, ни Alt+F4 не закрывают ролик раньше конца
        window.Closing += (_, e) => { if (!canClose) e.Cancel = true; };
        window.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };

        LibVLCSharp.Shared.LibVLC? engine = null;
        LibVLCSharp.Shared.MediaPlayer? player = null;
        LibVLCSharp.Shared.Media? media = null;

        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        tick.Tick += (_, _) =>
        {
            if (player is { } p && p.Length > 0) progress.Value = Math.Clamp(p.Position, 0, 1);
        };
        volume.ValueChanged += (_, e) => { if (player is not null) player.Volume = (int)e.NewValue; };

        // Страховка: ролик 20 секунд. Если конец так и не пришёл (плеер завис),
        // через минуту окно всё равно можно закрыть.
        var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        safety.Tick += (_, _) => { safety.Stop(); AllowClose("Закрыть"); };

        void Fail(string why)
        {
            Log.Warn("App", $"Ролик «что нового» не проигрывается: {why}");
            AllowClose("Закрыть");
            window.Close();
        }

        window.Loaded += async (_, _) =>
        {
            if (!offscreenTest) window.Activate();
            safety.Start();
            try
            {
                // Загрузка модулей LibVLC ощутима, окну она мешать не должна
                (engine, player, media) = await Task.Run(() =>
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    var e = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet",
                                                          "--audio-resampler=speex_resampler");
                    var p = new LibVLCSharp.Shared.MediaPlayer(e) { EnableHardwareDecoding = true };
                    return (e, p, new LibVLCSharp.Shared.Media(e, new Uri(path)));
                });
                if (!window.IsVisible) return;
                player.EndReached += (_, _) => window.Dispatcher.BeginInvoke(() =>
                {
                    progress.Value = 1;
                    Log.Info("App", "Ролик «что нового» досмотрен до конца");
                    AllowClose("Отлично");
                    if (offscreenTest) window.Close();
                });
                player.EncounteredError += (_, _) => window.Dispatcher.BeginInvoke(() => Fail("ошибка LibVLC"));
                player.Playing += (_, _) => window.Dispatcher.BeginInvoke(() => player.Volume = (int)volume.Value);
                view.MediaPlayer = player;
                if (!player.Play(media)) { Fail("LibVLC не принял файл"); return; }
                tick.Start();
            }
            catch (Exception ex) { Fail(ex.Message); }
        };

        window.Closed += (_, _) =>
        {
            tick.Stop();
            safety.Stop();
            view.MediaPlayer = null;
            // Остановка LibVLC в фоне: из потока окна она может подвиснуть (см. редактор)
            var (e, p, m) = (engine, player, media);
            window.Tag = Task.Run(() =>
            {
                try { p?.Stop(); } catch { }
                m?.Dispose();
                p?.Dispose();
                e?.Dispose();
            });
        };

        return window;
    }
}
