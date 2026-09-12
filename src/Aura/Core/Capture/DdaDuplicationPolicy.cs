namespace Aura.Core.Capture;

internal static class DdaDuplicationPolicy
{
    public static bool ShouldFallBackToLegacy(int hresult) => hresult is
        unchecked((int)0x80004002) or
        unchecked((int)0x80004001) or
        unchecked((int)0x887A0004);
}
