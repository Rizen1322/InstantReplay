using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Кто и на каком адаптере работает, и сколько видеопамяти занято.
///
/// ЗАЧЕМ. Разбор жалобы «у всех нормально, у меня лагает» упирался в то, что по
/// логу нельзя было понять две вещи: на том ли адаптере идёт захват и не кончилась
/// ли видеопамять. На системе с двумя графиками (дискретная плюс встроенная)
/// захват с ЧУЖОГО адаптера гонит каждый кадр через шину, и копия текстуры,
/// обычно занимающая доли миллисекунды, начинает застревать на секунды.
/// Обе цифры теперь видны в логе сразу.
/// </summary>
internal static class GpuInfo
{
    internal readonly record struct Adapter(string Name, long Luid)
    {
        public override string ToString() => Name;
    }

    /// <summary>Адаптер, на котором создано устройство.</summary>
    public static Adapter? Of(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return Describe(adapter);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Адаптер устройства не определяется: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Прочитать описание адаптера. Отдельным методом, потому что у разных версий
    /// DXGI набор доступных полей отличается: если полное описание не читается,
    /// довольствуемся идентификатором — он для сверки адаптеров и нужен.
    /// </summary>
    private static Adapter Describe(IDXGIAdapter adapter)
    {
        // Только имя и идентификатор. Объём видеопамяти отсюда НЕ берём: поле
        // DedicatedVideoMemory приходит указательного размера и на этой системе
        // валило чтение всего описания с «Arithmetic operation resulted in an
        // overflow», из-за чего в логе стояло «адаптер неизвестен». Занятость и
        // бюджет всё равно точнее читаются через QueryVideoMemoryInfo.
        var d = adapter.Description;
        return new Adapter(d.Description.Trim(), d.Luid);
    }

    /// <summary>Адаптер, к которому подключён монитор с этим индексом.</summary>
    public static Adapter? ForMonitor(int monitorIndex)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int index = 0;
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                using (adapter)
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        using (output)
                            if (index++ == monitorIndex) return Describe(adapter);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Адаптер монитора #{monitorIndex} не определяется: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Адаптер, к которому подключён монитор — для создания устройства ИМЕННО на нём.
    /// Вызывающий обязан освободить результат. null — монитор не найден.
    /// </summary>
    public static IDXGIAdapter1? OpenAdapterForMonitor(int monitorIndex)
    {
        // Собираем «выход → его адаптер» в порядке перечисления DXGI — том же, в
        // котором монитор выбирается в настройках и в GetMonitorHandle. Индекс за
        // пределами списка КЛАМПИТСЯ так же, как там: иначе захват шёл бы по
        // клампнутому монитору, а устройство создавалось на адаптере по умолчанию —
        // ровно тот кросс-адаптерный случай, ради которого всё это и делалось.
        var perOutput = new List<IDXGIAdapter1>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                int outputs = 0;
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    output.Dispose();
                    outputs++;
                }

                // По одной ссылке на каждый выход: владение простое и очевидное
                for (int i = 0; i < outputs; i++)
                    perOutput.Add(i == 0 ? adapter : adapter.QueryInterface<IDXGIAdapter1>());

                if (outputs == 0) adapter.Dispose();
            }

            if (perOutput.Count == 0) return null;

            int index = Math.Clamp(monitorIndex, 0, perOutput.Count - 1);
            var chosen = perOutput[index];
            perOutput.RemoveAt(index);
            return chosen;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Адаптер монитора #{monitorIndex} не определяется: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var spare in perOutput) spare.Dispose();
        }
    }

    /// <summary>
    /// Занято/бюджет видеопамяти в мегабайтах. Бюджет назначает Windows, и он
    /// меньше физического объёма: под нагрузкой система его ужимает. Превышение
    /// бюджета означает вытеснение текстур в оперативную память через шину —
    /// именно тогда всё, что трогает GPU, начинает застревать.
    /// </summary>
    public static (long UsedMb, long BudgetMb)? Usage(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
            var info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return ((long)info.CurrentUsage / (1024 * 1024), (long)info.Budget / (1024 * 1024));
        }
        catch { return null; }
    }
}
