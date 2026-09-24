namespace Aura.Core.Capture;

/// <summary>
/// Веса фильтра Ланцоша для уменьшения по одной оси (см. LanczosScaler). Отдельно
/// от графики, чтобы их проверяли тесты: сумма весов каждого окна обязана быть
/// равна единице, иначе яркость кадра поплывёт полосами.
/// </summary>
internal static class LanczosWeights
{
    /// <summary>
    /// Для каждой выходной позиции: первый исходный отсчёт окна и нормированные
    /// веса. Окно растягивается во столько раз, во сколько уменьшается кадр, —
    /// так фильтр усредняет все исходные пиксели, а не выхватывает часть.
    /// </summary>
    public static (int[] First, float[] Weights, int Taps) Compute(int source, int output, int a)
    {
        double scale = (double)source / output;
        double stretch = Math.Max(scale, 1);
        double radius = a * stretch;
        int taps = (int)Math.Ceiling(radius * 2) + 1;
        var first = new int[output];
        var weights = new float[output * taps];
        for (int o = 0; o < output; o++)
        {
            double center = (o + 0.5) * scale;
            int start = (int)Math.Floor(center - radius);
            first[o] = start;
            double sum = 0;
            for (int k = 0; k < taps; k++)
            {
                double w = Lanczos((start + k + 0.5 - center) / stretch, a);
                weights[o * taps + k] = (float)w;
                sum += w;
            }
            for (int k = 0; k < taps; k++) weights[o * taps + k] = (float)(weights[o * taps + k] / sum);
        }
        return (first, weights, taps);
    }

    private static double Lanczos(double x, int a)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= a) return 0;
        double px = Math.PI * x;
        return a * Math.Sin(px) * Math.Sin(px / a) / (px * px);
    }
}
