namespace Aura.Core.Capture;

/// <summary>Подготовка данных формы курсора DXGI без зависимости от D3D.</summary>
internal static class CursorShapePixels
{
    /// <summary>
    /// У DXGI позиция указателя уже означает левый верхний угол bitmap формы.
    /// Hotspot описывает саму форму, но повторно вычитать его из позиции нельзя.
    /// </summary>
    public static (int X, int Y) GetDrawOrigin(
        int pointerX, int pointerY, int hotspotX, int hotspotY) => (pointerX, pointerY);

    /// <summary>
    /// Маскированный цветной курсор уже приходит в нужной упаковке:
    /// BGR = цвет для XOR, alpha = AND-маска. Значение маски нормализуем к 0/255.
    /// </summary>
    public static byte[] ExpandMaskedColor(byte[] buffer, int width, int height, int pitch)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int row = y * pitch;
            for (int x = 0; x < width; x++)
            {
                int source = row + x * 4;
                if (source + 3 >= buffer.Length) continue;

                int p = (y * width + x) * 4;
                pixels[p + 0] = buffer[source + 0];
                pixels[p + 1] = buffer[source + 1];
                pixels[p + 2] = buffer[source + 2];
                pixels[p + 3] = buffer[source + 3] == 0 ? (byte)0 : (byte)255;
            }
        }
        return pixels;
    }

    /// <summary>
    /// У монохромной формы идут две 1-bpp маски: AND, затем XOR. Для шейдеров
    /// раскладываем XOR во все каналы BGR, AND — в alpha.
    /// </summary>
    public static byte[] ExpandMonochrome(byte[] buffer, int width, int height, int pitch)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int andRow = y * pitch;
            int xorRow = (y + height) * pitch;
            for (int x = 0; x < width; x++)
            {
                int mask = 0x80 >> (x & 7);
                int index = x >> 3;

                bool andBit = andRow + index < buffer.Length && (buffer[andRow + index] & mask) != 0;
                bool xorBit = xorRow + index < buffer.Length && (buffer[xorRow + index] & mask) != 0;

                int p = (y * width + x) * 4;
                byte xor = xorBit ? (byte)255 : (byte)0;
                pixels[p + 0] = xor;
                pixels[p + 1] = xor;
                pixels[p + 2] = xor;
                pixels[p + 3] = andBit ? (byte)255 : (byte)0;
            }
        }
        return pixels;
    }
}
