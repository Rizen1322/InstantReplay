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

    public virtual void OnShown() { }
    public virtual void OnHidden() { }
}
