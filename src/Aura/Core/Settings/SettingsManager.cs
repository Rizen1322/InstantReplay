using System.Text.Json;
using System.Text.Json.Serialization;
using Aura.Core.Logging;

namespace Aura.Core.Settings;

/// <summary>
/// Загрузка/сохранение настроек. Атомарная запись (tmp + Replace),
/// событие Changed для реактивного применения (перезапуск буфера, пересчёт статистики и т.п.).
///
/// Три правила, из-за которых здесь лок и своё имя временного файла:
///
/// 1. САМА ЗАПИСЬ ПОД ЛОКОМ. Save зовут и из потока интерфейса, и из фонового
///    потока сохранения клипа (счётчик повторов). Раньше оба писали в один и тот же
///    settings.json.tmp без всякой синхронизации: в лучшем случае это исключение,
///    которое проглатывалось логом, в худшем — потерянная запись.
///
/// 2. ВРЕМЕННЫЙ ФАЙЛ СВОЙ НА ПРОЦЕСС. Лок спасает только внутри процесса, а копия
///    с ключом --dev работает рядом с обычной и делит ту же папку настроек.
///
/// 3. СБРОС НА ДИСК ЯВНЫЙ. File.WriteAllText отдаёт данные кэшу файловой системы и
///    возвращается; после внезапной перезагрузки на месте настроек оказывался файл
///    нулевой длины. FlushFileBuffers до подмены это закрывает.
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Папка настроек. Ключ --data-dir или переменная AURA_DATA_DIR подменяют её для
    /// проверочных прогонов: они не должны переписывать настройки установленной копии.
    /// Ключ нужен, потому что Aura запускается с правами администратора, а
    /// повышенный процесс получает окружение заново и переменную не видит.
    /// </summary>
    public static string Dir { get; } = ResolveDir();

    private static string ResolveDir()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--data-dir");
        if (i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0) return args[i + 1];
        return Environment.GetEnvironmentVariable("AURA_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura");
    }
    private static string FilePath => Path.Combine(Dir, "settings.json");
    /// <summary>Прошлая удачно записанная версия: File.Replace кладёт её сюда при каждом сохранении.</summary>
    private static string BackupPath => Path.Combine(Dir, "settings.json.bak");
    private static string TempPath => Path.Combine(Dir, $"settings.json.{Environment.ProcessId}.tmp");

    /// <summary>Защищает файл настроек и подмену объекта Current.</summary>
    private readonly object _sync = new();

    public AppSettings Current { get; private set; } = new();

    /// <summary>Срабатывает после Save(). Аргумент — имя изменённой группы ("" = неизвестно/всё).</summary>
    public event Action<string>? Changed;

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Current = ReadOrRecover();
                    Current.Normalize();

                    // Discord поднял лимит вложения с 10 МБ до 20. Настройку никто руками не
                    // задаёт — поля в интерфейсе нет, — так что записанная старая десятка это
                    // просто прежнее значение по умолчанию, застрявшее в файле.
                    if (Current.AttachmentSizeMb == AppSettings.LegacyAttachmentSizeMb)
                        Current.AttachmentSizeMb = AppSettings.DefaultAttachmentSizeMb;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings", $"Не удалось прочитать settings.json: {ex.Message}. Использую значения по умолчанию.");
                Current = new();
            }
            try { Directory.CreateDirectory(Current.SaveRootPath); }
            catch (Exception ex)
            {
                Log.Error("Settings", $"Папка записей недоступна ({ex.Message}) — возвращаю стандартную.");
                Current.SaveRootPath = new AppSettings().SaveRootPath;
                Directory.CreateDirectory(Current.SaveRootPath);
            }
        }
    }

    /// <summary>
    /// Прочитать settings.json. Если он испорчен (обрыв питания посреди записи,
    /// ручная правка с ошибкой), файл не пропадает: он откладывается рядом как
    /// «settings.json.broken-…», а настройки берутся из прошлой удачной версии.
    /// Раньше испорченный файл молча заменялся значениями по умолчанию при первом
    /// же сохранении, и все настройки терялись насовсем.
    /// </summary>
    private static AppSettings ReadOrRecover()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts)
                   ?? throw new JsonException("файл пуст");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            string broken = Path.Combine(Dir, $"settings.json.broken-{DateTime.Now:yyyyMMdd-HHmmss}");
            try { File.Copy(FilePath, broken, overwrite: true); } catch { }
            Log.Error("Settings", $"settings.json испорчен ({ex.Message}), копия: {Path.GetFileName(broken)}");
            try
            {
                if (File.Exists(BackupPath) &&
                    JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(BackupPath), JsonOpts) is { } backup)
                {
                    Log.Warn("Settings", "Настройки восстановлены из settings.json.bak");
                    // Испорченный файл уже отложен. Иначе следующее сохранение
                    // переложило бы его на место резервной копии.
                    try { File.Copy(BackupPath, FilePath, overwrite: true); } catch { }
                    return backup;
                }
            }
            catch (Exception bex) { Log.Warn("Settings", $"settings.json.bak тоже не читается: {bex.Message}"); }
            Log.Warn("Settings", "Резервной копии нет, использую значения по умолчанию");
            return new AppSettings();
        }
    }

    /// <summary>Полный сброс к значениям по умолчанию.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Current = new AppSettings();
            Directory.CreateDirectory(Current.SaveRootPath);
            SaveLocked();
        }
        Changed?.Invoke("");
    }

    /// <summary>
    /// Изменить настройки под локом и сохранить.
    ///
    /// Нужен там, где правку делает не поток интерфейса: счётчик сохранённых повторов
    /// раньше увеличивался прямо из фонового потока записи файла (`TotalReplaysSaved++`),
    /// а сериализация могла идти в этот же момент с другого потока.
    /// </summary>
    public void Update(Action<AppSettings> change, string changedGroup = "")
    {
        lock (_sync)
        {
            change(Current);
            SaveLocked();
        }
        Changed?.Invoke(changedGroup);
    }

    /// <summary>
    /// Как <see cref="Update"/>, но на диск пишется не сразу, а через 0.4 с после
    /// последней правки. Для ползунков: при перетаскивании значение меняется
    /// десятки раз в секунду, и каждый раз файл писался со сбросом на диск.
    /// Движок видит новое значение сразу, через Changed.
    /// </summary>
    public void UpdateDeferred(Action<AppSettings> change, string changedGroup = "")
    {
        lock (_sync)
        {
            change(Current);
            _deferredSave ??= new System.Threading.Timer(_ => FlushDeferred(), null, Timeout.Infinite, Timeout.Infinite);
            _deferredSave.Change(400, Timeout.Infinite);
            _savePending = true;
        }
        Changed?.Invoke(changedGroup);
    }

    private System.Threading.Timer? _deferredSave;
    private bool _savePending;

    /// <summary>Дописать отложенное сохранение. Зовётся и при выходе из приложения.</summary>
    public void FlushDeferred()
    {
        lock (_sync)
        {
            if (!_savePending) return;
            _savePending = false;
            SaveLocked();
        }
    }

    public void Save(string changedGroup = "")
    {
        lock (_sync)
        {
            SaveLocked();
        }

        // Обработчики зовём ВНЕ лока: на "video"/"audio"/"replay" движок целиком
        // пересобирает конвейер, и держать на этом лок настроек незачем и опасно.
        Changed?.Invoke(changedGroup);
    }

    private void SaveLocked()
    {
        _savePending = false;
        string tmp = TempPath;
        try
        {
            Current.Normalize();
            Directory.CreateDirectory(Dir);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Current, JsonOpts));

            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Settings", $"Не удалось сохранить настройки: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
