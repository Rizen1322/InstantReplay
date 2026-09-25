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
        if (entries.Count == 0 || Application.Current is null) return;
        CreateChangelogWindow(entries).ShowDialog();
    }

    /// <summary>
    /// Окно «Что нового»: знак и номер версии крупно, дальше разделы с короткими
    /// строчками. Строка «## …» внутри версии становится заголовком раздела.
    /// </summary>
    internal static Window CreateChangelogWindow(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries)
    {
        var app = Application.Current!;
        var window = NewDialogWindow(width: 600);

        // --- шапка ---
        var head = new Grid { Margin = new Thickness(28, 26, 28, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var logo = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://siteoforigin:,,,/Assets/logo.png")),
            Width = 52,
            Height = 52,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        head.Children.Add(logo);

        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "Что нового",
            FontSize = 12.5,
            Foreground = (Brush)app.FindResource("AccentTxBrush"),
            FontWeight = FontWeights.SemiBold
        });
        titles.Children.Add(new TextBlock
        {
            Text = entries[0].Headline,
            FontFamily = (FontFamily)app.FindResource("DispFont"),
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        titles.Children.Add(new TextBlock
        {
            Text = entries.Count == 1
                ? $"Версия {entries[0].Version}"
                : $"Версии {entries[^1].Version}-{entries[0].Version}",
            Style = (Style)app.FindResource("RowSub"),
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);

        // --- содержимое ---
        var content = new StackPanel { Margin = new Thickness(28, 6, 24, 8) };
        for (int n = 0; n < entries.Count; n++)
        {
            var entry = entries[n];
            // Заголовок второй и следующих версий: у первой он уже в шапке
            if (n > 0)
                content.Children.Add(new TextBlock
                {
                    Text = $"{entry.Headline}, {entry.Version}",
                    FontFamily = (FontFamily)app.FindResource("DispFont"),
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 22, 0, 0)
                });

            StackPanel? card = null;
            foreach (string item in entry.Items)
            {
                if (item.StartsWith("## "))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = item[3..],
                        Style = (Style)app.FindResource("GroupLabel"),
                        Margin = new Thickness(2, 18, 0, 8)
                    });
                    card = null;
                    continue;
                }
                if (card is null)
                {
                    card = new StackPanel { Margin = new Thickness(14, 6, 14, 12) };
                    content.Children.Add(new Border
                    {
                        Style = (Style)app.FindResource("Card"),
                        Margin = new Thickness(0, n == 0 && content.Children.Count == 0 ? 16 : 0, 0, 0),
                        Child = card
                    });
                }
                card.Children.Add(Bullet(item, app));
            }
        }

        // Длинный список не должен вырастать за пределы экрана
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Min(560, SystemParameters.WorkArea.Height - 240),
            Margin = new Thickness(0, 10, 4, 0)
        };

        var okButton = new Button
        {
            Content = "Отлично",
            Style = (Style)app.FindResource("BtnPri"),
            MinWidth = 140,
            Height = 38,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        okButton.Click += (_, _) => window.Close();
        var foot = new Border
        {
            BorderBrush = (Brush)app.FindResource("SepBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(28, 14, 28, 18),
            Child = okButton
        };

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(scroll);
        panel.Children.Add(foot);

        Dress(window, panel, app);
        return window;
    }

    /// <summary>Строчка списка: зелёная точка слева держит край ровным.</summary>
    private static Grid Bullet(string text, Application app)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)app.FindResource("AccentBrush"),
            Margin = new Thickness(0, 7, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        });
        var body = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)app.FindResource("Tx2Brush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
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
