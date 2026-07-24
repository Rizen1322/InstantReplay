using System.Text.Json;
using System.Text.Json.Serialization;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Settings;

/// <summary>
/// Загрузка/сохранение настроек. Атомарная запись (tmp + Replace),
/// событие Changed для реактивного применения (перезапуск буфера, пересчёт статистики и т.п.).
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstantReplay");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    /// <summary>Срабатывает после Save(). Аргумент — имя изменённой группы ("" = неизвестно/всё).</summary>
    public event Action<string>? Changed;

    public void Load()
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

    /// <summary>Полный сброс к значениям по умолчанию.</summary>
    public void Reset()
    {
        Current = new AppSettings();
        Directory.CreateDirectory(Current.SaveRootPath);
        Save("");
    }

    public void Save(string changedGroup = "")
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("Settings", $"Не удалось сохранить настройки: {ex.Message}");
        }
        Changed?.Invoke(changedGroup);
    }
}
