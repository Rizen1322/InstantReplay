using Vortice.Direct3D11;

namespace Aura.Core.Capture;

internal enum CaptureSurfaceScope
{
    Monitor,
    GameWindow
}

internal readonly record struct CapturedSurface(
    ID3D11Texture2D Texture,
    long Timestamp,
    long Generation,
    CaptureCursorUpdate Cursor,
    CaptureSurfaceScope Scope,
    long TargetRevision);
