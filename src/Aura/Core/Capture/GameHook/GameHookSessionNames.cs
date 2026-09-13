using System.Security.Cryptography;

namespace Aura.Core.Capture.GameHook;

internal readonly record struct GameHookSessionNames(
    string Nonce,
    string BootstrapMapping,
    string FrameMapping,
    string FrameReadyEvent,
    string ControlEvent)
{
    public static GameHookSessionNames Create(int targetPid, int controllerPid)
    {
        if (targetPid <= 0) throw new ArgumentOutOfRangeException(nameof(targetPid));
        if (controllerPid <= 0) throw new ArgumentOutOfRangeException(nameof(controllerPid));

        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string prefix = $"Local\\Aura.GameCapture.{controllerPid}.{targetPid}";
        return new GameHookSessionNames(
            nonce,
            $"{prefix}.Bootstrap",
            $"{prefix}.{nonce}.Frames",
            $"{prefix}.{nonce}.FrameReady",
            $"{prefix}.{nonce}.Control");
    }
}
