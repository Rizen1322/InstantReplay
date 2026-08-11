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

    /// <summary>
    /// «Что нового» после обновления. Собирается теми же ресурсами темы, что и
    /// остальные диалоги, поэтому окно выглядит одинаково родным и в тёмной теме,
    /// и в светлой — отдельной вёрстки под каждую не требуется.
    /// </summary>
    public static void ShowChangelog(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries)
    {
        if (entries.Count == 0) return;
        var app = Application.Current;
        if (app is null) return;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "Что нового",
            FontFamily = (FontFamily)app.FindResource("DispFont"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = entries.Count == 1
                ? $"Версия {entries[0].Version}"
                : $"Версии {entries[^1].Version} — {entries[0].Version}",
            Style = (Style)app.FindResource("RowSub"),
            Margin = new Thickness(0, 3, 0, 0)
        });

        foreach (var entry in entries)
        {
            content.Children.Add(new TextBlock
            {
                Text = entry.Headline,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 18, 0, 0)
            });

            foreach (string item in entry.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 9, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                // Точка вместо тире: маркер держит левый край строк ровным
                var dot = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = (Brush)app.FindResource("AccentBrush"),
                    Margin = new Thickness(2, 7, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                row.Children.Add(dot);

                var text = new TextBlock
                {
                    Text = item,
                    Style = (Style)app.FindResource("RowSub"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                content.Children.Add(row);
            }
        }

        // Длинный список не должен вырастать за пределы экрана
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            Margin = new Thickness(0, 0, 4, 0)
        };

        var panel = new StackPanel { Margin = new Thickness(24, 22, 20, 18) };
        panel.Children.Add(scroll);

        var window = NewDialogWindow(width: 480);

        var okButton = new Button
        {
            Content = "Понятно",
            Style = (Style)app.FindResource("BtnPri"),
            MinWidth = 120,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        okButton.Click += (_, _) => window.Close();
        panel.Children.Add(okButton);

        Dress(window, panel, app);
        window.ShowDialog();
    }

    /// <summary>
    /// Годится ли окно в хозяева диалога.
    ///
    /// WPF бросает InvalidOperationException прямо из присваивания Owner, если окно
    /// ещё ни разу не показывали. В Application.Current.Windows такое окно вполне
    /// может лежать: приложение умеет стартовать свёрнутым в трей и пересоздавать
    /// главное окно. Исключение летело в глобальный обработчик и гасилось там —
    /// со стороны «Удалить» и «Переименовать» выглядели как неработающие кнопки.
    /// </summary>
    private static bool CanOwnDialog(Window window)
    {
        try { return PresentationSource.FromVisual(window) is not null; }
        catch { return false; }
    }

    /// <summary>Пустое окно диалога: хозяин, прозрачность, размер по содержимому.</summary>
    private static Window NewDialogWindow(double width)
    {
        var app = Application.Current;

        // Сначала видимое окно, потом любое показанное — диалог должен появиться
        // по центру того окна, из которого его позвали.
        var candidates = app?.Windows.OfType<MainWindow>().Where(CanOwnDialog).ToList() ?? [];
        var owner = candidates.FirstOrDefault(w => w.IsVisible) ?? candidates.FirstOrDefault();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            // Без хозяина диалог иначе никак не найти: в трее окна нет, а поверх
            // игры он не всплывёт. Кнопка на панели задач — единственный путь к нему.
            ShowInTaskbar = owner is null,
            SizeToContent = SizeToContent.Height,
            Width = width,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };

        // Присваивание всё равно в try: между проверкой и установкой окно могли закрыть.
        if (owner is not null)
            try { window.Owner = owner; }
            catch { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; }

        return window;
    }

    /// <summary>Карточка вокруг содержимого и поведение окна — общее для всех диалогов.</summary>
    private static void Dress(Window window, FrameworkElement content, Application app)
    {
        window.Content = new Border
        {
            Background = (Brush)app.FindResource("CanvasBrush"),
            BorderBrush = (Brush)app.FindResource("HairBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(12),
            Effect = (System.Windows.Media.Effects.Effect)app.FindResource("ToastShadow"),
            Child = content
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
    }

    private static string? Show(string title, string? message, string okText, bool cancel, string? input)
    {
        var app = Application.Current;
        var window = NewDialogWindow(420);

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

        Dress(window, panel, app!);
        window.ShowDialog();
        return result;
    }
}
