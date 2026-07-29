using InstantReplay.Core.Hotkeys;
using InstantReplay.Core.Settings;
using Xunit;

namespace InstantReplay.Tests;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("Alt+F10", 0x79u, false, false, true, false)]
    [InlineData("ctrl + shift + s", (uint)'S', true, true, false, false)]
    [InlineData("F1", 0x70u, false, false, false, false)]
    [InlineData("Win+PrintScreen", 0x2Cu, false, false, false, true)]
    public void РазбираетСочетание(string combo, uint vk, bool ctrl, bool shift, bool alt, bool win)
    {
        Assert.True(HotkeyParser.TryParse(combo, out var key));
        Assert.Equal((vk, ctrl, shift, alt, win), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]          // только модификатор — не сочетание
    [InlineData("Alt+Shift")]
    [InlineData("Alt+Абра")]
    [InlineData("F25")]           // за пределами F1..F24
    public void ОтклоняетМусор(string combo) =>
        Assert.False(HotkeyParser.TryParse(combo, out _));

    [Fact]
    public void НормализацияНеЗависитОтПорядкаИРегистра() =>
        Assert.Equal(HotkeyParser.Normalize("shift+ALT+f10"), HotkeyParser.Normalize("Alt + Shift + F10"));

    [Fact]
    public void НормализацияПустаДляМусора() => Assert.Equal("", HotkeyParser.Normalize("Alt+Shift"));

    [Theory]
    [InlineData(0x79u, "F10")]
    [InlineData((uint)'A', "A")]
    [InlineData(0x20u, "Space")]
    public void ИмяКлавишиПоКоду(uint vk, string expected) => Assert.Equal(expected, HotkeyParser.KeyName(vk));
}

public class HotkeyConflictsTests
{
    [Fact]
    public void ПоУмолчаниюКонфликтовНет()
    {
        var settings = new AppSettings();
        Assert.Empty(HotkeyConflicts.FindDuplicates(HotkeyConflicts.Bindings(settings)));
    }

    [Fact]
    public void НаходитДубльДажеПриРазномПорядкеМодификаторов()
    {
        var settings = new AppSettings
        {
            HotkeySaveReplay = "Alt+F10",
            HotkeyScreenshot = "F10+Alt" // то же сочетание, записанное иначе
        };

        var duplicates = HotkeyConflicts.FindDuplicates(HotkeyConflicts.Bindings(settings));

        Assert.Contains(HotkeyAction.SaveReplay, duplicates.Keys);
        Assert.Contains(HotkeyAction.Screenshot, duplicates.Keys);
    }

    [Fact]
    public void ПустыеБиндыНеСчитаютсяДублями()
    {
        var settings = new AppSettings { HotkeySaveReplay = "", HotkeyScreenshot = "", HotkeyOpenFolder = "" };
        Assert.Empty(HotkeyConflicts.FindDuplicates(HotkeyConflicts.Bindings(settings)));
    }

    [Fact]
    public void НаходитЗанявшегоСочетание()
    {
        var settings = new AppSettings { HotkeySaveReplay = "Alt+F10" };

        var occupant = HotkeyConflicts.Occupant(settings, HotkeyAction.Screenshot, "alt+f10");

        Assert.NotNull(occupant);
        Assert.Equal(HotkeyAction.SaveReplay, occupant!.Action);
    }

    [Fact]
    public void СвоёЖеСочетаниеНеСчитаетсяЗанятым() =>
        Assert.Null(HotkeyConflicts.Occupant(new AppSettings(), HotkeyAction.SaveReplay, "Alt+F10"));

    [Theory]
    [InlineData("S")]        // буква без модификатора — хук съест её во всех программах
    [InlineData("Enter")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+C")]
    [InlineData("Win+G")]
    public void ПредупреждаетОбОпасныхСочетаниях(string combo) => Assert.NotNull(HotkeyConflicts.Risk(combo));

    [Theory]
    [InlineData("Alt+F10")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("PrintScreen")]
    [InlineData("")]
    public void НормальныеСочетанияБезПредупреждений(string combo) => Assert.Null(HotkeyConflicts.Risk(combo));
}
