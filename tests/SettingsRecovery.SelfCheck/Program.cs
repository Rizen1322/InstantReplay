using System.IO;
using System.Text.Json;
using Aura.Core.Settings;

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }

// A separate process isolates SettingsManager's static data directory from real preferences.
string root = Environment.GetEnvironmentVariable("AURA_DATA_DIR") ??
    throw new InvalidOperationException("Set AURA_DATA_DIR to a new, isolated test folder.");
root = Path.GetFullPath(root);
Require(!Directory.Exists(root), "Use a new folder for each run.");
Require(Path.GetFullPath(SettingsManager.Dir) == root, "Settings directory is not isolated.");
string file = Path.Combine(root, "settings.json"), backup = file + ".bak";
int SavedLength()
{
    using var json = JsonDocument.Parse(File.ReadAllText(file));
    return json.RootElement.GetProperty("ReplayLengthSeconds").GetInt32();
}

var settings = new SettingsManager();
settings.Load();
settings.Update(s => { s.SaveRootPath = Path.Combine(root, "clips"); s.ReplayLengthSeconds = 77; });
Require(File.Exists(file), "First launch did not save settings in a new directory.");
Require(SavedLength() == 77, "First save lost preferences.");
settings.Update(s => s.ReplayLengthSeconds = 88);
Require(File.Exists(backup), "Previous preferences were not backed up.");
File.WriteAllText(file, "{broken");
settings.Load();
Require(settings.Current.ReplayLengthSeconds == 77, "Corrupt preferences did not recover from backup.");
Require(Directory.GetFiles(root, "settings.json.broken-*").Length > 0, "Corrupt input was not retained.");
settings.Update(s => s.ReplayLengthSeconds = 79);

string original = File.ReadAllText(file), originalBackup = File.ReadAllText(backup);
using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    settings.Load();
    settings.Update(s => s.ReplayLengthSeconds = 90);
}
settings.Save();
Require(File.ReadAllText(file) == original, "I/O failure overwrote preferences with defaults.");
Require(File.ReadAllText(backup) == originalBackup, "I/O failure overwrote the backup.");
settings.Load();
Require(settings.Current.ReplayLengthSeconds == 79, "Reload did not recover after the file lock was released.");

File.WriteAllText(file, JsonSerializer.Serialize(new
{
    Theme = "FutureTheme", ReplayLengthSeconds = 96, SaveRootPath = Path.Combine(root, "clips")
}));
settings.Load();
Require(settings.Current.ReplayLengthSeconds == 96, "Unknown enum discarded unrelated preferences.");
Require(settings.Current.Theme == new AppSettings().Theme, "Unknown enum did not use this version's default.");
bool notified = false;
settings.Changed += _ => throw new IOException("test subscriber");
settings.Changed += _ => notified = true;
settings.Update(s => s.ReplayLengthSeconds = 101);
Require(notified && SavedLength() == 101, "Subscriber failure broke persistence or another subscriber.");
settings.UpdateDeferred(s => s.ReplayLengthSeconds = 103);
settings.FlushDeferred();
Require(SavedLength() == 103, "Deferred save was lost.");

using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) settings.Load();
settings.Reset();
Require(SavedLength() == new AppSettings().ReplayLengthSeconds, "Explicit reset remained blocked after an I/O failure.");
Console.WriteLine("PASS: first launch, backup recovery, locked file, backup preservation, future enum, subscribers, deferred save, explicit reset");
