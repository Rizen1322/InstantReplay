using Aura.Core.Settings;
using Aura.Core.SystemIntegration;
using Xunit;

namespace InstantReplay.Tests;

public sealed class SettingsRecoveryTests
{
    [Fact]
    public void Future_enum_keeps_field_default_and_other_settings()
    {
        var settings = SettingsManager.Deserialize("""{"Theme":"FutureTheme","ReplayLengthSeconds":75}""");
        Assert.Equal(new AppSettings().Theme, settings.Theme);
        Assert.Equal(75, settings.ReplayLengthSeconds);
    }

    [Fact]
    public void Broken_json_is_not_treated_as_future_settings() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SettingsManager.Deserialize("{invalid"));

    [Fact]
    public void Throwing_subscriber_does_not_block_other_subscribers()
    {
        var manager = new SettingsManager();
        bool notified = false;
        manager.Changed += _ => throw new IOException("unavailable folder");
        manager.Changed += _ => notified = true;
        // Exercise notification without writing any real settings.
        manager.NotifyChanged("system");
        Assert.True(notified);
    }

    [Fact]
    public void Task_command_is_xml_decoded_before_path_comparison()
    {
        const string path = @"C:\Игры & приложения\Aura\app\Aura.exe";
        string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><Exec><Command>"C:\Игры &amp; приложения\Aura\app\Aura.exe"</Command></Exec></Actions>
            </Task>
            """;
        Assert.True(StartupManager.TaskMatches(xml, path));
        Assert.False(StartupManager.TaskMatches(xml, @"C:\other\Aura.exe"));
    }
}
