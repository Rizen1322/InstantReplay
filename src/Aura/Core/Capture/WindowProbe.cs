using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>Прямоугольник в пикселях снимка экрана.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Прямоугольники видимых окон в координатах снятого монитора.
///
/// Список собирается ОДИН РАЗ при открытии оверлея: оверлей уже накрыл экран
/// собственным окном, картинка под ним застыла, и пересобирать перечисление на
/// каждое движение мыши незачем — только дёргать систему в горячем пути.
///
/// Порядок перечисления EnumWindows — это Z-порядок сверху вниз, и он здесь
/// сохраняется: окно, лежащее выше, должно выигрывать у лежащего под ним.
/// </summary>
internal static class WindowProbe
{
    /// <summary>
    /// Окна, попавшие на монитор, в координатах снимка (0,0 — левый верхний угол
    /// монитора), сверху вниз по Z-порядку.
    /// </summary>
    public static List<PixelRect> Collect(int monitorX, int monitorY, int width, int height)
    {
        var result = new List<PixelRect>();
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!IsCandidate(hwnd)) return true;
                if (!TryGetBounds(hwnd, out var r)) return true;

                // В координаты снимка и обрезаем по монитору: окно может свисать
                // за край или лежать на соседнем экране целиком
                int left = Math.Max(r.Left - monitorX, 0);
                int top = Math.Max(r.Top - monitorY, 0);
                int right = Math.Min(r.Right - monitorX, width);
                int bottom = Math.Min(r.Bottom - monitorY, height);

                if (right - left >= MinSize && bottom - top >= MinSize)
                    result.Add(new PixelRect(left, top, right - left, bottom - top));

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Без списка окон снап просто станет работать только по картинке —
            // ронять из-за этого оверлей нельзя
            Log.Warn("Capture", $"Не удалось перечислить окна для снапа: {ex.Message}");
        }

        return result;
    }

    private const int MinSize = 16;

    /// <summary>Годится ли окно в кандидаты: видимое, не свёрнутое, не служебное.</summary>
    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;

        int ex = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        // Панели инструментов и сквозные для мыши слои — не то, что человек метит снять
        if ((ex & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT)) != 0) return false;

        // Скрытые окна UWP остаются «видимыми» для user32: их выдаёт только DWM,
        // иначе список забивают невидимые призраки вроде ApplicationFrameHost
        if (NativeMethods.DwmGetWindowAttributeInt(
                hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return true;
    }

    /// <summary>
    /// Настоящие видимые границы. GetWindowRect включает невидимые поля тени —
    /// рамка выделения по нему оказывается заметно больше самого окна.
    /// </summary>
    private static bool TryGetBounds(IntPtr hwnd, out NativeMethods.RECT rect)
    {
        rect = default;
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());

        return hr == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }
}
