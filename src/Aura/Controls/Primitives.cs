using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aura.Controls;

/// <summary>
/// Векторная иконка. Все контуры нарисованы в квадрате 24×24 (Theme/Icons.xaml),
/// поэтому шаблон масштабирует их одинаково независимо от реальных границ фигуры.
/// </summary>
public sealed class Icon : Control
{
    static Icon() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(16d));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Icon), new PropertyMetadata(1.8));
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>Залитая иконка (play, логотип) вместо штриховой.</summary>
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(Icon), new PropertyMetadata(false));
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
}

/// <summary>Цветной скруглённый квадрат с иконкой внутри — как в разделах macOS.</summary>
public sealed class IconTile : Control
{
    static IconTile() => DefaultStyleKeyProperty.OverrideMetadata(typeof(IconTile), new FrameworkPropertyMetadata(typeof(IconTile)));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconTile), new PropertyMetadata(null));
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(double), typeof(IconTile), new PropertyMetadata(22d));
    public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconTile), new PropertyMetadata(13d));
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(IconTile), new PropertyMetadata(false));
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
}

/// <summary>
/// Переключатель в стиле iOS: пружинящая ручка, заливка акцентом.
///
/// Положение ручки ставится кодом, а не триггерами в шаблоне: раскладку меняли
/// и клик, и код (состояние движка), и раскадровки из EnterActions/ExitActions
/// иногда расходились с реальным IsChecked — тумблер показывал «включено» на
/// выключённом повторе.
/// </summary>
public sealed class Switch : ToggleButton
{
    static Switch() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Switch), new FrameworkPropertyMetadata(typeof(Switch)));

    public static readonly DependencyProperty LargeProperty = DependencyProperty.Register(
        nameof(Large), typeof(bool), typeof(Switch),
        new PropertyMetadata(false, (d, _) => ((Switch)d).Apply(animate: false)));
    public bool Large { get => (bool)GetValue(LargeProperty); set => SetValue(LargeProperty, value); }

    private TranslateTransform? _knob;
    private UIElement? _fill;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _knob = GetTemplateChild("KnobShift") as TranslateTransform;
        _fill = GetTemplateChild("On") as UIElement;
        Apply(animate: false);
    }

    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); Apply(animate: true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); Apply(animate: true); }

    private void Apply(bool animate)
    {
        if (_knob is null || _fill is null) return;

        bool on = IsChecked == true;
        double x = on ? (Large ? 21 : 18) : 0;
        double opacity = on ? 1 : 0;

        if (!animate)
        {
            // Снимаем анимации: иначе они держат старое значение поверх нового
            _knob.BeginAnimation(TranslateTransform.XProperty, null);
            _fill.BeginAnimation(UIElement.OpacityProperty, null);
            _knob.X = x;
            _fill.Opacity = opacity;
            return;
        }

        _knob.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromSeconds(0.34))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 } });
        _fill.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromSeconds(0.2)));
    }
}

/// <summary>
/// Сегментированный переключатель (Всё / Клипы / Скриншоты). Внутри обычный
/// ListBox, поэтому доступны SelectedIndex, привязки и клавиатура.
/// </summary>
public sealed class Segmented : ListBox
{
    static Segmented() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Segmented), new FrameworkPropertyMetadata(typeof(Segmented)));

    public Segmented()
    {
        SelectionMode = SelectionMode.Single;
        Focusable = false;
    }
}

/// <summary>Клавиши сочетания («Alt + F10») в виде клавиатурных колпачков.</summary>
public sealed class KeyCaps : ItemsControl
{
    static KeyCaps() => DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyCaps), new FrameworkPropertyMetadata(typeof(KeyCaps)));

    public static readonly DependencyProperty ComboProperty = DependencyProperty.Register(
        nameof(Combo), typeof(string), typeof(KeyCaps),
        new PropertyMetadata("", (d, e) => ((KeyCaps)d).Rebuild()));
    public string Combo { get => (string)GetValue(ComboProperty); set => SetValue(ComboProperty, value); }

    private void Rebuild()
    {
        var parts = (Combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries);
        var items = new List<string>();
        foreach (var p in parts)
        {
            string k = p.Trim();
            items.Add(k.Equals("Shift", StringComparison.OrdinalIgnoreCase) ? "⇧" : k);
        }
        ItemsSource = items;
    }
}

// ---------------- конвертеры ----------------

/// <summary>true → Visible, false → Collapsed. Inverted="True" переворачивает.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Inverted { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Inverted) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Умножение числа на параметр — для полос заполнения и раскладок.</summary>
public sealed class Multiply : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        double k = p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double kk) ? kk : 1;
        return v * k;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
