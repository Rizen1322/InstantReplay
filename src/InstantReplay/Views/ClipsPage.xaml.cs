using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using InstantReplay.Core.Library;
using InstantReplay.Core.Logging;

namespace InstantReplay.Views;

/// <summary>
/// Панорама записей: секции по датам, внутри каждой — сетка карточек с миниатюрами,
/// плюс просмотр клипа прямо в приложении.
///
/// Раскладка на ItemsRepeater, а не на сгруппированном GridView: секция = заголовок
/// дня + UniformGridLayout, и «Сегодня»/«Вчера» гарантированно идут друг под другом.
/// Миниатюры грузятся по EffectiveViewportChanged — только у карточек, доехавших
/// до видимой области.
/// </summary>
public sealed partial class ClipsPage : Page
{
    private readonly List<ClipItem> _all = [];

    /// <summary>Элемент под курсором на момент правого клика — для контекстного меню.</summary>
    private ClipItem? _contextItem;
    /// <summary>Элемент, открытый в просмотре.</summary>
    private ClipItem? _openItem;

    private bool _filtersReady;
    private bool _scanning;
    private static bool _cachePruned;

    // ---- Плеер просмотра ----
    private MediaPlayer? _player;
    private MediaPlaybackItem? _playbackItem;
    /// <summary>Какую аудиодорожку хочет пользователь. 0 = первая (у нас это звук игры).</summary>
    private int _desiredTrack;
    private bool _syncingTrackBox;

    public ClipsPage()
    {
        InitializeComponent();

        TypeBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Свежие записи должны появляться без перехода по вкладкам
        Services.Engine.ReplaySaved += OnFileSaved;
        Services.Engine.RecordingSaved += OnFileSaved;

        _ = RefreshAsync();

        if (!_cachePruned)
        {
            _cachePruned = true;
            // Миниатюры удалённых клипов иначе лежали бы в кэше вечно
            Task.Run(() => ClipThumbnails.PruneCache(TimeSpan.FromDays(30)));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Services.Engine.ReplaySaved -= OnFileSaved;
        Services.Engine.RecordingSaved -= OnFileSaved;

        CloseTheater(); // уходя со страницы, освобождаем файл в плеере
        if (_player is not null)
        {
            _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            Player.SetMediaPlayer(null);
            _player.Dispose();
            _player = null;
        }
    }

    private void OnFileSaved(string file, int seconds) =>
        Services.Dispatcher.Enqueue(() => _ = RefreshAsync());

    // ---------------- Загрузка списка ----------------

    private async Task RefreshAsync()
    {
        if (_scanning) return;
        _scanning = true;
        if (_all.Count == 0) LoadRing.IsActive = true;

        try
        {
            string root = Services.Settings.Current.SaveRootPath;
            var items = await Task.Run(() => ClipLibrary.Scan(root));

            // Плашка «новое» — то, что сохранено за последние 15 минут
            var fresh = DateTime.Now.AddMinutes(-15);
            foreach (var item in items)
                if (item.Created >= fresh) item.IsNew = true;

            _all.Clear();
            _all.AddRange(items);
            RebuildGameFilter();
            ApplyFilters();
        }
        catch (Exception ex) { Log.Warn("Library", $"Обновление панорамы: {ex.Message}"); }
        finally
        {
            LoadRing.IsActive = false;
            _scanning = false;
        }
    }

    /// <summary>Список игр в фильтре — по числу записей, выбор пользователя сохраняем.</summary>
    private void RebuildGameFilter()
    {
        string? selected = (GameBox.SelectedItem as ComboBoxItem)?.Tag as string;

        _filtersReady = false;
        GameBox.Items.Clear();
        GameBox.Items.Add(new ComboBoxItem { Content = "Все папки", Tag = "" });

        foreach (var group in _all.GroupBy(i => i.Game)
                                  .OrderByDescending(g => g.Count())
                                  .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            GameBox.Items.Add(new ComboBoxItem { Content = $"{group.Key} ({group.Count()})", Tag = group.Key });

        GameBox.SelectedIndex = 0;
        if (selected is { Length: > 0 })
            foreach (var item in GameBox.Items)
                if (item is ComboBoxItem c && (string?)c.Tag == selected) { GameBox.SelectedItem = c; break; }

        _filtersReady = true;
    }

    private void ApplyFilters()
    {
        string query = SearchBox.Text.Trim();
        string game = (GameBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        int type = Math.Max(0, TypeBox.SelectedIndex);
        int sort = Math.Max(0, SortBox.SelectedIndex);

        IEnumerable<ClipItem> filtered = _all;
        if (query.Length > 0)
            filtered = filtered.Where(i =>
                i.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                i.Game.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (game.Length > 0) filtered = filtered.Where(i => i.Game == game);
        if (type == 1) filtered = filtered.Where(i => !i.IsScreenshot);
        else if (type == 2) filtered = filtered.Where(i => i.IsScreenshot);

        var list = sort switch
        {
            1 => filtered.OrderBy(i => i.Created).ToList(),
            2 => filtered.OrderByDescending(i => i.SizeBytes).ToList(),
            3 => filtered.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => filtered.OrderByDescending(i => i.Created).ToList(),
        };

        // Заголовки по датам осмысленны только при сортировке по времени;
        // при сортировке по размеру/названию дни были бы вперемешку.
        List<ClipGroup> sections =
            sort is 0 or 1 ? ClipLibrary.GroupByDate(list)
            : list.Count > 0 ? [new ClipGroup(sort == 2 ? "Самые крупные" : "По названию", list)]
            : [];

        Sections.ItemsSource = sections;
        Scroller.ChangeView(null, 0, null, true); // после смены фильтра — к началу списка

        UpdateSummary(list.Count);
        UpdateEmptyState(list.Count);
    }

    private void UpdateSummary(int shown)
    {
        int clips = 0, shots = 0;
        long bytes = 0;
        foreach (var item in _all)
        {
            if (item.IsScreenshot) shots++; else clips++;
            bytes += item.SizeBytes;
        }

        string total = $"{clips} клипов · {shots} скриншотов · {Core.Storage.ByteSize.Format(bytes)}";
        SummaryText.Text = shown == _all.Count ? total : $"показано {shown} из {_all.Count} · {total}";
    }

    private void UpdateEmptyState(int shown)
    {
        bool empty = shown == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;

        bool filtered = _all.Count > 0;
        EmptyTitle.Text = filtered ? "Ничего не найдено" : "Здесь пока пусто";
        EmptyHint.Text = filtered
            ? "Попробуй изменить поиск или снять фильтры."
            : "Включи Instant Replay и сохрани повтор горячей клавишей — клип появится здесь.";
    }

    // ---------------- Карточки ----------------

    private void Card_ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // Вложенный ItemsRepeater реализует все карточки своей секции сразу, поэтому
        // миниатюру просим только у тех, что уже близко к видимой области — иначе
        // открытие вкладки запускало бы сотни запросов к системным миниатюрам.
        if (args.BringIntoViewDistanceY > 600) return;
        if (sender.DataContext is ClipItem item) _ = ClipThumbnails.LoadAsync(item);
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card) return;
        AnimateScale(card, 1.03f);
        if (card.FindName("PlayOverlay") is UIElement play) play.Opacity = 1;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement card) return;
        AnimateScale(card, 1f);
        if (card.FindName("PlayOverlay") is UIElement play) play.Opacity = 0;
    }

    /// <summary>Подъём карточки под курсором — композиционная анимация, UI-поток не участвует.</summary>
    private static void AnimateScale(FrameworkElement element, float target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f, 0f);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(target, target, 1f));
        animation.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Scale", animation);
    }

    private void Card_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        _contextItem = (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) OpenInTheater(item);
    }

    // ---------------- Просмотр ----------------

    private void OpenInTheater(ClipItem item)
    {
        _openItem = item;
        TheaterTitle.Text = item.Title;
        TheaterMeta.Text = item.FullInfo;
        PlayerBar.IsOpen = false;
        TrackPanel.Visibility = Visibility.Collapsed;
        _desiredTrack = 0; // всегда начинаем с первой дорожки — это звук игры
        Theater.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EnableDependentAnimation = true
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(fade, Theater);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Begin();

        if (item.IsScreenshot)
        {
            Player.Visibility = Visibility.Collapsed;
            ShotView.Visibility = Visibility.Visible;
            try { ShotView.Source = new BitmapImage(new Uri(item.FullPath)); }
            catch { PlayerBar.IsOpen = true; }
        }
        else
        {
            ShotView.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Visible;
            _ = PlayAsync(item);
        }
    }

    private async Task PlayAsync(ClipItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
            if (!ReferenceEquals(_openItem, item)) return; // просмотр успели закрыть/переключить

            var source = MediaSource.CreateFromStorageFile(file);
            var playback = new MediaPlaybackItem(source);
            playback.AudioTracksChanged += OnAudioTracksChanged;

            _playbackItem = playback;
            EnsurePlayer().Source = playback;
        }
        catch (Exception ex)
        {
            Log.Warn("Library", $"Воспроизведение «{item.FileName}»: {ex.Message}");
            PlayerBar.IsOpen = true;
        }
    }

    /// <summary>
    /// Свой MediaPlayer, а не автоматический внутри MediaPlayerElement: нужен доступ
    /// к сессии воспроизведения, чтобы выбрать аудиодорожку в правильный момент.
    /// </summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player is null)
        {
            _player = new MediaPlayer { AutoPlay = true };
            _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            Player.SetMediaPlayer(_player);
        }
        return _player;
    }

    private void OnAudioTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args) =>
        Services.Dispatcher.Enqueue(ApplyTrackSelection);

    private void OnPlaybackStateChanged(MediaPlaybackSession session, object args)
    {
        // SelectedIndex, выставленный пока сессия в состоянии Opening, система
        // игнорирует (известная особенность MediaPlaybackItem), поэтому дожидаемся
        // любого состояния после открытия и только тогда выбираем дорожку.
        if (session.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Opening) return;
        Services.Dispatcher.Enqueue(ApplyTrackSelection);
    }

    /// <summary>
    /// В клипе с раздельным звуком дорожек две: 0 — игра, 1 — микрофон
    /// (порядок задаётся в MfMp4Writer.AddAudioStreams). Windows по умолчанию
    /// включала микрофон, поэтому дорожку выбираем сами и даём переключатель.
    /// </summary>
    private void ApplyTrackSelection()
    {
        var playback = _playbackItem;
        if (playback is null) return;

        var tracks = playback.AudioTracks;
        if (tracks.Count == 0)
        {
            TrackPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_desiredTrack >= tracks.Count) _desiredTrack = 0;
        if (tracks.SelectedIndex != _desiredTrack)
        {
            try { tracks.SelectedIndex = _desiredTrack; }
            catch (Exception ex) { Log.Warn("Library", $"Выбор аудиодорожки: {ex.Message}"); }
        }

        BuildTrackBox(tracks);
    }

    private void BuildTrackBox(MediaPlaybackAudioTrackList tracks)
    {
        // Переключатель нужен только когда дорожек больше одной
        if (tracks.Count < 2)
        {
            TrackPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _syncingTrackBox = true;
        if (TrackBox.Items.Count != tracks.Count)
        {
            TrackBox.Items.Clear();
            for (int i = 0; i < tracks.Count; i++)
                TrackBox.Items.Add(new ComboBoxItem { Content = TrackLabel(tracks, i) });
        }
        TrackBox.SelectedIndex = Math.Clamp(tracks.SelectedIndex, 0, tracks.Count - 1);
        _syncingTrackBox = false;
        TrackPanel.Visibility = Visibility.Visible;
    }

    private static string TrackLabel(MediaPlaybackAudioTrackList tracks, int index)
    {
        try
        {
            string? label = tracks[index].Label;
            if (!string.IsNullOrWhiteSpace(label)) return label!;
        }
        catch { }
        // Своя раскладка: первая дорожка — игра, вторая — микрофон
        if (tracks.Count == 2) return index == 0 ? "Звук игры" : "Микрофон";
        return $"Дорожка {index + 1}";
    }

    private void Track_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTrackBox || TrackBox.SelectedIndex < 0) return;
        _desiredTrack = TrackBox.SelectedIndex;
        ApplyTrackSelection();
    }

    private void CloseTheater()
    {
        if (Theater.Visibility == Visibility.Collapsed) return;

        // Источник обязательно снимаем: пока он назначен, файл заблокирован
        // и его нельзя ни удалить, ни переименовать.
        if (_playbackItem is not null)
        {
            _playbackItem.AudioTracksChanged -= OnAudioTracksChanged;
            _playbackItem = null;
        }
        if (_player is not null)
        {
            try { _player.Pause(); } catch { }
            _player.Source = null;
        }
        ShotView.Source = null;
        Theater.Visibility = Visibility.Collapsed;
        _openItem = null;
    }

    private void CloseTheater_Click(object sender, RoutedEventArgs e) => CloseTheater();

    private void TheaterBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Закрываем только по клику мимо содержимого, а не по самому плееру
        if (ReferenceEquals(e.OriginalSource, Theater)) CloseTheater();
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (Theater.Visibility == Visibility.Visible)
        {
            CloseTheater();
            e.Handled = true;
        }
    }

    // ---------------- Действия над файлом ----------------

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ClipItem item) OpenInTheater(item);
    }

    private void MenuExternal_Click(object sender, RoutedEventArgs e) => OpenExternal(ItemOf(sender));
    private void MenuReveal_Click(object sender, RoutedEventArgs e) => Reveal(ItemOf(sender));
    private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopyPath(ItemOf(sender));
    private void MenuRename_Click(object sender, RoutedEventArgs e) => _ = RenameAsync(ItemOf(sender));
    private void MenuDelete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(ItemOf(sender));

    private void TheaterExternal_Click(object sender, RoutedEventArgs e) => OpenExternal(_openItem);
    private void TheaterReveal_Click(object sender, RoutedEventArgs e) => Reveal(_openItem);
    private void TheaterCopy_Click(object sender, RoutedEventArgs e) => CopyPath(_openItem);
    private void TheaterRename_Click(object sender, RoutedEventArgs e) => _ = RenameAsync(_openItem);
    private void TheaterDelete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(_openItem);

    /// <summary>Элемент, к которому относится пункт меню (DataContext пункта, иначе — под правым кликом).</summary>
    private ClipItem? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ClipItem ?? _contextItem;

    private static void OpenExternal(ClipItem? item)
    {
        if (item is null) return;
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Library", $"Открытие «{item.FileName}»: {ex.Message}"); }
    }

    private static void Reveal(ClipItem? item)
    {
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Library", $"Показ в папке: {ex.Message}"); }
    }

    private static void CopyPath(ClipItem? item)
    {
        if (item is null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(item.FullPath);
            Clipboard.SetContent(package);
        }
        catch (Exception ex) { Log.Warn("Library", $"Копирование пути: {ex.Message}"); }
    }

    private async Task RenameAsync(ClipItem? item)
    {
        if (item is null) return;

        var box = new TextBox { Text = item.Title, SelectionStart = item.Title.Length };
        var dialog = new ContentDialog
        {
            Title = "Новое имя файла",
            Content = box,
            PrimaryButtonText = "Переименовать",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string name = box.Text.Trim();
        if (name.Length == 0 || name == item.Title) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await MessageAsync("Недопустимое имя", "В имени файла есть символы, которые Windows не разрешает.");
            return;
        }

        bool wasOpen = ReferenceEquals(_openItem, item);
        CloseTheater(); // плеер держит файл открытым

        string target = Path.Combine(Path.GetDirectoryName(item.FullPath)!,
                                     name + Path.GetExtension(item.FullPath));
        try
        {
            if (File.Exists(target))
            {
                await MessageAsync("Файл уже есть", "В этой папке уже лежит файл с таким именем.");
                return;
            }
            File.Move(item.FullPath, target);
            ClipThumbnails.Forget(item);          // ключ кэша построен на старом пути
            Services.Storage.Rename(item.FullPath, target);
            await RefreshAsync();
            if (wasOpen && _all.FirstOrDefault(i => i.FullPath == target) is ClipItem renamed)
                OpenInTheater(renamed);
        }
        catch (Exception ex)
        {
            await MessageAsync("Не удалось переименовать", ex.Message);
        }
    }

    private async Task DeleteAsync(ClipItem? item)
    {
        if (item is null) return;

        var dialog = new ContentDialog
        {
            Title = "Удалить запись?",
            Content = $"«{item.FileName}» ({item.SizeText}) будет перемещён в корзину — оттуда его можно вернуть.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        CloseTheater();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
            // Default (не PermanentDelete) — файл уходит в корзину
            await file.DeleteAsync(StorageDeleteOption.Default);

            ClipThumbnails.Forget(item);
            Services.Storage.Forget(item.FullPath); // индекс папки и статистика
            _all.Remove(item);
            RebuildGameFilter();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            await MessageAsync("Не удалось удалить", ex.Message);
        }
    }

    private async Task MessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }

    // ---------------- Фильтры ----------------

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_filtersReady) ApplyFilters();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filtersReady) ApplyFilters();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
