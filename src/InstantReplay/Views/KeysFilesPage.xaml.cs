using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using InstantReplay.Core.Hotkeys;
using InstantReplay.Core.Settings;
using InstantReplay.Core.Storage;

namespace InstantReplay.Views;

/// <summary>
/// «Клавиши и файлы»: чем запускать сохранение и куда всё складывается.
/// Раньше это были две отдельные вкладки, хотя настраивают их за один заход.
///
/// Клавиши применяются сразу (назначение — уже подтверждённое действие), настройки
/// папки — кнопкой «Применить», как на вкладке «Запись».
/// </summary>
public sealed partial class KeysFilesPage : Page
{
    private sealed record HotkeyRow(HotkeyAction Action, string Title, string? Desc,
        Func<AppSettings, string> Get, Action<AppSettings, string> Set);

    private static readonly HotkeyRow[] Rows =
    [
        new(HotkeyAction.SaveReplay, "Сохранить повтор", "Весь буфер целиком", s => s.HotkeySaveReplay, (s, v) => s.HotkeySaveReplay = v),
        new(HotkeyAction.SaveLast30, "Сохранить последние 30 сек", "Быстрый короткий клип", s => s.HotkeySaveLast30, (s, v) => s.HotkeySaveLast30 = v),
        new(HotkeyAction.ToggleInstantReplay, "Вкл/выкл Instant Replay", null, s => s.HotkeyToggleInstantReplay, (s, v) => s.HotkeyToggleInstantReplay = v),
        new(HotkeyAction.StartRecording, "Начать запись", "Обычная запись в файл", s => s.HotkeyStartRecording, (s, v) => s.HotkeyStartRecording = v),
        new(HotkeyAction.StopRecording, "Остановить запись", null, s => s.HotkeyStopRecording, (s, v) => s.HotkeyStopRecording = v),
        new(HotkeyAction.Screenshot, "Скриншот", "Сохраняет PNG в папку записей", s => s.HotkeyScreenshot, (s, v) => s.HotkeyScreenshot = v),
        new(HotkeyAction.OpenFolder, "Открыть папку записей", null, s => s.HotkeyOpenFolder, (s, v) => s.HotkeyOpenFolder = v),
    ];

    private readonly Dictionary<HotkeyRow, Button> _buttons = new();
    /// <summary>Строка предупреждения под названием действия (дубль или опасное сочетание).</summary>
    private readonly Dictionary<HotkeyRow, TextBlock> _warnings = new();
    private HotkeyRow? _capturing;

    private readonly DirtyState _dirty;
    private bool _loading;
    /// <summary>Выбранная в пикере папка до нажатия «Применить».</summary>
    private string? _pendingPath;

    public KeysFilesPage()
    {
        InitializeComponent();
        _dirty = new DirtyState(DirtyText, ApplyBtn, RevertBtn);
        BuildHotkeyRows();

        Loaded += (_, _) =>
        {
            RefreshHotkeyLabels();
            LoadStorageSettings();
            Services.Storage.StatsChanged += OnStats;
            OnStats(Services.Storage.GetStats());
        };
        Unloaded += (_, _) =>
        {
            CancelCapture();
            Services.Storage.StatsChanged -= OnStats;
        };
    }

    // ---------------- Горячие клавиши ----------------

    private void BuildHotkeyRows()
    {
        foreach (var row in Rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = row.Title });
            if (row.Desc is not null)
                left.Children.Add(new TextBlock
                {
                    Text = row.Desc,
                    FontSize = 11.5,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

            var warning = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
            };
            left.Children.Add(warning);
            _warnings[row] = warning;
            grid.Children.Add(left);

            var btn = new Button
            {
                MinWidth = 230,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = row
            };
            btn.Click += CaptureButton_Click;
            btn.PreviewKeyDown += CaptureButton_KeyDown;
            btn.LostFocus += (_, _) => { if (_capturing == row) CancelCapture(); };
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            // "✕" обычным шрифтом: Segoe-иконок на Windows 10 может не быть
            var clear = new Button
            {
                Content = "✕",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Tag = row
            };
            ToolTipService.SetToolTip(clear, "Убрать горячую клавишу");
            clear.Click += Clear_Click;
            Grid.SetColumn(clear, 2);
            grid.Children.Add(clear);

            _buttons[row] = btn;
            RowsPanel.Children.Add(grid);
        }
    }

    private void RefreshHotkeyLabels()
    {
        var s = Services.Settings.Current;
        foreach (var (row, btn) in _buttons)
            btn.Content = Pretty(row.Get(s));
        UpdateConflicts();
    }

    /// <summary>
    /// Показать проблемы назначений: одно сочетание на двух действиях (сработает
    /// только одно) и сочетания, перехват которых ломает работу вне игры.
    /// </summary>
    private void UpdateConflicts()
    {
        var s = Services.Settings.Current;
        var duplicates = HotkeyConflicts.FindDuplicates(HotkeyConflicts.Bindings(s));
        int problems = 0;

        foreach (var row in Rows)
        {
            string? text = duplicates.TryGetValue(row.Action, out var other)
                ? $"Это же сочетание назначено действию «{other}» — сработает только одно"
                : HotkeyConflicts.Risk(row.Get(s));

            if (!_warnings.TryGetValue(row, out var label)) continue;
            label.Text = text ?? "";
            label.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            if (text is not null) problems++;
        }

        ConflictBar.IsOpen = problems > 0;
        ConflictBar.Message = problems == 1
            ? "Одно назначение требует внимания — подробности под строкой."
            : $"Назначений, требующих внимания: {problems} — подробности под строками.";
    }

    private static string Pretty(string combo)
    {
        string pretty = string.Join(" + ",
            combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return pretty.Length == 0 ? "Не задано" : pretty;
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HotkeyRow row) return;
        CancelCapture();
        _capturing = row;
        btn.Content = "Нажмите сочетание… (Esc — отмена)";
        Services.Hotkeys.Suspended = true; // хук не должен съесть новое сочетание
    }

    private void CaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing is null || sender is not Button btn || btn.Tag as HotkeyRow != _capturing) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape) { CancelCapture(); return; }
        if (IsModifier(e.Key)) return; // ждём основную клавишу

        string? keyName = KeyName(e.Key);
        if (keyName is null) return;

        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(keyName);

        string combo = string.Join(" + ", parts);
        if (!HotkeyParser.TryParse(combo, out _)) return;

        var row = _capturing;
        _capturing = null;
        Services.Hotkeys.Suspended = false;

        // Сочетание уже занято другим действием — спрашиваем, а не назначаем молча:
        // молчаливый дубль означал, что одно из двух действий никогда не сработает.
        var occupant = HotkeyConflicts.Occupant(Services.Settings.Current, row.Action, combo);
        if (occupant is not null)
        {
            _ = ResolveDuplicateAsync(row, combo, occupant);
            return;
        }

        Assign(row, combo);
    }

    private void Assign(HotkeyRow row, string combo)
    {
        row.Set(Services.Settings.Current, combo);
        Services.Settings.Save("hotkeys");
        RefreshHotkeyLabels();
    }

    private async Task ResolveDuplicateAsync(HotkeyRow row, string combo, HotkeyBinding occupant)
    {
        var dialog = new ContentDialog
        {
            Title = "Сочетание уже занято",
            Content = $"{Pretty(combo)} назначено действию «{occupant.Title}». " +
                      "Переназначить на текущее действие? Прежнее останется без горячей клавиши.",
            PrimaryButtonText = "Переназначить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            RefreshHotkeyLabels(); // вернуть подпись кнопки из режима захвата
            return;
        }

        var previous = Array.Find(Rows, r => r.Action == occupant.Action);
        previous?.Set(Services.Settings.Current, "");
        Assign(row, combo);
    }

    private void CancelCapture()
    {
        _capturing = null;
        Services.Hotkeys.Suspended = false;
        RefreshHotkeyLabels();
    }

    /// <summary>Убрать горячую клавишу для действия (пустой бинд).</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HotkeyRow row) return;
        if (_capturing is not null) CancelCapture();
        row.Set(Services.Settings.Current, "");
        Services.Settings.Save("hotkeys");
        RefreshHotkeyLabels();
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        var s = Services.Settings.Current;
        foreach (var row in Rows)
            row.Set(s, row.Get(defaults));
        Services.Settings.Save("hotkeys");
        RefreshHotkeyLabels();
    }

    private static bool IsModifier(VirtualKey k) => k is VirtualKey.Control or VirtualKey.LeftControl
        or VirtualKey.RightControl or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsDown(VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Имя клавиши в формате, который понимает HotkeyParser.</summary>
    private static string? KeyName(VirtualKey key)
    {
        int code = (int)key;
        if (code is >= 0x70 and <= 0x87) return $"F{code - 0x70 + 1}";        // F1..F24
        if (code is >= 'A' and <= 'Z') return ((char)code).ToString();
        if (code is >= '0' and <= '9') return ((char)code).ToString();
        if (code is >= 0x60 and <= 0x69) return ((char)('0' + code - 0x60)).ToString(); // NumPad
        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
            VirtualKey.Back => "Backspace",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Snapshot => "PrintScreen",
            VirtualKey.Pause => "Pause",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            _ => null
        };
    }

    // ---------------- Папка записей ----------------

    private void LoadStorageSettings()
    {
        _loading = true;
        _dirty.Suspended = true;

        var s = Services.Settings.Current;
        _pendingPath = null;
        PathText.Text = s.SaveRootPath;
        MaxFolderBox.Value = s.MaxFolderSizeGb;
        MinFreeBox.Value = s.MinFreeSpaceGb;
        AutoDeleteToggle.IsOn = s.AutoDeleteOldClips;
        UpdateLimitsVisibility();

        _dirty.Suspended = false;
        _dirty.Clear();
        _loading = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        _dirty.Mark();
        UpdateLimitsVisibility();
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => _dirty.Mark();

    /// <summary>Поля лимитов имеют смысл только при включённом автоудалении.</summary>
    private void UpdateLimitsVisibility() =>
        LimitsPanel.Visibility = AutoDeleteToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        if (_pendingPath is { Length: > 0 }) s.SaveRootPath = _pendingPath;
        s.MaxFolderSizeGb = (int)MaxFolderBox.Value;
        s.MinFreeSpaceGb = (int)MinFreeBox.Value;
        s.AutoDeleteOldClips = AutoDeleteToggle.IsOn;
        Services.Settings.Save("storage"); // StorageManager пересоберёт индекс по новой папке

        _pendingPath = null;
        _dirty.MarkSaved();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadStorageSettings();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 desktop: пикеру нужен HWND главного окна
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(WindowTracker.Main!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _pendingPath = folder.Path;
        PathText.Text = folder.Path;
        _dirty.Mark();
    }

    private void OnStats(StorageStats stats) => Services.Dispatcher.Enqueue(() =>
    {
        SavedCountText.Text = Services.Settings.Current.TotalReplaysSaved.ToString();
        UsedText.Text = ByteSize.Format(stats.FolderBytes);
        FreeText.Text = ByteSize.Format(stats.FreeDiskBytes);
        if (!_loading && _pendingPath is null) PathText.Text = stats.RootPath;
        UpdateUsageBar(stats);
    });

    /// <summary>
    /// Полоса «сколько из лимита занято» — только когда лимит реально работает,
    /// то есть при включённом автоудалении. Иначе это цифра ни о чём.
    /// </summary>
    private void UpdateUsageBar(StorageStats stats)
    {
        var settings = Services.Settings.Current;
        int limitGb = settings.MaxFolderSizeGb;
        if (limitGb <= 0 || !settings.AutoDeleteOldClips)
        {
            UsagePanel.Visibility = Visibility.Collapsed;
            return;
        }
        UsagePanel.Visibility = Visibility.Visible;

        long limitBytes = limitGb * 1024L * 1024 * 1024;
        double percent = Math.Clamp(stats.FolderBytes * 100.0 / limitBytes, 0, 100);
        UsageBar.Value = percent;

        string brush = percent >= 90 ? "SystemFillColorCriticalBrush"
                     : percent >= 70 ? "SystemFillColorCautionBrush"
                                     : "SystemFillColorSuccessBrush";
        UsageBar.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];

        UsageText.Text = $"{ByteSize.Format(stats.FolderBytes)} из {limitGb} ГБ ({percent:0}%) · " +
                         $"{stats.ClipCount} клипов · при заполнении удалятся самые старые";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
