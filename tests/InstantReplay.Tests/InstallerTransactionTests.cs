using InstantReplaySetup;
using Xunit;

namespace InstantReplay.Tests;

public sealed class InstallerTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aura-install-test-" + Guid.NewGuid().ToString("N"));

    public InstallerTransactionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        File.WriteAllText(Path.Combine(_root, ".aura-install-root"), "test");
        File.WriteAllText(Path.Combine(_root, "app", "Aura.exe"), "old");
    }

    private static void Payload(InstallTransaction transaction)
    {
        File.WriteAllText(Path.Combine(transaction.StagingDirectory, "Aura.exe"), "new");
        File.WriteAllText(Path.Combine(transaction.StagingDirectory, "AuraUninstall.exe"), "uninstall");
    }

    [Fact]
    public void Failed_registration_restores_previous_binaries()
    {
        using (var transaction = new InstallTransaction(_root))
        {
            Payload(transaction);
            transaction.Install();
            Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "app", "Aura.exe")));
        }
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "app", "Aura.exe")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Incomplete_payload_does_not_touch_previous_version()
    {
        using (var transaction = new InstallTransaction(_root))
            Assert.Throws<InvalidDataException>(transaction.Install);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "app", "Aura.exe")));
    }

    [Fact]
    public void Successful_install_keeps_new_version()
    {
        using (var transaction = new InstallTransaction(_root))
        {
            Payload(transaction);
            transaction.Install();
            transaction.Complete();
        }
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "app", "Aura.exe")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Update_requires_marker_and_application()
    {
        InstallTransaction.ValidateUpdateRoot(_root);
        File.Delete(Path.Combine(_root, ".aura-install-root"));
        Assert.Throws<InvalidOperationException>(() => InstallTransaction.ValidateUpdateRoot(_root));
    }

    [Fact]
    public void Installation_does_not_harden_an_unrelated_nonempty_directory()
    {
        InstallTransaction.ValidateTargetRoot(_root);
        File.Delete(Path.Combine(_root, ".aura-install-root"));
        Assert.Throws<InvalidOperationException>(() => InstallTransaction.ValidateTargetRoot(_root));
    }

    [Fact]
    public void Existing_marker_is_not_truncated_during_update()
    {
        string marker = Path.Combine(_root, ".aura-install-root");
        InstallTransaction.EnsureMarker(_root);
        Assert.Equal("test", File.ReadAllText(marker));
    }

    [Fact]
    public void Fresh_install_creates_a_marker()
    {
        string marker = Path.Combine(_root, ".aura-install-root");
        File.Delete(marker);
        InstallTransaction.EnsureMarker(_root);
        Assert.Contains("Aura installation root", File.ReadAllText(marker));
    }

    [Fact]
    public void Locked_previous_binary_leaves_either_complete_new_or_intact_old_version()
    {
        bool committed = false;
        using (var held = new FileStream(Path.Combine(_root, "app", "Aura.exe"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var transaction = new InstallTransaction(_root))
            {
                Payload(transaction);
                try { transaction.Install(); transaction.Complete(); committed = true; }
                catch (IOException) { }
            }
            Assert.Equal(committed ? "new" : "old", File.ReadAllText(Path.Combine(_root, "app", "Aura.exe")));
            Assert.Equal(committed, File.Exists(Path.Combine(_root, "app", "AuraUninstall.exe")));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
