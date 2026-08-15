using Vortice.DXGI;

namespace Aura.Core.Capture;

public sealed record MonitorDescription(int Index, string Label, int Width, int Height, bool IsPrimary);

/// <summary>Список мониторов для комбобокса настроек — тот же порядок DXGI, что у захвата.</summary>
public static class MonitorEnumerator
{
    public static List<MonitorDescription> Enumerate()
    {
        var result = new List<MonitorDescription>();
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        using (output)
                        {
                            var d = output.Description;
                            int w = d.DesktopCoordinates.Right - d.DesktopCoordinates.Left;
                            int h = d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top;
                            bool primary = d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0;
                            int idx = result.Count;
                            result.Add(new MonitorDescription(
                                idx,
                                $"Монитор {idx + 1} ({w}×{h}){(primary ? " — основной" : "")}",
                                w, h, primary));
                        }
            }
        }
        catch (Exception ex)
        {
            // Список короче настоящего — это молча сдвинутые индексы в настройках:
            // пользователь выбирает «Монитор 2», а пишется совсем другой экран.
            Logging.Log.Warn("Capture",
                $"Перечисление мониторов оборвалось на {result.Count}-м: {ex.Message}");
        }
        if (result.Count == 0)
            result.Add(new MonitorDescription(0, "Монитор 1 — основной", 0, 0, true));
        return result;
    }
}
