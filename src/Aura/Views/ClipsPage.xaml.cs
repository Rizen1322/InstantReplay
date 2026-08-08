using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aura.Core.Library;
using Aura.Core.Storage;

namespace Aura.Views;

/// <summary>
/// Панорама записей: секции по датам, сетка карточек, фильтры.
/// Своего плеера нет — клик открывает файл тем, что назначено в системе.
/// </summary>
public partial class ClipsPage : PageBase
{
    private List<ClipItem> _all = [];
    private CancellationTokenSource? _thumbs;
    private bool _loaded;

    // Порядок карточек на экране — по нему считается диапазон для Shift-клика.
    private List<ClipItem> _shown = [];
    private ClipItem? _anchor;

    public override string Title => "Клипы";

    public override UIElement[] ToolbarActions
    {
        get
        {
            var button = new Button { Content = "Открыть папку" };
            button.Click += OpenFolder_Click;
            return [button];
        }
    }

    public ClipsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) _ = ReloadAsync(); };
        ClipCommands.LibraryChanged += () => Dispatcher.BeginInvoke(() => _ = ReloadAsync());
    }

    public override void OnShown()
    {
        if (!_loaded) _ = ReloadAsync();
    }

    public override void OnHidden() => _thumbs?.Cancel();

    // ---------------- Загрузка ----------------

    private async Task ReloadAsync()
    {
        string root = Services.Settings.Current.SaveRootPath;
        _all = await Task.Run(() => ClipLibrary.Scan(root));
        _loaded = true;

        FillGameFilter();
        Render();
    }

    private void FillGameFilter()
    {
        string? previous = (GameFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        GameFilter.Items.Clear();
        GameFilter.Items.Add(new ComboBoxItem { Content = "Все игры" });
        foreach (var game in _all.Select(i => i.Game).Distinct().OrderBy(g => g))
            GameFilter.Items.Add(new ComboBoxItem { Content = game });

        GameFilter.SelectedIndex = 0;
        if (previous is null) return;
        foreach (ComboBoxItem item in GameFilter.Items)
            if ((string?)item.Content == previous) { GameFilter.SelectedItem = item; return; }
    }

    private void Render()
    {
        _thumbs?.Cancel();
        Groups.Children.Clear();

        var items = Filtered();
        _shown = items;
        UpdateSelectionBar();   // список сменился — пересчитываем панель
        long bytes = items.Sum(i => i.SizeBytes);
        Summary.Text = items.Count == 0
            ? "Пока ничего не сохранено."
            : $"{items.Count} записей, {ByteSize.Format(bytes)}.";

        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0)
        {
            bool filtered = _all.Count > 0;
            EmptyTitle.Text = filtered ? "Ничего не найдено" : "Здесь пока пусто";
            EmptyHint.Text = filtered
                ? "Попробуй убрать фильтры."
                : "Включи мгновенный повтор и сохрани момент — запись появится здесь.";
            return;
        }

        foreach (var group in ClipLibrary.GroupByDate(items))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 12) };
            header.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontFamily = (FontFamily)FindResource("DispFont"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = group.Subtitle,
                Style = (Style)FindResource("Caption3"),
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            });
            Groups.Children.Add(header);

            var grid = new ItemsControl
            {
                ItemsSource = group.Items,
                ItemTemplate = (DataTemplate)FindResource("ClipCard"),
                ItemsPanel = (ItemsPanelTemplate)FindResource("ClipGridPanel")
            };
            grid.AddHandler(ButtonBase_ClickEvent, new RoutedEventHandler(Card_Click));
            grid.AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(Card_ContextMenuOpening));
            Groups.Children.Add(grid);
        }

        _thumbs = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(items, _thumbs.Token);
    }

    private static readonly RoutedEvent ButtonBase_ClickEvent = System.Windows.Controls.Primitives.ButtonBase.ClickEvent;

    /// <summary>
    /// Кадры грузятся по очереди: у поставщика миниатюр своя очередь на два
    /// запроса, и залп из сотни заявок он просто отбросил бы.
    /// </summary>
    private static async Task LoadThumbnailsAsync(List<ClipItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) return;
            try { await ClipThumbnails.LoadAsync(item); } catch { }
        }
    }

    private List<ClipItem> Filtered()
    {
        IEnumerable<ClipItem> items = _all;

        int type = TypeFilter.SelectedIndex;
        if (type == 1) items = items.Where(i => !i.IsScreenshot);
        else if (type == 2) items = items.Where(i => i.IsScreenshot);

        if (GameFilter.SelectedIndex > 0 && GameFilter.SelectedItem is ComboBoxItem game)
            items = items.Where(i => i.Game == (string?)game.Content);

        items = SortOrder.SelectedIndex switch
        {
            1 => items.OrderBy(i => i.Created),
            2 => items.OrderByDescending(i => i.SizeBytes),
            3 => items.OrderBy(i => i.Title),
            _ => items.OrderByDescending(i => i.Created)
        };

        return items.ToList();
    }

    // ---------------- Действия ----------------

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();

    /// <summary>
    /// Обычный клик по-прежнему открывает файл — это главное действие, и менять
    /// его на «выделить» значило бы ломать привычку ради редкой операции.
    /// Выделение навешано на модификаторы, как в проводнике: Ctrl добавляет по
    /// одной, Shift берёт диапазон от последней тронутой карточки.
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ClipItem clip }) return;

        var keys = Keyboard.Modifiers;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            clip.IsSelected = !clip.IsSelected;
            _anchor = clip;
            UpdateSelectionBar();
            return;
        }
        if (keys.HasFlag(ModifierKeys.Shift))
        {
            SelectRange(_anchor ?? clip, clip);
            return;
        }

        // Клик без модификаторов по выделенному — снимаем выделение и открываем.
        if (SelectedItems().Count > 0) ClearSelection();
        Open(clip.FullPath);
    }

    private void SelectRange(ClipItem from, ClipItem to)
    {
        int a = _shown.IndexOf(from), b = _shown.IndexOf(to);
        if (a < 0 || b < 0) return;
        if (a > b) (a, b) = (b, a);

        foreach (var item in _shown) item.IsSelected = false;
        for (int i = a; i <= b; i++) _shown[i].IsSelected = true;
        UpdateSelectionBar();
    }

    private List<ClipItem> SelectedItems() => _shown.Where(i => i.IsSelected).ToList();

    private void ClearSelection()
    {
        foreach (var item in _shown) item.IsSelected = false;
        _anchor = null;
        UpdateSelectionBar();
    }

    /// <summary>Панель над сеткой: сколько выделено и что с этим можно сделать.</summary>
    private void UpdateSelectionBar()
    {
        var selected = SelectedItems();
        SelectionBar.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Count == 0) return;

        SelectionText.Text = $"Выбрано {selected.Count} · {ByteSize.Format(selected.Sum(i => i.SizeBytes))}";
    }

    // ---------------- Действия над выделением ----------------

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _shown) item.IsSelected = true;
        UpdateSelectionBar();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count > 0) ClipCommands.DeleteMany.Execute(selected);
    }

    private void CopySelectedPaths_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count > 0) ClipCommands.CopyPathsMany.Execute(selected);
    }

    /// <summary>
    /// Правый клик: если карточка не в выделении, она становится единственной
    /// выделенной — иначе меню действовало бы не на то, на что человек нажал.
    /// Пункты «для выделенных» показываются только когда выделено больше одной.
    /// </summary>
    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ClipItem clip } source) return;

        if (!clip.IsSelected)
        {
            ClearSelection();
            clip.IsSelected = true;
            _anchor = clip;
            UpdateSelectionBar();
        }

        var selected = SelectedItems();
        var menu = (source as FrameworkElement)?.ContextMenu
                   ?? (FindCard(source) as FrameworkElement)?.ContextMenu;
        if (menu is null) return;

        bool many = selected.Count > 1;
        foreach (var element in menu.Items)
        {
            if (element is not FrameworkElement named) continue;
            switch (named.Name)
            {
                case "ManySeparator":
                case "CopyPathsItem":
                case "DeleteManyItem":
                    named.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
                    if (named is MenuItem menuItem) menuItem.CommandParameter = selected;
                    break;
            }
        }
    }

    private static DependencyObject? FindCard(DependencyObject? source)
    {
        while (source is not null and not Button) source = VisualTreeHelper.GetParent(source);
        return source;
    }

    /// <summary>
    /// Открываем через explorer.exe, а не напрямую: приложение работает с правами
    /// администратора, а упакованные плееры Windows из такого процесса
    /// активируются криво и жалуются на «файл не найден».
    /// </summary>
    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
