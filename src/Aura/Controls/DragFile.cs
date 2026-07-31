using System.Windows;
using System.Windows.Input;

namespace Aura.Controls;

/// <summary>
/// Перетаскивание файла мышью прямо из окна: зажал карточку, потянул — и клип
/// уехал в Discord, браузер или папку.
///
/// Присоединяемое свойство, а не код в карточке: шаблон карточки живёт в словаре
/// ресурсов, у него нет code-behind.
/// </summary>
public static class DragFile
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(DragFile), new PropertyMetadata(null, OnPathChanged));

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);
    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);

    private static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(Point?), typeof(DragFile), new PropertyMetadata(null));

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        // Подписки ставим один раз: свойство переустанавливается при переиспользовании
        // контейнеров в списке, а обработчики должны остаться в одном экземпляре.
        element.PreviewMouseLeftButtonDown -= OnDown;
        element.PreviewMouseMove -= OnMove;
        element.PreviewMouseLeftButtonUp -= OnUp;

        if (e.NewValue is not string path || path.Length == 0) return;
        element.PreviewMouseLeftButtonDown += OnDown;
        element.PreviewMouseMove += OnMove;
        element.PreviewMouseLeftButtonUp += OnUp;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e) =>
        ((DependencyObject)sender).SetValue(StartProperty, e.GetPosition(null));

    private static void OnUp(object sender, MouseButtonEventArgs e) =>
        ((DependencyObject)sender).SetValue(StartProperty, null);

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (element.GetValue(StartProperty) is not Point start) return;

        // Порог системы: иначе обычный клик по карточке превращался бы в перетаскивание
        Vector moved = e.GetPosition(null) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        string? path = GetPath(element);
        element.SetValue(StartProperty, null);
        if (path is null || !File.Exists(path)) return;

        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            // Заодно текстом: часть окон принимает путь строкой, а не файлом
            data.SetData(DataFormats.Text, path);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch { /* перетаскивание — приятное дополнение, падать из-за него незачем */ }
    }
}
