using System.Windows;
using System.Windows.Controls;

namespace Aura.Controls;

/// <summary>
/// Строка настройки: слева подпись (и, если есть, иконка), справа управление.
///
/// Когда окно сужается и в строку всё перестаёт помещаться, управление уезжает
/// под подпись вместо того, чтобы обрезаться. Раньше именно так и пропадали
/// сегменты и выпадающие списки на узком окне.
///
/// Порядок детей: [иконка] [подпись] [управление] — управление всегда последнее.
/// </summary>
public sealed class AdaptiveRow : Panel
{
    /// <summary>Сколько места должно остаться подписи, иначе строка складывается.</summary>
    public static readonly DependencyProperty MinTextWidthProperty = DependencyProperty.Register(
        nameof(MinTextWidth), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(150d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinTextWidth { get => (double)GetValue(MinTextWidthProperty); set => SetValue(MinTextWidthProperty, value); }

    /// <summary>Отступ между подписью и управлением.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private bool _stacked;

    protected override Size MeasureOverride(Size available)
    {
        int count = InternalChildren.Count;
        if (count == 0) return new Size(0, 0);

        var control = InternalChildren[count - 1];
        double width = double.IsInfinity(available.Width) ? 1000 : available.Width;

        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double controlWidth = control.DesiredSize.Width;

        // Ведущие элементы, кроме подписи (обычно цветная плитка с иконкой)
        double leadingWidth = 0;
        for (int i = 0; i < count - 2; i++)
        {
            InternalChildren[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            leadingWidth += InternalChildren[i].DesiredSize.Width + Gap;
        }

        double forText = width - leadingWidth - controlWidth - Gap;
        _stacked = count > 1 && forText < MinTextWidth;

        if (count == 1)
        {
            control.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, control.DesiredSize.Height);
        }

        var text = InternalChildren[count - 2];
        if (_stacked)
        {
            text.Measure(new Size(Math.Max(0, width - leadingWidth), double.PositiveInfinity));
            control.Measure(new Size(width, double.PositiveInfinity));
            double top = Math.Max(text.DesiredSize.Height, LeadingHeight(count));
            return new Size(width, top + Gap / 2 + control.DesiredSize.Height);
        }

        text.Measure(new Size(Math.Max(0, forText), double.PositiveInfinity));
        double height = Math.Max(Math.Max(text.DesiredSize.Height, control.DesiredSize.Height), LeadingHeight(count));
        return new Size(width, height);
    }

    private double LeadingHeight(int count)
    {
        double height = 0;
        for (int i = 0; i < count - 2; i++)
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        return height;
    }

    protected override Size ArrangeOverride(Size final)
    {
        int count = InternalChildren.Count;
        if (count == 0) return final;

        var control = InternalChildren[count - 1];
        if (count == 1)
        {
            control.Arrange(new Rect(0, 0, final.Width, control.DesiredSize.Height));
            return final;
        }

        var text = InternalChildren[count - 2];

        // Ведущие иконки — слева, по центру верхней строки
        double x = 0;
        double leadHeight = LeadingHeight(count);
        double topRowHeight = _stacked ? Math.Max(text.DesiredSize.Height, leadHeight) : final.Height;
        for (int i = 0; i < count - 2; i++)
        {
            var lead = InternalChildren[i];
            double h = lead.DesiredSize.Height;
            lead.Arrange(new Rect(x, (topRowHeight - h) / 2, lead.DesiredSize.Width, h));
            x += lead.DesiredSize.Width + Gap;
        }

        if (_stacked)
        {
            text.Arrange(new Rect(x, 0, Math.Max(0, final.Width - x), text.DesiredSize.Height));
            double top = Math.Max(text.DesiredSize.Height, leadHeight) + Gap / 2;
            control.Arrange(new Rect(x, top, Math.Max(0, final.Width - x), control.DesiredSize.Height));
        }
        else
        {
            double controlWidth = Math.Min(control.DesiredSize.Width, Math.Max(0, final.Width - x));
            double textWidth = Math.Max(0, final.Width - x - controlWidth - Gap);
            text.Arrange(new Rect(x, (final.Height - text.DesiredSize.Height) / 2, textWidth, text.DesiredSize.Height));
            control.Arrange(new Rect(final.Width - controlWidth,
                                     (final.Height - control.DesiredSize.Height) / 2,
                                     controlWidth, control.DesiredSize.Height));
        }
        return final;
    }
}
