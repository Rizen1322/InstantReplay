using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aura.Core.Logging;

namespace Aura.Views;

/// <summary>
/// Картинка в буфер обмена.
///
/// Одного Clipboard.SetImage оказалось мало — он «проходил» без ошибки, а вставлять
/// было нечего:
/// • WPF кладёт единственный формат Bitmap, а браузеры, мессенджеры и графические
///   редакторы читают из буфера PNG или DIB — кладём все три сразу;
/// • альфа-канал в DIB часть программ понимает как полную прозрачность, поэтому
///   картинка переводится в формат без альфы;
/// • буфер — общий ресурс: пока его держит открытым другой процесс, запись падает
///   с CLIPBRD_E_CANT_OPEN, и единственное лекарство — повторить.
///
/// Повторы ждут асинхронно: раньше здесь стоял Thread.Sleep(70) до шести раз прямо
/// в потоке интерфейса, и на занятом буфере окно замирало примерно на полсекунды.
///
/// Общий код для оверлея выделения и меню карточки: расходиться этим двум местам
/// нельзя, иначе «копировать» будет работать по-разному в зависимости от того, откуда
/// нажали.
/// </summary>
public static class ImageClipboard
{
    /// <summary>Сколько раз пробовать запись, пока буфер занят другой программой.</summary>
    private const int Attempts = 6;

    /// <summary>Пауза между попытками: занятый буфер обычно освобождается за десятки мс.</summary>
    private const int RetryDelayMs = 70;

    /// <summary>
    /// Кладёт картинку в буфер. Вызывать с потока интерфейса: буфер обмена принадлежит
    /// окну (STA), и после каждого await продолжение обязано возвращаться на этот же
    /// поток — поэтому здесь нельзя ConfigureAwait(false). Асинхронность нужна из-за
    /// повторов: между попытками поток интерфейса свободен, окно не замирает.
    /// </summary>
    public static async Task<bool> CopyAsync(BitmapSource image)
    {
        try
        {
            // Кадр замораживаем до ухода с потока интерфейса: кодирование уходит в фон,
            // а незамороженный BitmapSource чужому потоку недоступен.
            if (image.CanFreeze && !image.IsFrozen) image.Freeze();

            // Всё, что нужно буферу, готовим заранее и один раз: PNG 4K-снимка
            // кодируется не мгновенно, повторять это на каждую попытку незачем.
            var (opaque, png) = image.IsFrozen
                ? await Task.Run(() => Prepare(image))
                : Prepare(image);

            var data = new DataObject();
            data.SetImage(opaque);                        // CF_BITMAP и CF_DIB
            data.SetData("PNG", png, autoConvert: false); // то, что берут браузеры и мессенджеры

            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    // copy: true — данные остаются в буфере после закрытия окна.
                    // В WPF у SetDataObject нет параметров повторов (они были только
                    // в WinForms), поэтому повторы с паузой — наш цикл, а Thread.Sleep
                    // здесь больше нет: поток интерфейса свободен.
                    // Вызов идёт на потоке интерфейса: await вернул нас сюда с потока
                    // интерфейса, потому что ConfigureAwait(false) здесь нет.
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("Clipboard", $"Буфер занят (попытка {attempt}): {ex.Message}");
                    if (attempt < Attempts) await Task.Delay(RetryDelayMs);
                }
            }
        }
        catch (Exception ex) { Log.Warn("Clipboard", $"Копирование не удалось: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Загрузить файл и положить в буфер. Файл открытым не держим — его можно будет
    /// удалить. Чтение и декодирование идут в фоне: на большом снимке это заметная
    /// пауза, а поток интерфейса должен оставаться отзывчивым.
    /// </summary>
    public static async Task<bool> CopyFileAsync(string path)
    {
        BitmapSource bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                // OnLoad закрывает файл сразу после декодирования, Freeze делает кадр
                // пригодным для буфера и для возврата на поток интерфейса.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return (BitmapSource)image;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard", $"Не удалось прочитать {path}: {ex.Message}");
            return false;
        }

        // await без ConfigureAwait(false): продолжение вернётся на поток интерфейса,
        // и Clipboard внутри CopyAsync будет вызван там, где положено.
        return await CopyAsync(bitmap);
    }

    /// <summary>
    /// Данные для буфера из готового кадра: непрозрачная копия для CF_BITMAP/CF_DIB
    /// и PNG-поток для браузеров и мессенджеров. Оба объекта заморожены — буфер
    /// обмена может пережить закрытие окна, и данные должны быть читаемы извне.
    /// Уже замороженный кадр можно готовить в фоновом потоке.
    /// </summary>
    private static (BitmapSource Opaque, MemoryStream Png) Prepare(BitmapSource image)
    {
        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();

        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);

        return (opaque, png);
    }
}
