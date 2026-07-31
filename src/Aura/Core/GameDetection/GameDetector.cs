using System.Diagnostics;
using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.GameDetection;

/// <summary>
/// Определение игры по активному окну в момент сохранения.
/// Порядок: своё окно и заведомо неигровые программы → «Desktop», затем словарь
/// известных игр, затем название из ресурсов exe, затем имя процесса.
/// Результат — имя папки: Videos\Aura\&lt;Игра&gt;\replay_*.mp4.
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
        ["deadlock"] = "Deadlock",
        ["destiny2"] = "Destiny 2",
        ["wow"] = "World of Warcraft", ["wow-64"] = "World of Warcraft", ["wowclassic"] = "World of Warcraft Classic",
        ["aces"] = "War Thunder",
        ["worldoftanks"] = "World of Tanks",
        ["dayz"] = "DayZ",
        ["arma3_x64"] = "Arma 3",
        ["starcitizen"] = "Star Citizen",
        ["factorio"] = "Factorio",
        ["terraria"] = "Terraria",
        ["stardewvalley"] = "Stardew Valley",
        ["beamng.drive"] = "BeamNG.drive", ["beamng.drive.x64"] = "BeamNG.drive",
    };

    /// <summary>
    /// Программы, которые игрой быть не могут. Записать их, конечно, можно —
    /// но заводить под них папку в библиотеке записей незачем: клип уедет в «Desktop».
    ///
    /// Список по категориям, чтобы дописывать было куда. Имя — без «.exe».
    /// </summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- само приложение и его прошлое имя ----
        "aura", "instantreplay",

        // ---- оболочка и системные окна Windows ----
        "explorer", "dwm", "searchhost", "searchapp", "shellexperiencehost",
        "applicationframehost", "startmenuexperiencehost", "textinputhost", "lockapp",
        "systemsettings", "taskmgr", "mmc", "control", "rundll32", "sihost", "ctfmon",
        "openwith", "msinfo32", "regedit", "dllhost", "wscript", "cscript",
        "cmd", "powershell", "pwsh", "windowsterminal", "wt", "conhost",

        // ---- браузеры ----
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "operagx",
        "browser", "brave", "vivaldi", "iexplore", "chromium", "arc", "tor", "yandex",

        // ---- общение и созвоны ----
        "discord", "discordptb", "discordcanary", "telegram", "whatsapp", "viber",
        "skype", "slack", "teams", "ms-teams", "zoom", "webex", "signal", "element",
        "guilded", "thunderbird", "outlook", "mail", "teamspeak3", "ts3client_win64", "mumble",

        // ---- ИИ-приложения и заметки ----
        "claude", "chatgpt", "copilot", "perplexity", "notion", "obsidian",
        "evernote", "onenote", "todoist", "anytype",

        // ---- разработка и редакторы ----
        "devenv", "code", "cursor", "rider64", "rider", "idea64", "pycharm64", "webstorm64",
        "clion64", "goland64", "studio64", "sublime_text", "notepad++", "notepad", "wordpad",
        "atom", "eclipse", "gitkraken", "sourcetree", "fork", "mobaxterm", "putty",
        "winscp", "filezilla", "docker desktop", "postman", "figma",

        // ---- игровые движки и редакторы (в них не играют, а работают) ----
        "unity", "unityhub", "unrealeditor", "ue4editor", "ue5editor", "godot",
        "blender", "substance painter", "3dsmax", "maya",

        // ---- лаунчеры и магазины ----
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "battle.net",
        "battlenet", "riotclientux", "riotclientservices", "leagueclient", "leagueclientux",
        "origin", "eadesktop", "ealauncher", "eabackgroundservice", "uplay", "upc",
        "ubisoftgamelauncher", "ubisoftconnect", "galaxyclient", "rockstarlauncher",
        "socialclubhelper", "playnite.desktopapp", "playnite.fullscreenapp", "itch",
        "gamingservices", "xboxpcapp", "wgc", "vkplay", "vkplayloader", "mailrugameloader",

        // ---- запись, стрим, оверлеи ----
        "obs64", "obs32", "obs", "streamlabs obs", "streamlabs", "xsplit.core",
        "nvidia share", "nvidia app", "radeonsoftware", "amdow", "bandicam", "fraps",
        "medal", "overwolf", "overwolfbrowser", "camtasia", "sharex", "screenclippinghost",
        "snippingtool", "screensketch", "elgato stream deck", "streamdeck",

        // ---- медиа ----
        "vlc", "mpc-hc64", "mpc-be64", "potplayermini64", "potplayer", "wmplayer", "mpv",
        "spotify", "yandexmusic", "aimp", "foobar2000", "itunes", "netflix", "plex",
        "plexmediaplayer", "kinopoisk", "ivi", "musicui",

        // ---- офис и чтение ----
        "winword", "excel", "powerpnt", "acrobat", "acrord32", "sumatrapdf",
        "foxitreader", "calibre", "wps", "wpp",

        // ---- файлы и архивы ----
        "utorrent", "qbittorrent", "bittorrent", "transmission", "totalcmd", "doublecmd",
        "7zfm", "winrar", "winzip", "teracopy",

        // ---- удалённый доступ и виртуалки ----
        "anydesk", "teamviewer", "rustdesk", "parsec", "mstsc", "vmware", "virtualbox",
        "vmconnect", "vmwareworkstation",

        // ---- утилиты, мониторинг, подсветка ----
        "ccleaner", "hwinfo64", "hwinfo", "msiafterburner", "rtss", "cpu-z", "gpu-z",
        "hwmonitor", "aida64", "crystaldiskinfo", "icue", "lghub", "synapse",
        "armourycrate", "msi center", "nzxt cam", "signalrgb", "openrgb", "fancontrol",
    };

    /// <summary>Своё имя процесса: окно самой Aura игрой считать нельзя ни при каком раскладе.</summary>
    private static readonly string SelfProcess = GetSelfName();

    private static string GetSelfName()
    {
        try { return Process.GetCurrentProcess().ProcessName; }
        catch { return "aura"; }
    }

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

            // Словарь игр проверяем первым: если имя вдруг попадёт и в список
            // исключений, игра всё равно выиграет.
            if (KnownGames.TryGetValue(exe, out var known)) return Sanitize(known);
            if (exe.Equals(SelfProcess, StringComparison.OrdinalIgnoreCase)) return "Desktop";
            if (Ignored.Contains(exe)) return "Desktop";

            string? path = null;
            try { path = proc.MainModule?.FileName; }
            catch { /* MainModule недоступен у процессов с более высокими правами */ }

            // Всё из системных папок Windows — точно не игра (игры туда не ставятся)
            if (IsWindowsComponent(path)) return "Desktop";

            // Пробуем человекочитаемое имя из ресурсов exe
            if (path is not null)
                try
                {
                    string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 60)
                        return Sanitize(desc.Trim());
                }
                catch { }

            return Sanitize(exe);
        }
        catch (Exception ex)
        {
            Log.Warn("GameDetect", ex.Message);
            return "Desktop";
        }
    }

    /// <summary>Программа лежит в системных папках Windows — служебное окно, а не игра.</summary>
    private static bool IsWindowsComponent(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
