using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ClipItem clip }) return;
        Open(clip.FullPath);
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
