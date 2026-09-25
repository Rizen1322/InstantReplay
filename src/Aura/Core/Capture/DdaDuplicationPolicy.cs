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

    /// <summary>
    /// DuplicateOutput1 не подошёл, но старый DuplicateOutput может сработать.
    /// E_INVALIDARG сюда входит: на части сборок Windows 10 DuplicateOutput1
    /// отвечает им на вполне корректный вызов (процесс без per-monitor DPI v2 в
    /// этом потоке, выход на другом адаптере), и старый вызов при этом работает.
    /// </summary>
    public static bool ShouldFallBackToLegacy(int hresult) => hresult is
        unchecked((int)0x80004002) or
        unchecked((int)0x80004001) or
        unchecked((int)0x80070057) or // E_INVALIDARG
        unchecked((int)0x887A0004);
}
