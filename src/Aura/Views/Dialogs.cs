using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aura.Views;

/// <summary>
/// Небольшие модальные окна в оформлении приложения: вопрос, ввод строки,
/// сообщение. Системный MessageBox рядом с этим интерфейсом смотрится чужим.
/// </summary>
public static class Dialogs
{
    public static bool Ask(string title, string message, string okText = "Ок") =>
        Show(title, message, okText, cancel: true, input: null) is not null;

    public static void Say(string title, string message) =>
        Show(title, message, "Понятно", cancel: false, input: null);

    /// <summary>Ввод строки. null — отменили.</summary>
    public static string? Prompt(string title, string initial, string okText = "Готово") =>
        Show(title, null, okText, cancel: true, input: initial);

    private static string? Show(string title, string? message, string okText, bool cancel, string? input)
    {
        var app = Application.Current;
        var owner = app?.Windows.OfType<MainWindow>().FirstOrDefault();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Width = 420,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)app!.FindResource("DispFont"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        TextBox? box = null;
        if (message is not null)
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Style = (Style)app.FindResource("RowSub"),
                Margin = new Thickness(0, 8, 0, 0)
            });

        if (input is not null)
        {
            box = new TextBox { Text = input, Margin = new Thickness(0, 14, 0, 0), Height = 34 };
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            panel.Children.Add(box);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        string? result = null;
        if (cancel)
        {
            var cancelButton = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 8, 0) };
            cancelButton.Click += (_, _) => window.Close();
            buttons.Children.Add(cancelButton);
        }

        var okButton = new Button
        {
            Content = okText,
            Style = (Style)app.FindResource("BtnPri"),
            MinWidth = 110,
            IsDefault = true
        };
        okButton.Click += (_, _) => { result = box?.Text ?? ""; window.Close(); };
        buttons.Children.Add(okButton);
        panel.Children.Add(buttons);

        window.Content = new Border
        {
            Background = (Brush)app.FindResource("CanvasBrush"),
            BorderBrush = (Brush)app.FindResource("HairBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(12),
            Effect = (System.Windows.Media.Effects.Effect)app.FindResource("ToastShadow"),
            Child = panel
        };

        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        window.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        window.Loaded += (_, _) =>
        {
            // Явно поднимаем и забираем фокус: диалог зовут из пунктов контекстного
            // меню, и без этого он мог остаться под закрывающимся всплывающим окном.
            window.Activate();
            window.Focus();

            window.Opacity = 0;
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.16)));
        };

        window.ShowDialog();
        return result;
    }
}
