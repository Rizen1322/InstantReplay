using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Aura.Core.Logging;
using Aura.Core.Tools;

namespace Aura.Views;

internal static class VideoFileDrop
{
    public static void Attach(Window window, Action<string[]> receive)
    {
        HwndSource? source = null;
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr handle, IntPtr unused, ref bool handled) =>
        {
            if (message != 0x233) return IntPtr.Zero;
            handled = true;
            try
            {
                uint count = DragQueryFile(handle, uint.MaxValue, null, 0);
                if (count > 128) throw new InvalidOperationException("Перетащите не больше 128 видео за один раз.");
                var paths = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFile(handle, i, null, 0);
                    if (length > 32767) throw new InvalidOperationException("Слишком длинный путь к видео.");
                    var path = new StringBuilder((int)length + 1);
                    DragQueryFile(handle, i, path, (uint)path.Capacity);
                    paths.Add(path.ToString());
                }
                window.Dispatcher.BeginInvoke(() => receive(paths.ToArray()));
            }
            catch (Exception ex) { Log.Warn("Editor", ex.Message); }
            finally { DragFinish(handle); }
            return IntPtr.Zero;
        };
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(hook);
            IntPtr hwnd = source?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            // Explorer is not elevated; accept only the shell's file-drop messages across UIPI.
            ChangeWindowMessageFilterEx(hwnd, 0x233, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, 0x49, 1, IntPtr.Zero);
            DragAcceptFiles(hwnd, true);
        };
        window.Closed += (_, _) => { if (source is { IsDisposed: false }) source.RemoveHook(hook); };
    }

    public static void Open(string[] paths)
    {
        if (paths.Length == 0) return;
        if (paths.Any(p => !File.Exists(p) || !VideoFiles.IsVideo(p)))
        { Dialogs.Say("Выберите видео", "Поддерживаются MP4, MKV, MOV, WebM, AVI, M4V, WMV и TS. Папки и изображения не добавляются."); return; }
        if (paths.Length == 1)
            ClipEditorWindow.ShowExternal(paths[0]);
        else VideoAssemblyWindow.ShowFiles(paths);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint capacity);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr filter);
}
