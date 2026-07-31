using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aura.Views;

/// <summary>
/// Заглушка на случай, если раздел не собрался. Лучше честное сообщение с
/// текстом ошибки, чем пустой экран, в котором непонятно, что случилось.
/// </summary>
public sealed class BrokenPage : PageBase
{
    public override string Title { get; }

    public BrokenPage(string key, Exception error)
    {
        Title = "Раздел не открылся";

        var panel = new StackPanel { Margin = new Thickness(28, 40, 28, 40), MaxWidth = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "Не удалось открыть раздел",
            Style = (Style)Application.Current.FindResource("PageTitle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Раздел «{key}» не собрался. Подробности записаны в лог — вкладка «Приложение», «Открыть папку с логами».",
            Style = (Style)Application.Current.FindResource("Lede")
        });

        var box = new TextBox
        {
            Text = error.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 320,
            Height = double.NaN,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.FindResource("Tx2Brush"),
            Padding = new Thickness(12)
        };
        panel.Children.Add(box);
        Content = panel;
    }
}
