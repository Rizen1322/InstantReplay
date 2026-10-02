using System.Security.Cryptography;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Aura.Core.Capture.GameHook;

internal readonly record struct GameHookSessionNames(
    string Nonce,
    string BootstrapMapping,
    string FrameMapping,
    string FrameReadyEvent,
    string ControlEvent)
{
    [SupportedOSPlatform("windows")]
    // Игра может ещё держать bootstrap прошлого поколения. Только этот маленький
    // discovery-блок переоткрывается; Frames и события всегда имеют новый nonce.
    public MemoryMappedFile OpenBootstrapMapping() => MemoryMappedFile.CreateOrOpen(
        BootstrapMapping, GameHookProtocol.BootstrapHeaderSize, MemoryMappedFileAccess.ReadWrite);

    public static GameHookSessionNames Create(int targetPid, int controllerPid)
    {
        if (targetPid <= 0) throw new ArgumentOutOfRangeException(nameof(targetPid));
        if (controllerPid <= 0) throw new ArgumentOutOfRangeException(nameof(controllerPid));

        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string prefix = $"Local\\Aura.GameCapture.{controllerPid}.{targetPid}";
        return new GameHookSessionNames(
            nonce,
            $"Local\\Aura.GameCapture.{targetPid}.Bootstrap",
            $"{prefix}.{nonce}.Frames",
            $"{prefix}.{nonce}.FrameReady",
            $"{prefix}.{nonce}.Control");
    }
}
