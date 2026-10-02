using System.IO.MemoryMappedFiles;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameHookSessionRestartTests
{
    [Fact]
    public void Restart_republishes_bootstrap_while_old_hook_still_holds_mapping()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = GameHookSessionNames.Create(Environment.ProcessId, Environment.ProcessId) with
        {
            BootstrapMapping = $"Local\\Aura.Tests.{Guid.NewGuid():N}.Bootstrap"
        };
        using var controller = first.OpenBootstrapMapping();
        using var oldHook = MemoryMappedFile.OpenExisting(first.BootstrapMapping, MemoryMappedFileRights.Read);
        using var oldView = oldHook.CreateViewAccessor(0, 256, MemoryMappedFileAccess.Read);
        controller.Dispose(); // Exactly the native hook's remaining handle after Aura stops.

        var next = GameHookSessionNames.Create(Environment.ProcessId, Environment.ProcessId) with
        {
            BootstrapMapping = first.BootstrapMapping
        };
        using var restart = next.OpenBootstrapMapping();
        using var writer = restart.CreateViewAccessor(0, 256, MemoryMappedFileAccess.ReadWrite);
        byte[] nonce = System.Text.Encoding.ASCII.GetBytes(next.Nonce);
        writer.WriteArray(40, nonce, 0, 32);
        byte[] discoveredNonce = new byte[32];
        oldView.ReadArray(40, discoveredNonce, 0, 32);
        Assert.Equal(nonce, discoveredNonce);
        Assert.NotEqual(first.FrameMapping, next.FrameMapping);
        Assert.NotEqual(first.FrameReadyEvent, next.FrameReadyEvent);
        Assert.NotEqual(first.ControlEvent, next.ControlEvent);
    }
}
