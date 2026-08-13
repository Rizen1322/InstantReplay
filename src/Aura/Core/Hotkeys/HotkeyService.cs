using System.Runtime.InteropServices;
using Aura.Core.Interop;
using Aura.Core.Logging;
using Aura.Core.Settings;
using static Aura.Core.Interop.NativeMethods;

namespace Aura.Core.Hotkeys;

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
    private IntPtr _mouseHook;
    private HookProc? _hookProc;      // держим делегаты, чтобы GC их не собрал
    private HookProc? _mouseHookProc;

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

        // Хук мыши живёт на ТОМ ЖЕ потоке и обслуживается тем же циклом сообщений:
        // ставить под него отдельный поток незачем, а лишний поток с очередью — это
        // ещё одно место, где можно потерять сообщение при выключении.
        _mouseHookProc = MouseHookCallback;
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseHookProc, IntPtr.Zero, 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Warn("Hotkeys", $"Хук мыши не установлен ({Marshal.GetLastWin32Error()}) — " +
                                "кнопки мыши назначить не получится");

        Log.Info("Hotkeys", "Глобальный хук клавиатуры установлен" +
                            (_mouseHook != IntPtr.Zero ? " (и мыши)" : ""));

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Suspended && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            // Читаем ТОЛЬКО vkCode — он лежит первым полем KBDLLHOOKSTRUCT.
            // Разбор всей структуры через PtrToStructure давал объект в куче на
            // каждое нажатие клавиши, а этот колбэк обязан возвращаться за <1 мс.
            uint vk = (uint)Marshal.ReadInt32(lParam);

            if (TryFire(vk)) return new IntPtr(1); // комбинацию съедаем, в игру она не попадёт
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Низкоуровневый хук мыши.
    ///
    /// ВАЖНО ПРО СКОРОСТЬ: сюда прилетает КАЖДОЕ движение мыши, а в игре это сотни
    /// событий в секунду. Поэтому первым делом — грубый отбор по типу сообщения, и
    /// только для нажатий средней и боковых кнопок мы вообще что-то читаем из памяти
    /// хука. Всё остальное уходит дальше по цепочке немедленно.
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Suspended)
        {
            int message = (int)wParam;
            if (message is WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                uint vk = 0x04; // VK_MBUTTON
                if (message == WM_XBUTTONDOWN)
                {
                    // Какая боковая — в старшем слове mouseData
                    uint mouseData = (uint)Marshal.ReadInt32(lParam, MouseDataOffset);
                    uint button = mouseData >> 16;
                    vk = button == XBUTTON1 ? 0x05u : button == XBUTTON2 ? 0x06u : 0u;
                }
                if (vk != 0 && TryFire(vk)) return new IntPtr(1);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Найти действие по коду и модификаторам и запустить его. true — сочетание наше,
    /// событие дальше не пойдёт.
    ///
    /// Модификаторы читаются здесь для обоих хуков одинаково: Alt+боковая кнопка
    /// должно работать так же, как Alt+F10.
    /// </summary>
    private bool TryFire(uint vk)
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0;

        HotkeyAction action;
        lock (_mapLock)
            if (!_map.TryGetValue((vk, ctrl, shift, alt, win), out action)) return false;

        // Обработку уводим из хука мгновенно — колбэк должен вернуться за <1 мс
        ThreadPool.QueueUserWorkItem(_ => HotkeyPressed?.Invoke(action));
        return true;
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
            TryAdd(s.HotkeyScreenshotRegion, HotkeyAction.ScreenshotRegion);
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

// HotkeyParser вынесен в отдельный файл (Core/Hotkeys/HotkeyParser.cs): им пользуются
// ещё и проверка конфликтов, и тесты, которым WinUI-часть проекта не нужна.
