using System.Text.Json.Serialization;

namespace Aura.Core.Settings;

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
/// <summary>Deep — тёмно-фиолетовая с градиентами (Palette.Deep.xaml).</summary>
public enum AppTheme { Dark, Light, System, Deep }
public enum AppLanguage { Ru, En }
public enum SaveSound { None, Classic, Soft, Custom }

/// <summary>
/// Все пользовательские настройки. Сериализуются в JSON
/// (%LocalAppData%\Aura\settings.json).
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
    /// <summary>
    /// HEVC по умолчанию: файлы вдвое легче при той же картинке, и битрейты готовых
    /// наборов рассчитаны именно под него. Если видеокарта его не умеет, запуск
    /// сам уведёт настройку на H.264 (см. App.GuardCodec).
    /// </summary>
    public VideoCodec Codec { get; set; } = VideoCodec.HEVC;
    /// <summary>Индекс монитора для захвата (0 = основной).</summary>
    public int MonitorIndex { get; set; } = 0;
    /// <summary>Записывать курсор мыши.</summary>
    public bool RecordCursor { get; set; } = true;

    // ---------- Аудио ----------
    public bool CaptureGameAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    public AudioTrackMode TrackMode { get; set; } = AudioTrackMode.Mixed;
    /// <summary>ID устройства вывода (loopback). null = устройство по умолчанию.</summary>
    public string? RenderDeviceId { get; set; }
    /// <summary>ID микрофона. null = устройство по умолчанию.</summary>
    public string? CaptureDeviceId { get; set; }
    /// <summary>Шумоподавление микрофона (noise gate: убирает фоновый гул в паузах).</summary>
    public bool MicNoiseSuppression { get; set; } = false;
    /// <summary>
    /// Порог шумодава в дБ: тише этого уровня микрофон глушится.
    /// -60 пропускает почти всё, -30 режет жёстко.
    /// </summary>
    public float MicNoiseGateDb { get; set; } = -44f;

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Aura");
    /// <summary>
    /// Раскладывать записи по папкам игр (Videos\Aura\Counter-Strike 2\…).
    /// В интерфейсе переключателя нет — это поведение по умолчанию; выключить можно
    /// только правкой settings.json.
    /// </summary>
    public bool GroupByGame { get; set; } = true;
    /// <summary>
    /// Шаблон имени файла. Плейсхолдеры: {game} {date} {time} {preset}.
    /// Настройка «для своих»: меняется только в settings.json, в интерфейсе её нет.
    /// </summary>
    public string FileNameTemplate { get; set; } = "{game} {date} - {time}";
    /// <summary>Счётчик сохранённых повторов (для статистики).</summary>
    public int TotalReplaysSaved { get; set; }

    /// <summary>
    /// Чем открывать пункт «Обрезать» — путь к LosslessCut.exe. Пусто = ещё не
    /// выбран: приложение сначала поищет его само, а если не найдёт — спросит.
    /// </summary>
    public string? LosslessCutPath { get; set; }

    /// <summary>Путь к ffmpeg.exe для «Сжать для Discord». Обычно находится сам рядом с LosslessCut.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Под какой размер вложения сжимать, МБ (у Discord без Nitro — 10).</summary>
    public int AttachmentSizeMb { get; set; } = 10;

    // ---------- Уведомления / UI ----------
    public bool ShowNotifications { get; set; } = true;
    public NotificationPosition NotificationPosition { get; set; } = NotificationPosition.BottomRight;
    /// <summary>Длительность показа уведомления, сек.</summary>
    public double NotificationDurationSeconds { get; set; } = 3.5;
    /// <summary>Звук при сохранении повтора.</summary>
    public SaveSound SaveSound { get; set; } = SaveSound.Soft;
    /// <summary>Свой WAV для звука сохранения (SaveSound = Custom).</summary>
    public string? CustomSaveSoundPath { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public AppLanguage Language { get; set; } = AppLanguage.Ru;
    /// <summary>
    /// Масштаб интерфейса: 0 — подобрать по монитору (на 2K и 4K крупнее),
    /// иначе множитель 0.9 / 1.0 / 1.15 / 1.35.
    /// </summary>
    public double UiScale { get; set; } = 0;

    // ---------- Система ----------
    public bool AutoStartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;
    /// <summary>Автоматически включать Instant Replay при запуске приложения.</summary>
    public bool AutoStartReplayBuffer { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    // ---------- Драйвер NVIDIA ----------
    /// <summary>Проверять новый драйвер NVIDIA примерно раз в неделю.</summary>
    public bool CheckNvidiaDriver { get; set; } = true;
    /// <summary>Когда проверяли в последний раз (UTC). Раньше срока не дёргаем сеть.</summary>
    public DateTime LastDriverCheckUtc { get; set; } = DateTime.MinValue;
    /// <summary>
    /// О какой версии драйвера уже сообщили. Пока не выйдет следующая — молчим,
    /// чтобы уведомление не всплывало каждую неделю про один и тот же драйвер.
    /// </summary>
    public string? NotifiedDriverVersion { get; set; }
    // Репозиторий обновлений больше не настройка, а константа UpdateService.Repo:
    // релизы берутся только из официального репозитория проекта. Раньше пустое
    // значение в сохранённом settings.json молча отключало проверку обновлений.

    [JsonIgnore]
    public long BitrateBps => BitrateMbps * 1_000_000L;
}
