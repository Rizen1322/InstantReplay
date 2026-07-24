using Vortice.Direct3D11;
using Windows.Graphics.Imaging;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Capture;

/// <summary>
/// Скриншот монитора → PNG.
///
/// Два пути, и порядок важен:
/// 1. Если буфер записи уже работает — берём у него ПОСЛЕДНИЙ захваченный кадр.
///    На Windows 10 это единственный рабочий вариант: DXGI не даёт открыть вторую
///    дупликацию того же монитора, и своя сессия падала с
///    DuplicateOutput → E_INVALIDARG (скриншот не делался вовсе).
///    Побочно это ещё и мгновенно (кадр уже в видеопамяти) и работает на
///    статичном экране, где Desktop Duplication новых кадров не присылает.
/// 2. Буфер выключен — одноразовая сессия захвата, как раньше.
/// </summary>
public static class ScreenshotService
{
    public static async Task<string> CaptureAsync(int monitorIndex, string filePath, bool cursor,
        LiveFrameProvider? live = null)
    {
        (byte[] Bgra, int W, int H)? shot = null;

        // 1) Кадр у работающего буфера
        if (live is not null)
        {
            try
            {
                live((device, context, texture) => shot = ReadPixels(device, context, texture));
                if (shot is not null) Log.Info("Screenshot", "Кадр взят у работающего буфера");
            }
            catch (Exception ex)
            {
                Log.Warn("Screenshot", $"Не удалось взять кадр у буфера: {ex.Message}");
                shot = null;
            }
        }

        // 2) Буфер выключен — своя одноразовая сессия
        if (shot is null)
            shot = await CaptureOwnSessionAsync(monitorIndex, cursor);

        var (bgra, width, height) = shot.Value;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
        }

        Log.Info("Screenshot", $"Сохранён: {filePath} ({width}x{height})");
        return filePath;
    }

    /// <summary>Одноразовая сессия захвата: живёт ~200 мс, ждём первый кадр.</summary>
    private static async Task<(byte[] Bgra, int W, int H)> CaptureOwnSessionAsync(int monitorIndex, bool cursor)
    {
        var tcs = new TaskCompletionSource<(byte[] Bgra, int W, int H)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var source = ScreenCaptureFactory.Create();
        source.FrameArrived += (texture, _) =>
        {
            if (tcs.Task.IsCompleted) return;
            // Всё внутри колбэка: текстура валидна только здесь
            try { tcs.TrySetResult(ReadPixels(source.D3DDevice, source.D3DContext, texture)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };

        source.Start(monitorIndex, 0, cursor);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>Считать пиксели текстуры в RAM через staging-копию.</summary>
    public static (byte[] Bgra, int W, int H) ReadPixels(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        using var staging = device.CreateTexture2D(desc with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
        context.CopyResource(staging, texture);

        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            int w = (int)desc.Width, h = (int)desc.Height;
            var bytes = new byte[w * h * 4];
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int row = 0; row < h; row++)
                    new Span<byte>(src + row * mapped.RowPitch, w * 4)
                        .CopyTo(bytes.AsSpan(row * w * 4));
            }
            return (bytes, w, h);
        }
        finally { context.Unmap(staging, 0); }
    }
}
