using System.IO.Compression;
using InstantReplaySetup;
using Xunit;
namespace InstantReplay.Tests;
public sealed class InstallerPayloadVerificationTests
{
    private static MemoryStream Package(params string[] names)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (string name in names) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("test"); }
        stream.Position = 0;
        return stream;
    }
    [Theory]
    [InlineData("../Aura.exe")] [InlineData("/Aura.exe")] [InlineData("C:/Aura.exe")]
    [InlineData("x/../Aura.exe")] [InlineData("x\\..\\Aura.exe")]
    [InlineData("secret.pem")] [InlineData("Aura.pdb")] [InlineData("libvlc/win-x86/libvlc.dll")]
    public void Unsafe_or_unwanted_payloads_are_rejected(string name)
    {
        using var zip = Package(name);
        Assert.Throws<InvalidDataException>(() => InstallerPayloadVerification.Verify(zip, "2.0.65.0"));
    }
    [Fact] public void Missing_runtime_is_rejected()
    {
        using var zip = Package("Aura.exe");
        Assert.Contains("Aura.dll", Assert.Throws<InvalidDataException>(() => InstallerPayloadVerification.Verify(zip, "2.0.65.0")).Message);
    }
    [Fact] public void Case_ambiguous_entries_are_rejected()
    {
        using var zip = Package("Aura.exe", "aura.EXE");
        Assert.Contains("Повторный", Assert.Throws<InvalidDataException>(() => InstallerPayloadVerification.Verify(zip, "2.0.65.0")).Message);
    }
}
