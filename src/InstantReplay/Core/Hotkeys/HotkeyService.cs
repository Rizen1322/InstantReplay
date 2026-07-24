using System.Runtime.InteropServices;
using InstantReplay.Core.Interop;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;
using static InstantReplay.Core.Interop.NativeMethods;

namespace InstantReplay.Core.Hotkeys;

public enum HotkeyAction { SaveReplay, SaveLast30, StartRecording, StopRecording, ToggleInstantReplay, Screenshot, OpenFolder }

/// <summary>
/// Глобальные горячие клавиши через низкоуровневый хук WH_KEYBOARD_LL.
/// В отличие от RegisterHotKey, хук стоит В НАЧАЛЕ цепочки обработки ввода —
/// комбинация ловится системно, ДО того как её увидит игра (в т.ч. fullscreen
/// с raw input). Хук живёт в собственном потоке со своей message loop, колбэк
/// отрабатывает за микросекунды (только сверка комбинации), поэтому задержек
/// ввода в играх не создаёт.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>
    /// Режим захвата новой комбинации в UI: хук временно пропускает всё.
    /// Задаётся с таймаутом-страховкой: если UI забудет снять флаг (ушли со страницы
    /// «Клавиши» с открытым полем захвата), хоткеи не должны умереть навсегда.
    /// </summary>
    private long _suspendUntilTicks;

    public bool Suspended
    {
        get => Environment.TickCount64 < Interlocked.Read(ref _suspendUntilTicks);
        set => Interlocked.Exchange(ref _suspendUntilTicks,
            value ? Environment.TickCount64 + 15_000 : 0); // максимум 15 сек на захват
    }

    private readonly SettingsManager _settings;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private HookProc? _hookProc; // держим делегат, чтобы GC не собрал

    // Скомпилированные комбинации: (vk, ctrl, shift, alt, win) → действие
    private readonly Dictionary<(uint vk, bool ctrl, bool shift, bool alt, bool win), HotkeyAction> _map = new();
    private readonly object _mapLock = new();

    public HotkeyService(SettingsManager settings)
    {
        _settings = settings;
        _settings.Changed += g => { if (g is "" or "hotkeys") RebuildMap(); };
        RebuildMap();
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "HotkeyHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadIdNative(); // нативный id нужен для PostThreadMessage

        _hookProc = HookCallback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error("Hotkeys", $"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        Log.Info("Hotkeys", "Глобальный хук клавиатуры установлен");

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Suspended && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = info.vkCode;

            // Модификаторы через GetAsyncKeyState — состояние на момент нажатия
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool win = ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0;

            HotkeyAction? action = null;
            lock (_mapLock)
                if (_map.TryGetValue((vk, ctrl, shift, alt, win), out var a)) action = a;

            if (action is not null)
            {
                // Обработку уводим из хука мгновенно — колбэк должен вернуться за <1 мс
                var act = action.Value;
                ThreadPool.QueueUserWorkItem(_ => HotkeyPressed?.Invoke(act));
                return new IntPtr(1); // комбинацию съедаем, в игру она не попадёт
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void RebuildMap()
    {
        var s = _settings.Current;
        lock (_mapLock)
        {
            _map.Clear();
            TryAdd(s.HotkeySaveReplay, HotkeyAction.SaveReplay);
            TryAdd(s.HotkeySaveLast30, HotkeyAction.SaveLast30);
            TryAdd(s.HotkeyStartRecording, HotkeyAction.StartRecording);
            TryAdd(s.HotkeyStopRecording, HotkeyAction.StopRecording);
            TryAdd(s.HotkeyToggleInstantReplay, HotkeyAction.ToggleInstantReplay);
            TryAdd(s.HotkeyScreenshot, HotkeyAction.Screenshot);
            TryAdd(s.HotkeyOpenFolder, HotkeyAction.OpenFolder);
        }
    }

    private void TryAdd(string combo, HotkeyAction action)
    {
        // Пустой бинд — действие намеренно без горячей клавиши, это не ошибка
        if (string.IsNullOrWhiteSpace(combo)) return;
        if (HotkeyParser.TryParse(combo, out var key))
            _map[key] = action;
        else
            Log.Warn("Hotkeys", $"Не удалось разобрать комбинацию '{combo}' для {action}");
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }
}

/// <summary>Парсер строк вида "Ctrl+Alt+F10", "Shift+S", "F9" в (vk + модификаторы).</summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, uint> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
    };

    public static bool TryParse(string combo, out (uint vk, bool ctrl, bool shift, bool alt, bool win) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        bool ctrl = false, shift = false, alt = false, win = false;
        uint vk = 0;

        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0]))
                        vk = char.ToUpperInvariant(raw[0]);
                    else if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out int f) && f is >= 1 and <= 24)
                        vk = (uint)(0x70 + f - 1); // VK_F1..F24
                    else if (Special.TryGetValue(raw, out var s))
                        vk = s;
                    else return false;
                    break;
            }
        }
        if (vk == 0) return false;
        result = (vk, ctrl, shift, alt, win);
        return true;
    }
}
