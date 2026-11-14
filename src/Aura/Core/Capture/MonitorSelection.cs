namespace Aura.Core.Capture;

internal static class MonitorSelection
{
    internal static int Resolve(IReadOnlyList<string> identities, int previousIndex, string? identity)
    {
        if (!string.IsNullOrEmpty(identity))
        {
            for (int i = 0; i < identities.Count; i++)
                if (string.Equals(identities[i], identity, StringComparison.OrdinalIgnoreCase)) return i;
        }
        else if ((uint)previousIndex < (uint)identities.Count) return previousIndex;
        throw new InvalidOperationException("Выбранный монитор отключён. Выберите экран в настройках захвата.");
    }
}
