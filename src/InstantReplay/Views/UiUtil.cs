using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InstantReplay.Views;

internal static class UiUtil
{
    /// <summary>Включает/выключает все Control-потомки (у панелей WinUI нет IsEnabled).</summary>
    public static void SetEnabledRecursive(UIElement root, bool enabled)
    {
        if (root is Control c)
        {
            c.IsEnabled = enabled;
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(root, i) is UIElement child)
                SetEnabledRecursive(child, enabled);
    }
}
