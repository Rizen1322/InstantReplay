using System.Windows;
using System.Windows.Controls;

namespace Aura.Controls;

/// <summary>
/// Держит у содержимого заданное соотношение сторон: высота считается от
/// ширины. Нужен карточкам и кадрам, которые тянутся по ширине сетки, но должны
/// оставаться 16:9.
/// </summary>
public sealed class Aspect : Decorator
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(Aspect),
        new FrameworkPropertyMetadata(16.0 / 9, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Ширина, делённая на высоту.</summary>
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 320 : available.Width;
        double height = width / Math.Max(0.1, Ratio);
        if (!double.IsInfinity(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * Ratio;
        }
        var size = new Size(width, height);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(final));
        return final;
    }
}
