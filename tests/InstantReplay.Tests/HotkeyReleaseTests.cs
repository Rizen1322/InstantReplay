using System.Reflection;
using System.Runtime.InteropServices;
using Aura.Core.Hotkeys;
using Aura.Core.Settings;
using Xunit;

namespace InstantReplay.Tests;

public sealed class HotkeyReleaseTests
{
    [Fact]
    public void Consumed_mouse_button_release_does_not_leak_to_the_game()
    {
        using var hotkeys = new HotkeyService(new SettingsManager());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var consumed = (HashSet<uint>)typeof(HotkeyService).GetField("_consumedKeys", flags)!.GetValue(hotkeys)!;
        consumed.Add(5);
        IntPtr data = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(data, 8, 1 << 16); // XBUTTON1
            var result = typeof(HotkeyService).GetMethod("MouseHookCallback", flags)!
                .Invoke(hotkeys, [0, new IntPtr(0x020C), data]); // WM_XBUTTONUP
            Assert.Equal(new IntPtr(1), result);
            Assert.Empty(consumed);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [Fact]
    public void Disposed_hotkeys_do_not_dispatch_new_actions()
    {
        var hotkeys = new HotkeyService(new SettingsManager());
        hotkeys.Dispose();
        var result = typeof(HotkeyService).GetMethod("TryFire", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hotkeys, [0x79u]);
        Assert.Equal(false, result);
    }
}
