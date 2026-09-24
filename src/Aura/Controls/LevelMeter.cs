using System.Windows;
using System.Windows.Media;

namespace Aura.Controls;

/// <summary>
/// Шкала уровня звука: дорожка, заливка по уровню (зелёная, к пику жёлтая и
/// красная) и необязательная белая метка порога гейта.
///
/// Уровень задаётся в децибелах: пиковое значение 0..1 пересчитывается в дБ,
/// а шкала показывает диапазон −60…0 дБ. В линейной шкале тихий голос занимал
/// бы пару пикселей, и по ней нельзя было бы подобрать порог гейта.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public const double FloorDb = -60;

    public static readonly DependencyProperty LevelDbProperty = DependencyProperty.Register(
        nameof(LevelDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));
    public double LevelDb { get => (double)GetValue(LevelDbProperty); set => SetValue(LevelDbProperty, value); }

    /// <summary>Порог гейта в дБ; NaN — метки нет.</summary>
    public static readonly DependencyProperty MarkDbProperty = DependencyProperty.Register(
        nameof(MarkDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public double MarkDb { get => (double)GetValue(MarkDbProperty); set => SetValue(MarkDbProperty, value); }

    public LevelMeter()
    {
        Height = 6;
        SnapsToDevicePixels = true;
    }

    /// <summary>Пик 0..1 → дБ, не ниже нижней границы шкалы.</summary>
    public static double ToDb(double peak) =>
        peak <= 0.000_001 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(peak));

    private static double Part(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = h / 2;

        var track = TryFindResource("TrackBrush") as Brush ?? Brushes.DimGray;
        var fill = TryFindResource("LevelBrush") as Brush ?? Brushes.LimeGreen;
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        double filled = w * Part(LevelDb);
        if (filled > 0.5)
        {
            // Градиент лежит на всю ширину и обрезается по уровню: цвет зависит
            // от места на шкале, а не от длины заливки.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, filled, h), r, r));
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, w, h), r, r);
            dc.Pop();
        }

        if (!double.IsNaN(MarkDb))
        {
            double x = Math.Round(w * Part(MarkDb));
            var mark = TryFindResource("TxBrush") as Brush ?? Brushes.White;
            dc.PushOpacity(0.8);
            dc.DrawRectangle(mark, null, new Rect(x - 1, -3, 2, h + 6));
            dc.Pop();
        }
    }
}
