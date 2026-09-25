using System.Windows;
using System.Windows.Controls;

namespace Aura.Views;

/// <summary>
/// Общий предок страниц: заголовок для шапки окна, кнопки, которые страница
/// добавляет в шапку, и уведомления о появлении/уходе (чтобы не крутить
/// таймеры на невидимой странице).
/// </summary>
public abstract class PageBase : UserControl
{
    public abstract string Title { get; }

    /// <summary>Кнопки, которые окно покажет в шапке рядом с кнопками окна.</summary>
    public virtual UIElement[] ToolbarActions => [];

    /// <summary>
    /// Страница занимает окно целиком и прокручивает свой список сама (страницы
    /// настроек: список слева прокручивается, панель справа стоит на месте).
    /// Иначе прокручивается вся страница, как раньше.
    /// </summary>
    public virtual bool FillsWindow => false;

    /// <summary>
    /// Панель справа уходит, когда окно узкое: список настроек важнее подсказок.
    /// </summary>
    protected void HideAsideWhenNarrow(ColumnDefinition column, FrameworkElement aside, double width = 300)
    {
        SizeChanged += (_, _) =>
        {
            bool wide = ActualWidth >= 820;
            column.Width = new GridLength(wide ? width : 0);
            aside.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    /// <summary>
    /// Панель «Совет» в правую колонку. Совет меняется при каждом заходе в раздел.
    /// </summary>
    protected void AddTips(Panel aside, string topic)
    {
        var panel = Tips.Panel(topic, out var refresh);
        aside.Children.Add(panel);
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) refresh(); };
    }

    public virtual void OnShown() { }
    public virtual void OnHidden() { }
}
