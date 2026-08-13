using System.Text.RegularExpressions;
using Vortice.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

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
        var (bgra, width, height) = await CapturePixelsAsync(monitorIndex, cursor, live);
        await SavePngAsync(bgra, width, height, filePath);
        return filePath;
    }

    /// <summary>
    /// Пиксели экрана без записи на диск — этим пользуется оверлей выделения области:
    /// ему нужна картинка на экране, а файл появится только если пользователь решит
    /// сохранить. Порядок источников тот же, что и у обычного скриншота.
    /// </summary>
    public static async Task<(byte[] Bgra, int W, int H)> CapturePixelsAsync(
        int monitorIndex, bool cursor, LiveFrameProvider? live = null, byte[]? into = null)
    {
        (byte[] Bgra, int W, int H)? shot = null;

        // 1) Кадр у работающего буфера.
        //
        // Обязательно В ФОНЕ, и вот почему. Внутри — ожидание ближайшего кадра
        // (до полусекунды на статичном экране), копия в видеопамяти и вычитывание
        // 14 МБ через staging-текстуру. Всё это синхронное, а метод зовут в том
        // числе с потока интерфейса: оверлей выделения области открывался прямо
        // из обработчика горячей клавиши, и нажатие PrintScreen подвешивало окно.
        // Обычный скриншот от этого был защищён своим Task.Run в App — лишняя
        // обёртка там ничего не стоит, а здесь чинит корень.
        if (live is not null)
        {
            shot = await Task.Run(() =>
            {
                (byte[] Bgra, int W, int H)? frame = null;
                try
                {
                    live((device, context, texture) => frame = ReadPixels(device, context, texture, into));
                    if (frame is not null) Log.Info("Screenshot", "Кадр взят у работающего буфера");
                }
                catch (Exception ex)
                {
                    Log.Warn("Screenshot", $"Не удалось взять кадр у буфера: {ex.Message}");
                    frame = null;
                }
                return frame;
            }).ConfigureAwait(false);
        }

        // 2) Буфер выключен — своя одноразовая сессия
        shot ??= await CaptureOwnSessionAsync(monitorIndex, cursor);
        return shot.Value;
    }

    /// <summary>
    /// Следующее свободное имя вида Screenshot_1.png, Screenshot_2.png и так далее.
    ///
    /// Папка перечитывается при КАЖДОМ сохранении, а счётчик нигде не хранится:
    /// снимки удаляют, переименовывают и копируют руками, и сохранённый счётчик рано
    /// или поздно указал бы на занятое имя — то есть затёр бы чужой файл. Берём
    /// максимальный существующий номер и прибавляем единицу; заодно проверяем каждое
    /// имя на существование, потому что между перечислением и записью файл может
    /// появиться (второй снимок из трея, например).
    /// </summary>
    public static string NextFilePath(string folder, string prefix = "Screenshot", string extension = ".png")
    {
        Directory.CreateDirectory(folder);

        int max = 0;
        var pattern = new Regex($@"^{Regex.Escape(prefix)}_(\d+)$", RegexOptions.IgnoreCase);
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, $"{prefix}_*{extension}"))
            {
                var match = pattern.Match(Path.GetFileNameWithoutExtension(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int number) && number > max)
                    max = number;
            }
        }
        catch (Exception ex) { Log.Warn("Screenshot", $"Чтение папки {folder}: {ex.Message}"); }

        int next = max + 1;
        string path = Path.Combine(folder, $"{prefix}_{next}{extension}");
        while (File.Exists(path))
            path = Path.Combine(folder, $"{prefix}_{++next}{extension}");
        return path;
    }

    /// <summary>BGRA → PNG на диск.</summary>
    public static async Task SavePngAsync(byte[] bgra, int width, int height, string filePath)
    {
        // Кодируем в память, а на диск пишем готовые байты одним куском.
        // Раньше энкодер писал прямо в FileStream через AsRandomAccessStream: обёртка
        // живёт своей жизнью, и порядок закрытия «сначала FileStream, потом обёртка»
        // мог оставить файл недописанным. Плюс сжатие 2560×1440 — это заметные
        // сотни миллисекунд, и делать их в UI-потоке нельзя.
        var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();

        memory.Seek(0);
        var bytes = new byte[memory.Size];
        using (var reader = memory.AsStreamForRead())
        using (var buffer = new MemoryStream())
        {
            await reader.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }
        memory.Dispose();

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, bytes);

        Log.Info("Screenshot", $"Сохранён: {filePath} ({width}x{height}, {bytes.Length / 1024} КБ)");
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

    /// <summary>
    /// Считать пиксели текстуры в RAM через staging-копию.
    ///
    /// <paramref name="into"/> — готовый буфер, если он есть и достаточного размера.
    /// Кадр 2560×1440 это 14 МБ, и выделять их заново на каждый снимок незачем:
    /// оверлей выделения держит свой буфер между вызовами.
    /// </summary>
    public static (byte[] Bgra, int W, int H) ReadPixels(
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, byte[]? into = null)
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
            int size = w * h * 4;
            var bytes = into is not null && into.Length >= size ? into : new byte[size];
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
