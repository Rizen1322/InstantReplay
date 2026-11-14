using System.Collections.ObjectModel;
using System.Windows;
using Aura.Core.Logging;
using Aura.Core.Tools;
using Microsoft.Win32;

namespace Aura.Views;

public partial class VideoAssemblyWindow : Window
{
    private readonly ObservableCollection<VideoSegment> _segments = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _export;
    private bool _adding;
    private string? _ffmpeg;

    private VideoAssemblyWindow()
    {
        InitializeComponent();
        Segments.ItemsSource = _segments;
        _segments.CollectionChanged += (_, _) => UpdateSummary();
        SizeLimit.Text = Services.Settings.Current.AttachmentSizeMb.ToString();
        _ffmpeg = Ffmpeg.Find(Services.Settings.Current.FfmpegPath, Services.Settings.Current.LosslessCutPath);
        VideoFileDrop.Attach(this, paths => _ = AddFilesAsync(paths));
        Closed += (_, _) => { _lifetime.Cancel(); _export?.Cancel(); };
        UpdateSummary();
    }

    public static void ShowFiles(IEnumerable<string> paths) => ShowSegments([], paths);

    public static void ShowSegments(IEnumerable<VideoSegment> segments, IEnumerable<string>? paths = null)
    {
        var window = new VideoAssemblyWindow();
        foreach (var segment in segments) window._segments.Add(segment);
        var owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (paths is not null) window.Loaded += async (_, _) => await window.AddFilesAsync(paths.ToArray());
        window.ShowDialog();
    }

    private async Task AddFilesAsync(string[] paths)
    {
        if (_export is not null || _adding || _lifetime.IsCancellationRequested) return;
        if (paths.Length == 0) return;
        if (paths.Any(p => !File.Exists(p) || !VideoFiles.IsVideo(p)))
        { Dialogs.Say("Выберите видео", "Добавляйте видеофайлы, а не папки или изображения."); return; }
        _adding = true;
        Save.IsEnabled = false;
        try
        {
            var added = new List<VideoSegment>();
            foreach (string path in paths)
            {
                Status.Text = "Открываю " + Path.GetFileName(path) + "…";
                var info = await VideoAssembly.ReadInfoAsync(path, _lifetime.Token);
                added.Add(new VideoSegment(Path.GetFullPath(path), TimeSpan.Zero, info.Duration));
            }
            foreach (var segment in added) _segments.Add(segment);
            Status.Text = "Видео добавлены. Порядок сверху вниз совпадает с порядком в готовом ролике.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally { _adding = false; UpdateSummary(); }
    }

    private void UpdateSummary()
    {
        Summary.Text = $"Фрагментов: {_segments.Count} · Длительность: {TimeSpan.FromTicks(_segments.Sum(s => s.Duration.Ticks)):hh\\:mm\\:ss\\.fff}";
        Save.IsEnabled = _segments.Count > 0 && !_adding && _export is null;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = VideoFiles.Filter, Multiselect = true, Title = "Добавить видео в ролик" };
        if (picker.ShowDialog(this) == true) await AddFilesAsync(picker.FileNames);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || Segments.SelectedItem is not VideoSegment segment) return;
        int index = _segments.IndexOf(segment);
        var edited = ClipEditorWindow.PickSegments(segment, this);
        if (edited is null) return;
        _segments.RemoveAt(index);
        foreach (var part in edited) _segments.Insert(index++, part);
        Segments.SelectedIndex = index - 1;
    }

    private void Move(int direction)
    {
        int index = Segments.SelectedIndex, target = index + direction;
        if (_export is null && index >= 0 && target >= 0 && target < _segments.Count)
        { _segments.Move(index, target); Segments.SelectedIndex = target; }
    }
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Remove_Click(object sender, RoutedEventArgs e)
    { if (_export is null && Segments.SelectedIndex >= 0) _segments.RemoveAt(Segments.SelectedIndex); }

    private void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        _ffmpeg = SelectFfmpeg(this) ?? _ffmpeg;
        if (_ffmpeg is not null) Status.Text = "Дополнительные форматы подключены.";
    }

    internal static string? SelectFfmpeg(Window owner)
    {
        var picker = new OpenFileDialog { Title = "Выбрать ffmpeg.exe", Filter = "ffmpeg|ffmpeg.exe" };
        if (picker.ShowDialog(owner) != true) return null;
        try
        {
            string ffmpeg = ExecutableTrust.RequireProtected(picker.FileName);
            Services.Settings.Update(s => s.FfmpegPath = ffmpeg, "tools");
            return ffmpeg;
        }
        catch (Exception ex) { Dialogs.Say("Не удалось подключить ffmpeg", ex.Message); }
        return null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_export is not null || _adding || _segments.Count == 0) return;
        int? limit = null;
        if (LimitSize.IsChecked == true)
        {
            if (!int.TryParse(SizeLimit.Text, out int megabytes) || megabytes is < 1 or > 4096)
            { Status.Text = "Укажите лимит от 1 до 4096 МБ."; return; }
            limit = megabytes;
        }
        var picker = new SaveFileDialog
        {
            Title = "Сохранить собранный ролик", Filter = "Видео MP4|*.mp4", DefaultExt = ".mp4",
            FileName = "Сборка.mp4", InitialDirectory = Path.GetDirectoryName(_segments[0].Path), OverwritePrompt = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName)) { Status.Text = "Выберите новое имя: существующие файлы не перезаписываются."; return; }
        var parts = _segments.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _export = cancellation;
        EditingButtons.IsEnabled = ExportOptions.IsEnabled = Segments.IsEnabled = Save.IsEnabled = false;
        CancelExport.Visibility = Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Status.Text = "Собираю ролик…";
        try
        {
            string output = await VideoAssembly.RenderAsync(parts, picker.FileName, _ffmpeg, limit,
                new Progress<double>(p => Progress.Value = p), cancellation.Token);
            ClipCommands.RegisterSavedVideo(output);
            Status.Text = "Сохранено: " + output;
        }
        catch (OperationCanceledException) { Status.Text = "Сохранение отменено."; }
        catch (Exception ex) { Status.Text = ex.Message; Log.Error("Editor", ex); }
        finally
        {
            _export = null;
            EditingButtons.IsEnabled = ExportOptions.IsEnabled = Segments.IsEnabled = true;
            CancelExport.Visibility = Progress.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _export?.Cancel();
}
