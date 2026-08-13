using System.Text.Json;
using System.Text.Json.Serialization;
using Aura.Core.Logging;

namespace Aura.Core.GameDetection;

/// <summary>
/// Списки известных игр и заведомо неигровых программ.
///
/// ВСТРОЕННЫЕ ЗНАЧЕНИЯ — база, которая работает всегда, даже если файлов нет или они
/// испорчены. Поверх неё накладываются два <c>games.json</c>:
///
/// • рядом с приложением — список можно обновить, не пересобирая код;
/// • <c>%LocalAppData%\Aura\games.json</c> — свой список пользователя, он главнее.
///
/// Файл содержит только ОТЛИЧИЯ от встроенного, поэтому он короткий, а его отсутствие
/// или синтаксическая ошибка ничего не ломают: детектор просто работает на встроенных
/// значениях. Раньше и словарь игр, и список исключений лежали прямо в коде — каждая
/// новая игра требовала выпуска версии.
///
/// Формат:
/// <code>
/// {
///   "games":   { "cs2": "Counter-Strike 2" },   // имя exe без расширения → имя папки
///   "ignored": ["chrome", "discord"],           // добавить в исключения
///   "allowed": ["obs64"]                        // СНЯТЬ встроенное исключение
/// }
/// </code>
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<string, string> _games;
    private readonly HashSet<string> _ignored;

    private GameDatabase(Dictionary<string, string> games, HashSet<string> ignored)
    {
        _games = games;
        _ignored = ignored;
    }

    public int GameCount => _games.Count;
    public int IgnoredCount => _ignored.Count;

    /// <summary>Человекочитаемое имя игры по имени exe (без расширения).</summary>
    public bool TryGetGame(string exe, out string name) => _games.TryGetValue(exe, out name!);

    /// <summary>Программа заведомо не игра — клип уедет в «Desktop».</summary>
    public bool IsIgnored(string exe) => _ignored.Contains(exe);

    /// <summary>Только встроенные значения, без файлов. Ими же пользуются тесты.</summary>
    public static GameDatabase BuiltIn() => new(
        new Dictionary<string, string>(KnownGames, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(IgnoredPrograms, StringComparer.OrdinalIgnoreCase));

    /// <summary>Встроенные значения плюс оба файла games.json, если они есть.</summary>
    public static GameDatabase Load()
    {
        var database = BuiltIn();
        foreach (string path in FilePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                database = database.Apply(File.ReadAllText(path));
                Log.Info("GameDetect", $"Список игр дополнен из {path}");
            }
            catch (Exception ex)
            {
                // Испорченный файл не должен ломать определение игры — работаем дальше
                // на том, что уже накоплено.
                Log.Warn("GameDetect", $"Не удалось прочитать {path}: {ex.Message}");
            }
        }
        return database;
    }

    /// <summary>Где ищем файлы: сначала рядом с приложением, потом у пользователя.</summary>
    public static IEnumerable<string> FilePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "games.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Aura", "games.json");
    }

    /// <summary>
    /// Наложить отличия из JSON и вернуть НОВУЮ базу. Исходная не меняется: так
    /// детектор может подменить ссылку одним присваиванием, не ловя полусобранное
    /// состояние на чужом потоке.
    /// </summary>
    public GameDatabase Apply(string json)
    {
        var patch = JsonSerializer.Deserialize<Patch>(json, JsonOpts);
        var games = new Dictionary<string, string>(_games, StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(_ignored, StringComparer.OrdinalIgnoreCase);
        if (patch is null) return new GameDatabase(games, ignored);

        if (patch.Games is not null)
            foreach (var (exe, name) in patch.Games)
                if (!string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(name))
                    games[exe.Trim()] = name.Trim();

        if (patch.Ignored is not null)
            foreach (string exe in patch.Ignored)
                if (!string.IsNullOrWhiteSpace(exe)) ignored.Add(exe.Trim());

        // Снятие встроенного исключения: кто-то пишет летсплей про сам OBS
        if (patch.Allowed is not null)
            foreach (string exe in patch.Allowed)
                if (!string.IsNullOrWhiteSpace(exe)) ignored.Remove(exe.Trim());

        return new GameDatabase(games, ignored);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class Patch
    {
        [JsonPropertyName("games")] public Dictionary<string, string>? Games { get; set; }
        [JsonPropertyName("ignored")] public List<string>? Ignored { get; set; }
        [JsonPropertyName("allowed")] public List<string>? Allowed { get; set; }
    }

    // ---------------- Встроенные значения ----------------

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
    private static readonly HashSet<string> IgnoredPrograms = new(StringComparer.OrdinalIgnoreCase)
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
}
