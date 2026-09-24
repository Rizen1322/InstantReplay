using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aura.Controls;
using Aura.Core.Engine;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>
/// Настройки звука.
///
/// Источники, устройства и раскладка дорожек копятся до «Применить»: от них
/// зависит состав дорожек в файле, и конвейер пересобирается. Громкость,
/// шумодав и порог гейта применяются сразу: их подбирают на слух.
///
/// Выключенный источник прячет свои строки целиком. Серые неактивные строки
/// только отвлекали: настраивать выключенный микрофон незачем.
/// </summary>
public partial class AudioPage : PageBase
{
    private readonly DispatcherTimer _levels = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _loading;
    private bool _dirty;
    private double _gameDb = LevelMeter.FloorDb, _micDb = LevelMeter.FloorDb;

    public override string Title => "Звук";

    public override bool FillsWindow => true;

    public AudioPage()
    {
        InitializeComponent();

        // Подписка ПОСЛЕ разбора разметки: присвоение Minimum само поднимает
        // ValueChanged, а обработчик читает поля, которых в тот момент ещё нет.
        Gate.ValueChanged += Gate_Changed;
        GameVolume.ValueChanged += Volume_Changed;
        MicVolume.ValueChanged += Volume_Changed;

        _levels.Tick += (_, _) => ShowLevels();
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 820;
            AsideCol.Width = new GridLength(wide ? 300 : 0);
            Aside.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    public override void OnShown()
    {
        LoadFromSettings();
        _levels.Start();
    }

    public override void OnHidden()
    {
        _levels.Stop();
        (Window.GetWindow(this) as MainWindow)?.HideApplyBar();
    }

    // ---------------- Загрузка и сохранение ----------------

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Services.Settings.Current;
        GameAudio.IsChecked = s.CaptureGameAudio;
        MicAudio.IsChecked = s.CaptureMicrophone;
        NoiseGate.IsChecked = s.MicNoiseSuppression;
        NeuralDenoise.IsChecked = s.MicNeuralNoiseSuppression;
        Gate.Value = s.MicNoiseGateDb;
        GameVolume.Value = s.GameVolumePercent;
        MicVolume.Value = s.MicVolumePercent;
        ShowVolumes();
        ShowGate();
        SelectTrack(s.TrackMode.ToString());
        FillAudioDevices(s);
        UpdateDependents();
        _loading = false;
        SetDirty(false);
    }

    private void FillAudioDevices(AppSettings s)
    {
        RenderDevice.Items.Clear();
        CaptureDevice.Items.Clear();
        RenderDevice.Items.Add(new ComboBoxItem { Content = "Устройство по умолчанию", Tag = null });
        CaptureDevice.Items.Add(new ComboBoxItem { Content = "Устройство по умолчанию", Tag = null });

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                RenderDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                CaptureDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
        }
        catch { }

        SelectTag(RenderDevice, s.RenderDeviceId);
        SelectTag(CaptureDevice, s.CaptureDeviceId);
        ShowDeviceNames();
    }

    private void Apply_Click()
    {
        Services.Settings.Update(s =>
        {
            s.CaptureGameAudio = GameAudio.IsChecked == true;
            s.CaptureMicrophone = MicAudio.IsChecked == true;
            s.TrackMode = Enum.Parse<AudioTrackMode>((string)((ListBoxItem)TrackMode.SelectedItem).Tag);
            s.RenderDeviceId = (string?)((ComboBoxItem)RenderDevice.SelectedItem)?.Tag;
            s.CaptureDeviceId = (string?)((ComboBoxItem)CaptureDevice.SelectedItem)?.Tag;
        }, "video");
        SetDirty(false);
    }

    // ---------------- Обработчики ----------------

    private void AudioSelection_Changed(object sender, SelectionChangedEventArgs e) => Audio_Changed(sender, e);

    private void Audio_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDependents();
        ShowDeviceNames();
        if (_loading) return;
        SetDirty(true);
    }

    /// <summary>Шумодав и порог применяются сразу: значение подбирают на слух.</summary>
    private void NoiseGate_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDependents();
        ShowGate();
        if (_loading) return;
        Services.Settings.Update(s => s.MicNoiseSuppression = NoiseGate.IsChecked == true, "audio-live");
    }

    private void Gate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowGate();
        if (_loading) return;
        Services.Settings.Update(s => s.MicNoiseGateDb = (float)Gate.Value, "audio-live");
    }

    private void NeuralDenoise_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Services.Settings.Update(s => s.MicNeuralNoiseSuppression = NeuralDenoise.IsChecked == true, "audio-live");
    }

    /// <summary>Громкость, как и шумодав, применяется сразу: её подбирают на слух.</summary>
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowVolumes();
        if (_loading) return;
        int game = (int)GameVolume.Value, mic = (int)MicVolume.Value;
        Services.Settings.Update(s =>
        {
            s.GameVolumePercent = game;
            s.MicVolumePercent = mic;
        }, "audio-live");
    }

    // ---------------- Показ ----------------

    /// <summary>
    /// Строки, которые имеют смысл только при включённом источнике, прячутся
    /// вместе с ним. Раскладка дорожек нужна, только когда есть что раскладывать.
    /// </summary>
    private void UpdateDependents()
    {
        bool game = GameAudio.IsChecked == true, mic = MicAudio.IsChecked == true;
        bool gate = NoiseGate.IsChecked == true;
        GameDeviceRow.Visibility = Show(game);
        MicDeviceRow.Visibility = Show(mic);
        GameVolumeRow.Visibility = Show(game);
        MicVolumeRow.Visibility = Show(mic);
        VolumeGroup.Visibility = Show(game || mic);
        TracksGroup.Visibility = Show(game || mic);
        VoiceGroup.Visibility = Show(mic);
        GateRow.Visibility = Show(gate);
        GameMeterRow.Visibility = Show(game);
        MicMeterRow.Visibility = Show(mic);
        GateNote.Visibility = Show(mic && gate);
        MicKey.Visibility = MicDeviceName.Visibility = Show(mic);
        MicMeter.MarkDb = mic && gate ? Gate.Value : double.NaN;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ShowVolumes()
    {
        GameVolumeValue.Text = $"{(int)GameVolume.Value} %";
        MicVolumeValue.Text = $"{(int)MicVolume.Value} %";
    }

    private void ShowGate()
    {
        GateValue.Text = $"−{Math.Abs((int)Gate.Value)} дБ";
        MicMeter.MarkDb = NoiseGate.IsChecked == true && MicAudio.IsChecked == true ? Gate.Value : double.NaN;
    }

    private void ShowDeviceNames()
    {
        GameDeviceName.Text = (RenderDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        MicDeviceName.Text = (CaptureDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    }

    /// <summary>
    /// Живые уровни. Пики сглаживаются на спаде (как у обычных индикаторов), иначе
    /// шкала дёргалась бы на каждом кадре звука.
    /// </summary>
    private void ShowLevels()
    {
        bool running = Services.Engine.State != EngineState.Stopped;
        MeterIdle.Visibility = Show(!running);
        var (game, mic) = running ? Services.Engine.AudioLevels : (0f, 0f);
        _gameDb = Smooth(_gameDb, LevelMeter.ToDb(game));
        _micDb = Smooth(_micDb, LevelMeter.ToDb(mic));
        GameMeter.LevelDb = _gameDb;
        MicMeter.LevelDb = _micDb;
        GameDb.Text = DbText(_gameDb);
        MicDb.Text = DbText(_micDb);
    }

    private static double Smooth(double shown, double now) => now > shown ? now : Math.Max(now, shown - 1.5);

    private static string DbText(double db) =>
        db <= LevelMeter.FloorDb + 0.5 ? "тихо" : $"−{Math.Abs(Math.Round(db)):0} дБ";

    // ---------------- Вспомогательное ----------------

    private void SelectTrack(string tag)
    {
        foreach (ListBoxItem item in TrackMode.Items)
            if ((string)item.Tag == tag) { TrackMode.SelectedItem = item; return; }
        TrackMode.SelectedIndex = 0;
    }

    private static void SelectTag(ComboBox box, string? tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string?)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    /// <summary>
    /// Плашка «есть несохранённое» живёт в окне, а не на странице: так она видна
    /// всегда, а не только если долистать до низа.
    /// </summary>
    private void SetDirty(bool dirty)
    {
        if (_dirty == dirty) return;
        _dirty = dirty;

        var window = Window.GetWindow(this) as MainWindow;
        if (dirty) window?.ShowApplyBar("Изменения ещё не применены", Apply_Click, LoadFromSettings);
        else window?.HideApplyBar();
    }
}
