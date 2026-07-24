using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NAudio.CoreAudioApi;
using InstantReplay.Core.Engine;
using InstantReplay.Core.Settings;

namespace InstantReplay.Views;

public sealed partial class AudioPage : Page
{
    private bool _loading = true;
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public AudioPage()
    {
        InitializeComponent();
        _levelTimer.Tick += (_, _) => UpdateLevels();

        Loaded += (_, _) =>
        {
            BuildDevices();
            LoadFromSettings();
            Services.Engine.StateChanged += OnEngineState;
            OnEngineState(Services.Engine.State);
            _levelTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _levelTimer.Stop();
            Services.Engine.StateChanged -= OnEngineState;
        };
    }

    private void BuildDevices()
    {
        RenderDeviceBox.Items.Clear();
        CaptureDeviceBox.Items.Clear();
        RenderDeviceBox.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = "" });
        CaptureDeviceBox.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = "" });
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                RenderDeviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.ID });
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                CaptureDeviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.ID });
        }
        catch { }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Services.Settings.Current;
        GameAudioToggle.IsOn = s.CaptureGameAudio;
        MicToggle.IsOn = s.CaptureMicrophone;
        SelectByTag(TrackModeBox, s.TrackMode.ToString());
        SelectByTag(RenderDeviceBox, s.RenderDeviceId ?? "");
        if (RenderDeviceBox.SelectedIndex < 0) RenderDeviceBox.SelectedIndex = 0;
        SelectByTag(CaptureDeviceBox, s.CaptureDeviceId ?? "");
        if (CaptureDeviceBox.SelectedIndex < 0) CaptureDeviceBox.SelectedIndex = 0;
        NoiseToggle.IsOn = s.MicNoiseSuppression;
        GameVolSlider.Value = s.GameVolume * 100;
        MicVolSlider.Value = s.MicVolume * 100;
        UpdateVolumeTexts();
        _loading = false;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        s.CaptureGameAudio = GameAudioToggle.IsOn;
        s.CaptureMicrophone = MicToggle.IsOn;
        if (Enum.TryParse(Tag(TrackModeBox), out AudioTrackMode tm)) s.TrackMode = tm;
        string render = Tag(RenderDeviceBox);
        s.RenderDeviceId = render.Length == 0 ? null : render;
        string capture = Tag(CaptureDeviceBox);
        s.CaptureDeviceId = capture.Length == 0 ? null : capture;
        s.MicNoiseSuppression = NoiseToggle.IsOn;
        Services.Settings.Save("audio");

        ApplyBtn.Content = "Применено ✓";
        _ = ResetApplyLabelAsync();
    }

    private async Task ResetApplyLabelAsync()
    {
        await Task.Delay(1500);
        ApplyBtn.Content = "Применить";
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateVolumeTexts();
        if (_loading) return;
        // Громкость применяется на лету, без перезапуска конвейера
        Services.Settings.Current.GameVolume = (float)(GameVolSlider.Value / 100.0);
        Services.Settings.Current.MicVolume = (float)(MicVolSlider.Value / 100.0);
        Services.Settings.Save("volume");
    }

    private void UpdateVolumeTexts()
    {
        GameVolText.Text = $"{(int)GameVolSlider.Value}%";
        MicVolText.Text = $"{(int)MicVolSlider.Value}%";
    }

    private void UpdateLevels()
    {
        if (Services.Engine.State == EngineState.Stopped)
        {
            GameLevel.Value = 0;
            MicLevel.Value = 0;
            return;
        }
        var (game, mic) = Services.Engine.AudioLevels;
        GameLevel.Value = Math.Min(1, game);
        MicLevel.Value = Math.Min(1, mic);
    }

    private void OnEngineState(EngineState st) => Services.Dispatcher.Enqueue(() =>
    {
        bool locked = st != EngineState.Stopped;
        ActiveBar.IsOpen = locked;
        UiUtil.SetEnabledRecursive(SourcesCard, !locked);
        ApplyBtn.IsEnabled = !locked;
    });

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem c && (string?)c.Tag == tag) { box.SelectedItem = c; return; }
    }

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
