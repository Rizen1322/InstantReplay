using System.Runtime.InteropServices;

namespace Aura.Core.Interop;

internal static partial class NativeMethods
{
    // ---------------- user32 ----------------
    [LibraryImport("user32.dll")] internal static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [LibraryImport("user32.dll")] internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [LibraryImport("user32.dll")] internal static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(IntPtr hhk);
    [LibraryImport("user32.dll")] internal static partial short GetAsyncKeyState(int vKey);
    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [LibraryImport("user32.dll")] internal static partial IntPtr DispatchMessageW(ref MSG lpMsg);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // Границы монитора и его масштаб: оверлею выделения области нужно лечь ровно на
    // тот монитор, с которого снят кадр. WPF работает в аппаратно-независимых
    // пикселях, DXGI отдаёт физические — без DPI монитора одно в другое не перевести.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    // POINT объявлена ниже, рядом с MINMAXINFO
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>MDT_EFFECTIVE_DPI = 0 — тот масштаб, который видит пользователь.</summary>
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetWindowLongW(IntPtr hWnd, int nIndex);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // Вывод окна на передний план, когда его просит показать вторая копия приложения:
    // Activate() у скрытого окна оставляет его позади активного (обычно позади игры).
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
    internal const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;

    // Кнопки мыши, которые можно назначать на действия. Левой и правой здесь нет
    // намеренно: перехватить их значило бы отобрать у человека мышь.
    internal const int WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;

    /// <summary>Какая боковая кнопка нажата — лежит в старшем слове mouseData.</summary>
    internal const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000,
                       WS_EX_TRANSPARENT = 0x00000020, WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    /// <summary>
    /// Смещение поля mouseData в MSLLHOOKSTRUCT: перед ним лежит POINT из двух int.
    /// Читаем поле напрямую, а не разбираем структуру целиком — низкоуровневый хук
    /// мыши вызывается на каждое движение, и объект в куче там непозволителен.
    /// </summary>
    internal const int MouseDataOffset = 8;

    // ---------------- субклассинг окна (минимальный размер через WM_GETMINMAXINFO) ----------------
    internal const int GWLP_WNDPROC = -4;
    internal const uint WM_GETMINMAXINFO = 0x0024;

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, WndProcDelegate newProc);
    [DllImport("user32.dll")]
    internal static extern IntPtr CallWindowProcW(IntPtr prevProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---------------- GPU-приоритет процесса (как «GPU priority» у OBS) ----------------
    // Под 100% загрузкой GPU игрой наши Blt/Copy/NVENC-команды иначе стоят в общей
    // очереди планировщика — кадры записи опаздывают и дропаются.
    internal const int D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4;

    [DllImport("gdi32.dll")]
    internal static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, int priorityClass);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    // ---------------- Фоновый режим потока (низкий приоритет ввода-вывода) ----------------
    // THREAD_MODE_BACKGROUND_BEGIN опускает потоку и процессорный приоритет, и приоритет
    // ДИСКОВЫХ операций. Именно это нужно потоку сохранения клипа, пока идёт игра:
    // сотни мегабайт уходят на диск «по остаточному принципу» и не отбирают ввод-вывод
    // у игры. Обычный ThreadPriority.BelowNormal на дисковую очередь не влияет.
    internal const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    internal const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    // ---------------- winmm (разрешение системного таймера) ----------------
    // Без timeBeginPeriod(1) Thread.Sleep квантуется по 15.6 мс — аудио-микшер и
    // конвейер записи получают рваный темп (так же делают OBS/ShadowPlay).
    [LibraryImport("winmm.dll")] internal static partial uint timeBeginPeriod(uint uPeriod);
    [LibraryImport("winmm.dll")] internal static partial uint timeEndPeriod(uint uPeriod);

    // ---------------- display (текущий режим экрана: разрешение + герцовка) ----------------
    internal const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    // ---------------- dwmapi ----------------
    // ГЛАВНЫЙ фикс белой рамки уведомления: DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    internal const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------------- combase (WinRT activation) ----------------
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string src, int len, out IntPtr hstring);
    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(IntPtr hstring);
    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    // ---------------- d3d11 (WinRT Direct3D interop) ----------------
    [LibraryImport("d3d11.dll")]
    internal static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ---------------- kernel32 (карта адресного пространства) ----------------
    /// <summary>Описание области адресного пространства (см. MEMORY_BASIC_INFORMATION).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemMapped = 0x40000;
    internal const uint MemImage = 0x1000000;

    [LibraryImport("kernel32.dll")]
    internal static partial nuint VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, nuint length);
}
