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
/// Общий код для оверлея выделения и меню карточки: расходиться этим двум местам
/// нельзя, иначе «копировать» будет работать по-разному в зависимости от того, откуда
/// нажали.
/// </summary>
public static class ImageClipboard
{
    /// <summary>
    /// Кладёт картинку в буфер. Вызывать из потока интерфейса: буфер обмена
    /// принадлежит окну, и класть в него из фонового потока нельзя. Кодирование PNG
    /// здесь же — оно на снимке области занимает единицы миллисекунд, а вот запись
    /// файла из этого метода намеренно вынесена наружу, в фон.
    /// </summary>
    public static bool Copy(BitmapSource image)
    {
        try
        {
            var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();

            var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(png);

            var data = new DataObject();
            data.SetImage(opaque);                        // CF_BITMAP и CF_DIB
            data.SetData("PNG", png, autoConvert: false); // то, что берут браузеры и мессенджеры

            for (int attempt = 1; attempt <= 6; attempt++)
            {
                try
                {
                    // copy: true — данные остаются в буфере после закрытия окна
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("Clipboard", $"Буфер занят (попытка {attempt}): {ex.Message}");
                    Thread.Sleep(70);
                }
            }
        }
        catch (Exception ex) { Log.Warn("Clipboard", $"Копирование не удалось: {ex.Message}"); }
        return false;
    }

    /// <summary>Загрузить файл и положить в буфер. Файл открытым не держим — его можно будет удалить.</summary>
    public static bool CopyFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return Copy(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard", $"Не удалось прочитать {path}: {ex.Message}");
            return false;
        }
    }
}
