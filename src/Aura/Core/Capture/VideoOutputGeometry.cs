namespace Aura.Core.Capture;

internal readonly record struct VideoRect(int X, int Y, int Width, int Height);

/// <summary>Вписывает источник в фиксированный холст без изменения аспекта.</summary>
internal static class VideoOutputGeometry
{
    public static VideoRect Fit(
        int sourceWidth,
        int sourceHeight,
        int canvasWidth,
        int canvasHeight)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (canvasWidth < 2) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (canvasHeight < 2) throw new ArgumentOutOfRangeException(nameof(canvasHeight));

        int maximumWidth = canvasWidth & ~1;
        int maximumHeight = canvasHeight & ~1;
        int width;
        int height;

        if ((long)sourceWidth * maximumHeight <= (long)maximumWidth * sourceHeight)
        {
            height = maximumHeight;
            width = EvenAtLeastTwo(
                (int)Math.Round(
                    (double)sourceWidth * height / sourceHeight,
                    MidpointRounding.AwayFromZero),
                maximumWidth);
        }
        else
        {
            width = maximumWidth;
            height = EvenAtLeastTwo(
                (int)Math.Round(
                    (double)sourceHeight * width / sourceWidth,
                    MidpointRounding.AwayFromZero),
                maximumHeight);
        }

        return new VideoRect(
            (canvasWidth - width) / 2,
            (canvasHeight - height) / 2,
            width,
            height);
    }

    private static int EvenAtLeastTwo(int value, int maximum) =>
        Math.Clamp(value & ~1, 2, maximum);
}
