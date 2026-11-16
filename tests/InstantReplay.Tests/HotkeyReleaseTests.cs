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
    public void Зажатая_push_to_talk_сбрасывается_по_физически_отпущенной_клавише()
    {
        var settings = new SettingsManager();
        using var hotkeys = new HotkeyService(settings);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(HotkeyService);

        // Бинд задаём штатным путём настроек (без записи settings.json на диск):
        // F24 в тестовой среде никто не держит, значит клавиша физически отпущена.
        settings.Current.HotkeyPushToTalk = "F24";
        settings.NotifyChanged("hotkeys");
        type.GetField("_pushToTalkHeld", flags)!.SetValue(hotkeys, true);

        var raised = new List<bool>();
        hotkeys.PushToTalkChanged += (held, _) => raised.Add(held);

        var reset = type.GetMethod("ResetStuckPushToTalk", flags)!;
        reset.Invoke(hotkeys, null);

        Assert.False((bool)type.GetField("_pushToTalkHeld", flags)!.GetValue(hotkeys)!);
        Assert.Equal(new[] { false }, raised);

        // Повторный проход ничего не поднимает: состояние уже сброшено — так же,
        // как если бы отпускание успел обработать сам хук.
        reset.Invoke(hotkeys, null);
        Assert.Single(raised);
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
