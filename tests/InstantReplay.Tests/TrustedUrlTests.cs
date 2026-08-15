using Aura.Core.SystemIntegration;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Ссылка на драйвер приходит из ответа gfwsl.geforce.com и уходит в
/// Process.Start с UseShellExecute у процесса с правами администратора.
/// Значит «просто строка из JSON» обязана пройти те же ворота, что и
/// ссылка на обновление.
/// </summary>
public class TrustedUrlTests
{
    [Fact]
    public void NvidiaDriver_AcceptsOfficialDownloadHost()
    {
        Assert.True(TrustedUrl.IsNvidiaDriver("https://us.download.nvidia.com/Windows/566.36/566.36-desktop-win10-win11-64bit-international-dch-whql.exe"));
    }

    [Fact]
    public void NvidiaDriver_AcceptsApexSubdomain()
    {
        Assert.True(TrustedUrl.IsNvidiaDriver("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php"));
    }

    [Fact]
    public void NvidiaDriver_RejectsPlainHttp()
    {
        Assert.False(TrustedUrl.IsNvidiaDriver("http://us.download.nvidia.com/driver.exe"));
    }

    [Fact]
    public void NvidiaDriver_RejectsUncPath()
    {
        // ShellExecute отработал бы \\host\share\x.exe как запуск с сетевой шары
        Assert.False(TrustedUrl.IsNvidiaDriver(@"\\attacker\share\payload.exe"));
    }

    [Fact]
    public void NvidiaDriver_RejectsLocalFilePath()
    {
        Assert.False(TrustedUrl.IsNvidiaDriver(@"C:\Windows\System32\cmd.exe"));
        Assert.False(TrustedUrl.IsNvidiaDriver("file:///C:/Windows/System32/cmd.exe"));
    }

    [Fact]
    public void NvidiaDriver_RejectsProtocolHandler()
    {
        Assert.False(TrustedUrl.IsNvidiaDriver("ms-settings:windowsupdate"));
        Assert.False(TrustedUrl.IsNvidiaDriver("javascript:alert(1)"));
    }

    [Theory]
    // Хост обязан быть nvidia.com или его поддоменом — не «похожим на»
    [InlineData("https://nvidia.com.evil.io/driver.exe")]
    [InlineData("https://evil-nvidia.com/driver.exe")]
    [InlineData("https://notnvidia.com/driver.exe")]
    [InlineData("https://nvidia.com@evil.io/driver.exe")]
    public void NvidiaDriver_RejectsLookalikeHosts(string url)
    {
        Assert.False(TrustedUrl.IsNvidiaDriver(url));
    }

    [Fact]
    public void NvidiaDriver_AcceptsBareDomain()
    {
        Assert.True(TrustedUrl.IsNvidiaDriver("https://nvidia.com/driver.exe"));
    }

    [Fact]
    public void NvidiaDriver_RejectsNullAndEmpty()
    {
        Assert.False(TrustedUrl.IsNvidiaDriver(null));
        Assert.False(TrustedUrl.IsNvidiaDriver(""));
        Assert.False(TrustedUrl.IsNvidiaDriver("   "));
    }
}
