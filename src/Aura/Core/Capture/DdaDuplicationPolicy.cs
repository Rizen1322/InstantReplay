namespace Aura.Core.Capture;

internal static class DdaDuplicationPolicy
{
    /// <summary>
    /// Эти результаты означают, что текущая frame-сессия больше не пригодна.
    /// Повтор AcquireNextFrame на том же объекте зациклится, поэтому нужна новая
    /// IDXGIOutputDuplication.
    /// </summary>
    public static bool ShouldRecreateFrameSession(int hresult) => hresult is
        unchecked((int)0x887A0001) or // DXGI_ERROR_INVALID_CALL
        unchecked((int)0x887A0026);   // DXGI_ERROR_ACCESS_LOST

    public static bool ShouldFallBackToLegacy(int hresult) => hresult is
        unchecked((int)0x80004002) or
        unchecked((int)0x80004001) or
        unchecked((int)0x887A0004);
}
