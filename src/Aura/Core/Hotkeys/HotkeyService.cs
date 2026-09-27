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

    // WH_KEYBOARD_LL присылает WM_KEYDOWN снова и снова, пока клавишу держат.
    // Для действия нужен только переход «отпущена → нажата»; отдельно запоминаем
    // съеденные клавиши, чтобы не отдавать игре их повторы и одинокий KEYUP.
    private readonly HashSet<uint> _keysDown = [];

    /// <summary>
    /// Push-to-talk: клавиша, пока зажата, открывает микрофон. Отдельно от _map:
    /// действия съедают нажатие, а эта клавиша обязана дойти до Discord и игры.
    /// </summary>
    private (uint vk, bool ctrl, bool shift, bool alt, bool win)? _pushToTalk;
    private bool _pushToTalkHeld;

    /// <summary>Push-to-talk зажата или отпущена; второй аргумент — время в тиках QPC (100 нс).</summary>
    public event Action<bool, long>? PushToTalkChanged;

    private static long NowTicks() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                                             (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>Нажатие или отпускание кнопки: не наша ли это push-to-talk.</summary>
    private void TrackPushToTalk(uint vk, bool down)
    {
        if (_pushToTalk is not { } key || key.vk != vk) return;
        if (down)
        {
            if (_pushToTalkHeld) return;
            // Модификаторы из сочетания обязаны быть зажаты, лишние не мешают:
            // в игре с зажатым Shift говорить тоже надо
            if (key.ctrl && (GetAsyncKeyState(0x11) & 0x8000) == 0) return;
            if (key.shift && (GetAsyncKeyState(0x10) & 0x8000) == 0) return;
            if (key.alt && (GetAsyncKeyState(0x12) & 0x8000) == 0) return;
            if (key.win && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) == 0) return;
            _pushToTalkHeld = true;
        }
        else
        {
            if (!_pushToTalkHeld) return;
            _pushToTalkHeld = false;
        }
        // Прямо из хука, а не через пул потоков: там нажатие и отпускание могли
        // выполниться в обратном порядке, и микрофон остался бы открытым. Обработчик
        // только кладёт отметку в список под замком, это микросекунды.
        try { PushToTalkChanged?.Invoke(_pushToTalkHeld, NowTicks()); }
        catch (Exception ex) { Log.Warn("Hotkeys", $"Push-to-talk: {ex.Message}"); }
    }
    private readonly HashSet<uint> _consumedKeys = [];

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

        // Хук мыши живёт на ТОМ ЖЕ потоке и обслуживается тем же циклом сообщений.
        // Ставим его ТОЛЬКО когда на кнопку мыши назначено действие: низкоуровневый
        // хук мыши получает каждое её движение (игровые мыши — до 8000 событий в
        // секунду), и каждое проходит через управляемый колбэк. Пауза сборщика мусора
        // в этот момент задерживает курсор во всей системе, то есть в игре. Без
        // назначенных кнопок платить за это незачем.
        UpdateMouseHook();

        Log.Info("Hotkeys", "Глобальный хук клавиатуры установлен" +
                            (_mouseHook != IntPtr.Zero ? " (и мыши)" : ""));

        _lastKeyboardEvent = Environment.TickCount64;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (_threadId != 0) PostThreadMessageW(_threadId, WmCheckHook, IntPtr.Zero, IntPtr.Zero);
        }, null, 5000, 5000);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmUpdateMouseHook) { UpdateMouseHook(); continue; }
            if (msg.message == WmCheckHook) { CheckHookAlive(); continue; }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();

    /// <summary>Своё сообщение потоку хука: проверить, не снят ли хук системой.</summary>
    private const uint WmCheckHook = 0x8000 + 0x52; // WM_APP + 0x52

    /// <summary>Когда хук клавиатуры последний раз получал событие (TickCount64).</summary>
    private long _lastKeyboardEvent;
    private long _lastReinstall;

    /// <summary>
    /// Windows молча снимает низкоуровневый хук, если его обработчик не уложился
    /// в LowLevelHooksTimeout: так бывает под тяжёлой игрой, после сна или
    /// блокировки. Узнать об этом нельзя, горячие клавиши просто перестают
    /// работать до перезапуска Aura. Признак: человек что-то вводил (GetLastInputInfo
    /// новее), а наш хук давно ничего не видел. Тогда ставим хук заново: сначала
    /// новый, потом снимаем старый, чтобы не остаться без хука ни на миг.
    /// Ввод бывает и одной мышью, поэтому переустановка не чаще раза в минуту.
    /// </summary>
    private void CheckHookAlive()
    {
        long now = Environment.TickCount64;
        long lastKeyboard = Interlocked.Read(ref _lastKeyboardEvent);
        if (now - lastKeyboard < 60_000 || now - _lastReinstall < 60_000) return;

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return;
        // Тики GetLastInputInfo 32-битные: сравниваем возраст ввода, а не значения
        long inputAge = (uint)Environment.TickCount - info.Time;
        if (inputAge >= 5000) return;               // человек сейчас ничего не вводит

        _lastReinstall = now;
        IntPtr fresh = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc!, IntPtr.Zero, 0);
        if (fresh == IntPtr.Zero)
        {
            Log.Warn("Hotkeys", $"Переустановка хука клавиатуры не удалась: {Marshal.GetLastWin32Error()}");
            return;
        }
        IntPtr old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            UpdateMouseHook();
        }
        _keysDown.Clear();
        _consumedKeys.Clear();
        if (Interlocked.Increment(ref _reinstalls) is 1 or 10 or 100)
            Log.Info("Hotkeys", $"Хук клавиатуры переустановлен: давно не видел ввода ({_reinstalls}-й раз)");
    }

    private long _reinstalls;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Своё сообщение потоку хука: пересмотреть, нужен ли хук мыши.</summary>
    private const uint WmUpdateMouseHook = 0x8000 + 0x51; // WM_APP + 0x51

    /// <summary>Есть ли среди назначенных комбинаций кнопка мыши.</summary>
    private volatile bool _mouseBound;

    /// <summary>
    /// Поставить или снять хук мыши по текущим назначениям. Зовётся ТОЛЬКО на потоке
    /// хука: хук принадлежит потоку, который его поставил, и обслуживается его циклом
    /// сообщений.
    /// </summary>
    private void UpdateMouseHook()
    {
        if (_mouseBound && _mouseHook == IntPtr.Zero)
        {
            _mouseHookProc ??= MouseHookCallback;
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseHookProc, IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero)
                Log.Warn("Hotkeys", $"Хук мыши не установлен ({Marshal.GetLastWin32Error()}) — " +
                                    "кнопки мыши работать не будут");
            else if (_hook != IntPtr.Zero)
                Log.Info("Hotkeys", "Хук мыши установлен: на кнопку мыши назначено действие");
        }
        else if (!_mouseBound && _mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            Log.Info("Hotkeys", "Хук мыши снят: кнопки мыши не назначены");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Читаем ТОЛЬКО vkCode — он лежит первым полем KBDLLHOOKSTRUCT.
            // Разбор всей структуры через PtrToStructure давал объект в куче на
            // каждое нажатие клавиши, а этот колбэк обязан возвращаться за <1 мс.
            uint vk = (uint)Marshal.ReadInt32(lParam);
            _lastKeyboardEvent = Environment.TickCount64;

            if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
            {
                TrackPushToTalk(vk, down: false);
                _keysDown.Remove(vk);
                if (_consumedKeys.Remove(vk)) return new IntPtr(1);
            }
            else if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
            {
                // Уже нажата — это автоповтор Windows. Если первый DOWN был нашим,
                // повтор тоже съедаем, но действие второй раз не запускаем.
                if (!_keysDown.Add(vk))
                    return _consumedKeys.Contains(vk)
                        ? new IntPtr(1)
                        : CallNextHookEx(_hook, nCode, wParam, lParam);
                TrackPushToTalk(vk, down: true);

                if (!Suspended && TryFire(vk))
                {
                    _consumedKeys.Add(vk);
                    return new IntPtr(1); // комбинацию съедаем, в игру она не попадёт
                }
            }
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
        if (nCode >= 0)
        {
            // Push-to-talk на кнопке мыши: и нажатие, и отпускание, дальше по цепочке
            int button = (int)wParam;
            if (button is WM_MBUTTONDOWN or WM_MBUTTONUP or WM_XBUTTONDOWN or WM_XBUTTONUP && _pushToTalk is not null)
            {
                uint pttVk = 0x04;
                if (button is WM_XBUTTONDOWN or WM_XBUTTONUP)
                {
                    uint data = (uint)Marshal.ReadInt32(lParam, MouseDataOffset) >> 16;
                    pttVk = data == XBUTTON1 ? 0x05u : data == XBUTTON2 ? 0x06u : 0u;
                }
                TrackPushToTalk(pttVk, down: button is WM_MBUTTONDOWN or WM_XBUTTONDOWN);
            }
        }
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
            _pushToTalk = !string.IsNullOrWhiteSpace(s.HotkeyPushToTalk) &&
                          HotkeyParser.TryParse(s.HotkeyPushToTalk, out var ptt) ? ptt : null;
            // Сочетание сменили, пока клавиша зажата: микрофон не должен остаться открытым
            if (_pushToTalkHeld)
            {
                _pushToTalkHeld = false;
                PushToTalkChanged?.Invoke(false, NowTicks());
            }
            _mouseBound = _map.Keys.Any(k => HotkeyParser.IsMouseButton(k.vk)) ||
                          _pushToTalk is { } p && HotkeyParser.IsMouseButton(p.vk);
        }
        // Поток хука мог ещё не стартовать — тогда он сам спросит при запуске
        if (_threadId != 0) PostThreadMessageW(_threadId, WmUpdateMouseHook, IntPtr.Zero, IntPtr.Zero);
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
