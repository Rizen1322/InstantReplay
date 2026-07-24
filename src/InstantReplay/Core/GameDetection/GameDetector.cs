using System.Diagnostics;
using InstantReplay.Core.Interop;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.GameDetection;

/// <summary>
/// Определение игры по активному процессу (foreground window) в момент сохранения.
/// Порядок: словарь известных exe → FileDescription из версии файла → имя процесса.
/// Неигровые/системные процессы → "Desktop" (как в ShadowPlay). Результат — имя папки в структуре
/// Videos/&lt;Игра&gt;/replay_*.mp4, как у ShadowPlay.
/// </summary>
public static class GameDetector
{
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = "Counter-Strike 2",
        ["csgo"] = "Counter-Strike Global Offensive",
        ["gta5"] = "GTA V",
        ["gta5_enhanced"] = "GTA V",
        ["gtav"] = "GTA V",
        ["javaw"] = "Minecraft",
        ["minecraft.windows"] = "Minecraft",
        ["dota2"] = "Dota 2",
        ["valorant-win64-shipping"] = "VALORANT",
        ["r5apex"] = "Apex Legends",
        ["r5apex_dx12"] = "Apex Legends",
        ["fortniteclient-win64-shipping"] = "Fortnite",
        ["overwatch"] = "Overwatch 2",
        ["rustclient"] = "Rust",
        ["eldenring"] = "Elden Ring",
        ["cyberpunk2077"] = "Cyberpunk 2077",
        ["witcher3"] = "The Witcher 3",
        ["rocketleague"] = "Rocket League",
        ["pubg"] = "PUBG",
        ["tslgame"] = "PUBG",
        ["escapefromtarkov"] = "Escape from Tarkov",
        ["warframe.x64"] = "Warframe",
        ["leagueoflegends"] = "League of Legends",
        ["league of legends"] = "League of Legends",
        ["hl2"] = "Half-Life 2",
        ["helldivers2"] = "Helldivers 2",
        ["starfield"] = "Starfield",
        ["baldursgate3"] = "Baldur's Gate 3", ["bg3"] = "Baldur's Gate 3", ["bg3_dx11"] = "Baldur's Gate 3",
    };

    // Процессы, которые точно не игра
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "searchhost", "shellexperiencehost", "applicationframehost",
        "chrome", "msedge", "firefox", "opera", "browser",
        "discord", "telegram", "steam", "steamwebhelper", "epicgameslauncher",
        "devenv", "code", "instantreplay", "obs64", "taskmgr",
    };

    public static string DetectForegroundGame()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Desktop";
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "Desktop";

            using var proc = Process.GetProcessById((int)pid);
            string exe = proc.ProcessName;

            if (Ignored.Contains(exe)) return "Desktop";
            if (KnownGames.TryGetValue(exe, out var known)) return Sanitize(known);

            // Пробуем человекочитаемое имя из ресурсов exe
            try
            {
                string? path = proc.MainModule?.FileName;
                if (path is not null)
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    string? desc = info.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 60)
                        return Sanitize(desc.Trim());
                }
            }
            catch { /* доступ к MainModule может быть запрещён для elevated-процессов */ }

            return Sanitize(exe);
        }
        catch (Exception ex)
        {
            Log.Warn("GameDetect", ex.Message);
            return "Desktop";
        }
    }

    /// <summary>Имя папки без запрещённых символов.</summary>
    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "Desktop" : name;
    }
}
