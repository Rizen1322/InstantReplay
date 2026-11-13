using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Aura.Core.SystemIntegration;
using Aura.Core.Tools;
using Xunit;

namespace InstantReplay.Tests;

public sealed class AppSecurityTests
{
    [Fact]
    public void Executables_in_user_writable_folders_are_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Path.GetTempFileName();
        try { Assert.False(ExecutableTrust.IsProtected(file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Network_paths_are_not_trusted_as_privileged_executables()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(ExecutableTrust.IsProtected(@"\\nonexistent.invalid\share\ffmpeg.exe"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Quit_signal_grants_access_only_to_administrators_and_system()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var signal = AppSignals.CreateEvent("Aura.Test." + Guid.NewGuid().ToString("N"));
        var rules = signal.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<EventWaitHandleAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, rule => Assert.Contains(rule.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544" }));
    }
}
