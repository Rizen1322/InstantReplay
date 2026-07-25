using System.Text.Json.Serialization;

namespace InstantReplay.Core.Settings;

public enum VideoCodec { H264, HEVC, AV1 }
public enum AudioTrackMode
{
    /// <summary>Одна дорожка: игра + микрофон смикшированы.</summary>
    Mixed,
    /// <summary>Две отдельные дорожки: игра и микрофон.</summary>
    Separate,
    GameOnly,
    MicOnly
}
public enum NotificationPosition { TopLeft, TopRight, BottomLeft, BottomRight, TopCenter }
public enum AppTheme { Dark, Light, System }
public enum AppLanguage { Ru, En }
public enum SaveSound { None, Classic, Soft, Custom }

/// <summary>
/// Все пользовательские настройки. Сериализуются в JSON
/// (%LocalAppData%\InstantReplay\settings.json).
/// </summary>
public sealed class AppSettings
{
    // ---------- Replay ----------
    /// <summary>Длина Instant Replay в секундах (15..900, либо своё значение).</summary>
    public int ReplayLengthSeconds { get; set; } = 180;

    // ---------- Видео ----------
    /// <summary>Высота кадра: 720/1080/1440/2160. Ширина считается по аспекту монитора.</summary>
    public int VerticalResolution { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    /// <summary>Битрейт в Mbps (ползунок 10..80).</summary>
    public int BitrateMbps { get; set; } = 35;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    /// <summary>Индекс монитора для захвата (0 = основной).</summary>
    public int MonitorIndex { get; set; } = 0;
    /// <summary>Записывать курсор мыши.</summary>
    public bool RecordCursor { get; set; } = true;

    // ---------- Аудио ----------
    public bool CaptureGameAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    public AudioTrackMode TrackMode { get; set; } = AudioTrackMode.Mixed;
    /// <summary>Громкость игры 0..2 (1 = 100%).</summary>
    public float GameVolume { get; set; } = 1.0f;
    /// <summary>Громкость микрофона 0..2.</summary>
    public float MicVolume { get; set; } = 1.0f;
    /// <summary>ID устройства вывода (loopback). null = устройство по умолчанию.</summary>
    public string? RenderDeviceId { get; set; }
    /// <summary>ID микрофона. null = устройство по умолчанию.</summary>
    public string? CaptureDeviceId { get; set; }
    /// <summary>Шумоподавление микрофона (noise gate: убирает фоновый гул в паузах).</summary>
    public bool MicNoiseSuppression { get; set; } = false;

    // ---------- Горячие клавиши (строковый формат "Alt+F10") ----------
    public string HotkeySaveReplay { get; set; } = "Alt+F10";
    public string HotkeySaveLast30 { get; set; } = "Alt+Shift+F10";
    public string HotkeyToggleInstantReplay { get; set; } = "Alt+F11";
    public string HotkeyStartRecording { get; set; } = "Alt+F9";
    public string HotkeyStopRecording { get; set; } = "Alt+Shift+F9";
    public string HotkeyScreenshot { get; set; } = "Alt+F12";
    public string HotkeyOpenFolder { get; set; } = "Alt+F8";

    // ---------- Хранилище ----------
    public string SaveRootPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "InstantReplay");
    /// <summary>Максимальный размер папки записей, ГБ. 0 = без лимита.</summary>
    public int MaxFolderSizeGb { get; set; } = 50;
    /// <summary>Минимум свободного места на диске, ГБ, при котором чистим старые записи.</summary>
    public int MinFreeSpaceGb { get; set; } = 5;
    public bool AutoDeleteOldClips { get; set; } = true;
    /// <summary>Раскладывать записи по папкам игр (Videos\InstantReplay\Counter-Strike 2\…).</summary>
    public bool GroupByGame { get; set; } = true;
    /// <summary>Шаблон имени файла. Плейсхолдеры: {game} {date} {time} {preset}.</summary>
    public string FileNameTemplate { get; set; } = "{game} {date} - {time}";
    /// <summary>Счётчик сохранённых повторов (для статистики).</summary>
    public int TotalReplaysSaved { get; set; }

    // ---------- Уведомления / UI ----------
    public bool ShowNotifications { get; set; } = true;
    public NotificationPosition NotificationPosition { get; set; } = NotificationPosition.BottomRight;
    /// <summary>Длительность показа уведомления, сек.</summary>
    public double NotificationDurationSeconds { get; set; } = 3.5;
    /// <summary>Звук при сохранении повтора.</summary>
    public SaveSound SaveSound { get; set; } = SaveSound.Soft;
    /// <summary>Свой WAV для звука сохранения (SaveSound = Custom).</summary>
    public string? CustomSaveSoundPath { get; set; }
    /// <summary>Открывать папку с файлом после сохранения.</summary>
    public bool OpenFolderAfterSave { get; set; } = false;
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public AppLanguage Language { get; set; } = AppLanguage.Ru;

    // ---------- Система ----------
    public bool AutoStartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;
    /// <summary>Автоматически включать Instant Replay при запуске приложения.</summary>
    public bool AutoStartReplayBuffer { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    // Репозиторий обновлений больше не настройка, а константа UpdateService.Repo:
    // релизы берутся только из официального репозитория проекта. Раньше пустое
    // значение в сохранённом settings.json молча отключало проверку обновлений.

    [JsonIgnore]
    public long BitrateBps => BitrateMbps * 1_000_000L;
}
