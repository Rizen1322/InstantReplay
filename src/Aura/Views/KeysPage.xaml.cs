using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aura.Controls;
using Aura.Core.Hotkeys;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>
/// Горячие клавиши. Применяются сразу: сервис хоткеев перечитывает настройки
/// по событию Changed, ждать «Применить» незачем.
/// </summary>
public partial class KeysPage : PageBase
{
    private Button? _capturing;

    public override string Title => "Клавиши";

    public KeysPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    public override void OnShown() => Build();

    private void Build()
    {
        Rows.Children.Clear();
        var settings = Services.Settings.Current;
        var bindings = HotkeyConflicts.Bindings(settings);
        var duplicates = HotkeyConflicts.FindDuplicates(bindings);

        bool first = true;
        foreach (var binding in bindings)
        {
            if (!first) Rows.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });
            first = false;

            var row = new AdaptiveRow { Margin = new Thickness(16, 11, 16, 11) };
            row.Children.Add(new TextBlock { Text = binding.Title, Style = (Style)FindResource("RowLabel") });

            var field = new Button
            {
                Style = (Style)FindResource("HotkeyField"),
                Tag = binding.Action,
                Content = Caps(binding.Combo)
            };
            field.Click += Field_Click;
            field.PreviewKeyDown += Field_KeyDown;
            field.PreviewMouseDown += Field_MouseDown;
            field.LostFocus += (_, _) => StopCapture(field, binding.Combo);
            row.Children.Add(field);

            if (duplicates.ContainsKey(binding.Action))
                field.BorderBrush = (System.Windows.Media.Brush)FindResource("RecBrush");

            Rows.Children.Add(row);
        }

        if (duplicates.Count > 0)
        {
            ConflictText.Text = "Одно сочетание назначено на несколько действий: " +
                string.Join(", ", duplicates.Values.Distinct()) + ". Сработает только одно из них.";
            ConflictBar.Visibility = Visibility.Visible;
        }
        else ConflictBar.Visibility = Visibility.Collapsed;
    }

    private void Field_Click(object sender, RoutedEventArgs e)
    {
        var field = (Button)sender;
        _capturing = field;
        field.Content = new TextBlock
        {
            Text = "Жми клавишу или кнопку мыши · Esc — снять",
            FontSize = 12.5,
            Foreground = (System.Windows.Media.Brush)FindResource("BlueBrush")
        };
        field.BorderBrush = (System.Windows.Media.Brush)FindResource("BlueBrush");
        field.Focus();
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } field || !ReferenceEquals(field, sender)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        e.Handled = true;
        // Escape снимает сочетание совсем: действие останется только в окне и трее
        if (key == Key.Escape)
        {
            Save((HotkeyAction)field.Tag, "");
            _capturing = null;
            Build();
            return;
        }

        Apply(field, KeyName(key));
    }

    /// <summary>
    /// Назначение на кнопку мыши.
    ///
    /// Левая и правая не назначаются намеренно: левой это поле и открывают, а
    /// перехватывать правую значило бы отобрать у человека контекстные меню.
    /// Остаются колесо и две боковые — то, что для этого и держат.
    /// </summary>
    private void Field_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturing is not { } field || !ReferenceEquals(field, sender)) return;

        string name = e.ChangedButton switch
        {
            MouseButton.Middle => "Mouse3",
            MouseButton.XButton1 => "Mouse4",
            MouseButton.XButton2 => "Mouse5",
            _ => ""
        };
        if (name.Length == 0) return;

        e.Handled = true;
        Apply(field, name);
    }

    /// <summary>Собрать сочетание с текущими модификаторами и сохранить.</summary>
    private void Apply(Button field, string name)
    {
        if (name.Length == 0) return;

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);

        string combo = string.Join("+", parts);
        if (HotkeyParser.Normalize(combo).Length == 0) return;

        Save((HotkeyAction)field.Tag, combo);
        _capturing = null;
        Build();
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => key.ToString()[1..],
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        _ => ""
    };

    /// <summary>Колпачки клавиш или «не задано», если сочетание снято.</summary>
    private object Caps(string combo) => combo.Length > 0
        ? new KeyCaps { Combo = combo }
        : new TextBlock
        {
            Text = "не задано",
            FontSize = 12.5,
            Foreground = (System.Windows.Media.Brush)FindResource("Tx3Brush")
        };

    private void StopCapture(Button field, string combo)
    {
        if (!ReferenceEquals(_capturing, field)) return;
        _capturing = null;
        field.Content = Caps(combo);
        field.BorderBrush = (System.Windows.Media.Brush)FindResource("HairBrush");
    }

    private static string Current(HotkeyAction action)
    {
        var s = Services.Settings.Current;
        return action switch
        {
            HotkeyAction.SaveReplay => s.HotkeySaveReplay,
            HotkeyAction.SaveLast30 => s.HotkeySaveLast30,
            HotkeyAction.ToggleInstantReplay => s.HotkeyToggleInstantReplay,
            HotkeyAction.StartRecording => s.HotkeyStartRecording,
            HotkeyAction.StopRecording => s.HotkeyStopRecording,
            HotkeyAction.Screenshot => s.HotkeyScreenshot,
            HotkeyAction.ScreenshotRegion => s.HotkeyScreenshotRegion,
            _ => s.HotkeyOpenFolder
        };
    }

    private static void Save(HotkeyAction action, string combo)
    {
        var s = Services.Settings.Current;
        switch (action)
        {
            case HotkeyAction.SaveReplay: s.HotkeySaveReplay = combo; break;
            case HotkeyAction.SaveLast30: s.HotkeySaveLast30 = combo; break;
            case HotkeyAction.ToggleInstantReplay: s.HotkeyToggleInstantReplay = combo; break;
            case HotkeyAction.StartRecording: s.HotkeyStartRecording = combo; break;
            case HotkeyAction.StopRecording: s.HotkeyStopRecording = combo; break;
            case HotkeyAction.Screenshot: s.HotkeyScreenshot = combo; break;
            case HotkeyAction.ScreenshotRegion: s.HotkeyScreenshotRegion = combo; break;
            case HotkeyAction.OpenFolder: s.HotkeyOpenFolder = combo; break;
        }
        Services.Settings.Save("hotkeys");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        var s = Services.Settings.Current;
        s.HotkeySaveReplay = defaults.HotkeySaveReplay;
        s.HotkeySaveLast30 = defaults.HotkeySaveLast30;
        s.HotkeyToggleInstantReplay = defaults.HotkeyToggleInstantReplay;
        s.HotkeyStartRecording = defaults.HotkeyStartRecording;
        s.HotkeyStopRecording = defaults.HotkeyStopRecording;
        s.HotkeyScreenshot = defaults.HotkeyScreenshot;
        s.HotkeyScreenshotRegion = defaults.HotkeyScreenshotRegion;
        s.HotkeyOpenFolder = defaults.HotkeyOpenFolder;
        Services.Settings.Save("hotkeys");
        Build();
    }
}
