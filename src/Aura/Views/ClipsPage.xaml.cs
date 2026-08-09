using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>
    /// Режим выделения. Включается пунктом «Выделить» в меню карточки; пока он
    /// включён, обычный клик выделяет, а не открывает файл. Выключается кнопкой
    /// «Готово» на панели или когда снято последнее выделение.
    /// </summary>
    private bool _selecting;

    public override string Title => "Клипы";

    public override UIElement[] ToolbarActions
    {
        get
        {
            var button = new Button { Content = "Открыть папку" };
            Controls.Deco.SetIcon(button, (Geometry)FindResource("Ico.FolderOpen"));
            button.Click += OpenFolder_Click;
            return [button];
        }
    }

    public ClipsPage()
    {
        InitializeComponent();
        // Прокрутку ищем именно в Loaded: OnShown зовётся сразу после присвоения
        // содержимого окну, когда визуального дерева над страницей ещё нет и
        // подниматься к ScrollViewer просто не по чему.
        Loaded += (_, _) =>
        {
            if (!_loaded) _ = ReloadAsync();
            HookScroll();
        };
        ClipCommands.LibraryChanged += () => Dispatcher.BeginInvoke(() => _ = ReloadAsync());
        ClipCommands.SelectRequested += clip => Dispatcher.BeginInvoke(() => BeginSelection(clip));
    }

    public override void OnShown()
    {
        if (!_loaded) _ = ReloadAsync();
        HookScroll();

        // Клавиши вешаем на окно и снимаем при уходе: у страницы своего фокуса нет,
        // курсор обычно стоит в прокрутке, и KeyDown до карточек просто не дошёл бы.
        if (Window.GetWindow(this) is Window window)
        {
            window.PreviewKeyDown -= Keys_Down;
            window.PreviewKeyDown += Keys_Down;
        }
    }

    /// <summary>
    /// Ctrl+A — выделить всё, Delete — удалить выделенное, Esc — снять выделение.
    /// В файловых панелях это те же клавиши, и рука тянется к ним сама.
    /// </summary>
    private void Keys_Down(object sender, KeyEventArgs e)
    {
        // В поле ввода (поиск, переименование) клавиши принадлежат полю
        if (Keyboard.FocusedElement is TextBoxBase) return;

        switch (e.Key)
        {
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control && _shown.Count > 0:
                // Ctrl+A всегда выделяет всё, а не переключает: так это работает везде
                _selecting = true;
                foreach (var item in _shown) item.IsSelected = true;
                UpdateSelectionBar(keepMode: true);
                e.Handled = true;
                break;
            case Key.Delete when _selecting:
                DeleteSelected_Click(this, e);
                e.Handled = true;
                break;
            case Key.Escape when _selecting:
                ClearSelection();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Панель выделения должна оставаться на виду при прокрутке: выделяют обычно
    /// не первые карточки, и уезжающая вверх панель заставляла бы скроллить обратно
    /// ради каждой кнопки. Страница целиком лежит в общей прокрутке окна, поэтому
    /// панель не «прилипает» сама — сдвигаем её ровно на столько, на сколько
    /// содержимое ушло вверх, и не дальше конца списка карточек.
    /// </summary>
    private ScrollViewer? _scroll;

    private void HookScroll()
    {
        if (_scroll is not null) return;
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null and not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        if (node is not ScrollViewer scroll) return;

        _scroll = scroll;
        _scroll.ScrollChanged += (_, _) => UpdateSelectionBarOffset();
        UpdateSelectionBarOffset();
    }

    /// <summary>Зазор между верхом окна и «прилипшей» панелью — иначе она выглядит приклеенной.</summary>
    private const double StickyGap = 12;

    private void UpdateSelectionBarOffset()
    {
        if (_scroll is null || SelectionBar.Visibility != Visibility.Visible) return;
        try
        {
            // Собственное место панели на странице — то, где она была бы без сдвига.
            double barTop = SelectionBar.TranslatePoint(new Point(0, 0), this).Y - SelectionBarShift.Y;

            // Ниже последней карточки панели делать нечего: там она висела бы
            // над пустотой, оторванная от того, к чему относится.
            double listBottom = Groups.TranslatePoint(new Point(0, 0), this).Y + Groups.ActualHeight;
            double room = Math.Max(0, listBottom - barTop - SelectionBar.ActualHeight);

            double shift = Math.Clamp(_scroll.VerticalOffset - barTop + StickyGap, 0, room);
            SelectionBarShift.Y = shift;
            SelectionBar.Effect = shift > 0.5 ? (System.Windows.Media.Effects.Effect)FindResource("ToastShadow") : null;
        }
        catch { }
    }

    public override void OnHidden()
    {
        _thumbs?.Cancel();
        if (Window.GetWindow(this) is Window window) window.PreviewKeyDown -= Keys_Down;
    }

    // ---------------- Загрузка ----------------

    private async Task ReloadAsync()
    {
        string root = Services.Settings.Current.SaveRootPath;
        _all = await Task.Run(() => ClipLibrary.Scan(root));
        _loaded = true;

        FillGameFilter();
        Render();

        // Свежий список — лучший момент для уборки кэша: всё, чего в нём нет,
        // осталось от записей, удалённых автоочисткой или мимо приложения.
        var snapshot = _all.ToList();
        _ = Task.Run(() => ClipThumbnails.PruneOrphans(snapshot));
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
    /// Вне режима выделения клик открывает файл — это главное действие карточки.
    /// В режиме выделения тот же клик отмечает запись, а Shift берёт диапазон
    /// от предыдущей отмеченной.
    /// </summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ClipItem clip }) return;

        // Ctrl+ЛКМ включает выделение сразу, без захода в меню.
        if (!_selecting)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { BeginSelection(clip); return; }
            Open(clip.FullPath);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null)
        {
            SelectRange(_anchor, clip);
            return;
        }

        clip.IsSelected = !clip.IsSelected;
        _anchor = clip;
        UpdateSelectionBar();
    }

    /// <summary>Пункт «Выделить» из меню карточки: включаем режим и берём её первой.</summary>
    private void BeginSelection(ClipItem clip)
    {
        _selecting = true;
        clip.IsSelected = true;
        _anchor = clip;
        UpdateSelectionBar();
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
        _selecting = false;
        UpdateSelectionBar();
    }

    /// <summary>
    /// Панель над сеткой: сколько выделено и что с этим можно сделать.
    /// keepMode — не выключать режим на пустом выделении (нажали «Снять всё»).
    /// </summary>
    private void UpdateSelectionBar(bool keepMode = false)
    {
        var selected = SelectedItems();

        // Сняли последнюю галочку — режим выключается сам. Отдельная кнопка выхода
        // для пустого выделения не нужна: снять всё и означает «я закончил».
        if (_selecting && selected.Count == 0 && !keepMode) { _selecting = false; _anchor = null; }

        SelectionBar.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        if (!_selecting) { SelectionBarShift.Y = 0; SelectionBar.Effect = null; return; }

        // Кнопка-выключатель показывает то действие, которое сделает следующий клик
        bool all = AllShownSelected();
        SelectAllButton.Content = all ? "Снять всё" : "Выбрать всё";
        Controls.Deco.SetIcon(SelectAllButton, (Geometry)FindResource(all ? "Ico.X" : "Ico.CheckSquare"));

        // Только что показанная панель ещё не размечена — её место на странице
        // известно лишь после раскладки, поэтому сдвиг считаем следующим заходом.
        Dispatcher.BeginInvoke(UpdateSelectionBarOffset, System.Windows.Threading.DispatcherPriority.Loaded);

        SelectionText.Text = $"Выбрано {selected.Count} · {ByteSize.Format(selected.Sum(i => i.SizeBytes))}";
    }

    // ---------------- Действия над выделением ----------------

    /// <summary>
    /// Кнопка работает как выключатель: выделено не всё — выделяем всё, выделено
    /// всё — снимаем. Так одно и то же место отвечает за оба действия, и не нужно
    /// искать, чем отменить случайное «выбрать всё».
    /// </summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_shown.Count == 0) return;

        bool selectAll = !AllShownSelected();
        foreach (var item in _shown) item.IsSelected = selectAll;

        // Снятие последнего выделения обычно гасит режим — здесь этого не нужно:
        // человек нажал «Снять всё», а не «Готово», и продолжает выбирать.
        _selecting = true;
        if (!selectAll) _anchor = null;
        UpdateSelectionBar(keepMode: true);
    }

    private bool AllShownSelected() => _shown.Count > 0 && _shown.All(i => i.IsSelected);

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
    /// Правый клик ничего не выделяет — он только показывает меню. Пункты для
    /// пачки появляются, лишь когда выделение уже есть.
    /// </summary>
    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source) return;

        var selected = SelectedItems();
        var menu = (source as FrameworkElement)?.ContextMenu
                   ?? (FindCard(source) as FrameworkElement)?.ContextMenu;
        if (menu is null) return;

        bool many = selected.Count > 0;
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
