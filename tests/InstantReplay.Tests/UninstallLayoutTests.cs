using AuraUninstall;
using Xunit;

namespace InstantReplay.Tests;

public sealed class UninstallLayoutTests
{
    [Fact]
    public void Resolves_only_the_parent_of_the_app_directory()
    {
        string executable = Path.Combine("C:\\Users\\Test\\AppData\\Local\\Programs\\Aura", "app", "AuraUninstall.exe");

        Assert.True(UninstallLayout.TryResolveInstallRoot(executable, out string root));
        Assert.Equal(
            Path.GetFullPath("C:\\Users\\Test\\AppData\\Local\\Programs\\Aura"),
            root);
    }

    [Theory]
    [InlineData("C:\\Users\\Test\\Downloads\\AuraUninstall.exe")]
    [InlineData("C:\\Aura\\bin\\AuraUninstall.exe")]
    [InlineData("C:\\app\\AuraUninstall.exe")]
    public void Rejects_locations_outside_the_app_subdirectory(string executable)
    {
        Assert.False(UninstallLayout.TryResolveInstallRoot(executable, out _));
    }
}
