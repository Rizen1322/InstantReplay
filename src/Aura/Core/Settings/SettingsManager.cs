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

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura");
    private static string FilePath => Path.Combine(Dir, "settings.json");
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
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("Settings", $"Не удалось прочитать settings.json: {ex.Message}. Использую значения по умолчанию.");
                Current = new();
            }
            Directory.CreateDirectory(Current.SaveRootPath);
        }
    }

    /// <summary>Полный сброс к значениям по умолчанию.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Current = new AppSettings();
            Directory.CreateDirectory(Current.SaveRootPath);
        }
        Save("");
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
        lock (_sync) change(Current);
        Save(changedGroup);
    }

    public void Save(string changedGroup = "")
    {
        lock (_sync)
        {
            string tmp = TempPath;
            try
            {
                Directory.CreateDirectory(Dir);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Current, JsonOpts));

                using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);   // FlushFileBuffers: данные реально на диске
                }

                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Settings", $"Не удалось сохранить настройки: {ex.Message}");
                // Недописанный временный файл рядом с настройками никому не нужен
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // Обработчики зовём ВНЕ лока: на "video"/"audio"/"replay" движок целиком
        // пересобирает конвейер, и держать на этом лок настроек незачем и опасно.
        Changed?.Invoke(changedGroup);
    }
}
