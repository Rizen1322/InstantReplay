namespace InstantReplaySetup;

internal sealed class InstallerAudioState
{
    private const double QuietVolume = 0.12;

    public bool IsMuted { get; private set; }
    public double Volume => IsMuted ? 0 : QuietVolume;

    public void ToggleMute() => IsMuted = !IsMuted;
}
