using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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
    private static bool _changelogVisible;

    public static void ShowChangelog(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries)
    {
        if (_changelogVisible || entries.Count == 0 || Application.Current is null) return;
        _changelogVisible = true;
        try { CreateChangelogWindow(entries).ShowDialog(); }
        finally { _changelogVisible = false; }
    }

    /// <summary>
    /// Окно «Что нового»: шапка с версией и кнопкой закрытия, дальше сгруппированные
    /// по разделам и версиям карточки. Строка «## …» внутри версии становится заголовком раздела.
    /// </summary>
    internal static Window CreateChangelogWindow(IReadOnlyList<Core.SystemIntegration.ChangelogEntry> entries)
    {
        var app = Application.Current!;
        var window = NewDialogWindow(width: 620);

        // --- шапка ---
        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://siteoforigin:,,,/Assets/logo.png")),
            Width = 50,
            Height = 50,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        headGrid.Children.Add(logo);

        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal };
        metaRow.Children.Add(new Border
        {
            Background = (Brush)app.FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2.5, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entries.Count == 1
                    ? $"v{entries[0].Version}"
                    : $"v{entries[^1].Version} – {entries[0].Version}",
                FontFamily = (FontFamily)app.FindResource("DispFont"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)app.FindResource("AccentTxBrush")
            }
        });
        metaRow.Children.Add(new TextBlock
        {
            Text = entries.Count > 5 ? "История обновлений" : "Что нового",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)app.FindResource("Tx3Brush"),
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        titles.Children.Add(metaRow);
        titles.Children.Add(new TextBlock
        {
            Text = entries[0].Headline,
            FontFamily = (FontFamily)app.FindResource("DispFont"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)app.FindResource("TxBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 26,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Grid.SetColumn(titles, 1);
        headGrid.Children.Add(titles);

        var closeButton = new Button
        {
            Style = (Style)app.FindResource("BtnPlain"),
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            ToolTip = "Закрыть (Esc)",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, -4, -4, 0)
        };
        Aura.Controls.Deco.SetIcon(closeButton, (Geometry)app.FindResource("Ico.X"));
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        headGrid.Children.Add(closeButton);

        var head = new Border
        {
            CornerRadius = new CornerRadius(15, 15, 0, 0),
            Background = (Brush)app.FindResource("AmbientBrush"),
            BorderBrush = (Brush)app.FindResource("SepBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(26, 22, 24, 20),
            Child = headGrid
        };

        // --- содержимое ---
        var content = new StackPanel { Margin = new Thickness(26, 18, 22, 18) };
        for (int n = 0; n < entries.Count; n++)
        {
            var entry = entries[n];
            // Заголовок второй и следующих версий: у первой он уже в шапке
            if (n > 0)
            {
                var versionHeader = new Grid { Margin = new Thickness(2, 22, 0, 10) };
                versionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                versionHeader.ColumnDefinitions.Add(new ColumnDefinition());
                var versionPill = new Border
                {
                    Style = (Style)app.FindResource("Pill"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 10, 0),
                    Child = new TextBlock
                    {
                        Text = $"v{entry.Version}",
                        FontFamily = (FontFamily)app.FindResource("DispFont"),
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)app.FindResource("Tx2Brush")
                    }
                };
                var versionTitle = new TextBlock
                {
                    Text = entry.Headline,
                    FontFamily = (FontFamily)app.FindResource("DispFont"),
                    FontSize = 14.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)app.FindResource("TxBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(versionTitle, 1);
                versionHeader.Children.Add(versionPill);
                versionHeader.Children.Add(versionTitle);
                content.Children.Add(versionHeader);
            }

            StackPanel? card = null;
            foreach (string item in entry.Items)
            {
                if (item.StartsWith("## "))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = item[3..],
                        Style = (Style)app.FindResource("GroupLabel"),
                        Margin = new Thickness(2, content.Children.Count == 0 ? 0 : 16, 0, 8)
                    });
                    card = null;
                    continue;
                }
                if (card is null)
                {
                    card = new StackPanel();
                    content.Children.Add(new Border
                    {
                        Style = (Style)app.FindResource("Card"),
                        Padding = new Thickness(18, 14, 18, 16),
                        Child = card
                    });
                }
                if (card.Children.Count > 0)
                {
                    card.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)app.FindResource("SepBrush"),
                        Margin = new Thickness(26, 11, 0, 11),
                        SnapsToDevicePixels = true
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
            MaxHeight = Math.Min(540, SystemParameters.WorkArea.Height - 250),
            Margin = new Thickness(0, 0, 4, 0)
        };

        var hintText = new TextBlock
        {
            Text = entries.Count == 1 ? "Esc — закрыть" : $"Версий в списке: {entries.Count} · Esc — закрыть",
            FontSize = 12,
            Foreground = (Brush)app.FindResource("Tx3Brush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var okButton = new Button
        {
            Content = "Отлично",
            Style = (Style)app.FindResource("BtnPri"),
            MinWidth = 132,
            Height = 36,
            IsDefault = true
        };
        okButton.Click += (_, _) => window.Close();

        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition());
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(hintText);
        Grid.SetColumn(okButton, 1);
        footGrid.Children.Add(okButton);

        var foot = new Border
        {
            BorderBrush = (Brush)app.FindResource("SepBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(26, 14, 26, 18),
            Child = footGrid
        };

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(scroll);
        panel.Children.Add(foot);

        Dress(window, panel, app);
        return window;
    }

    /// <summary>Строчка списка: точка с мягким ореолом слева и выделенным префиксом до «: ».</summary>
    private static Grid Bullet(string text, Application app)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var marker = new Grid
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 4, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        marker.Children.Add(new Ellipse { Fill = (Brush)app.FindResource("AccentSoftBrush") });
        marker.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)app.FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(marker);

        var body = new TextBlock
        {
            FontSize = 13.5,
            Foreground = (Brush)app.FindResource("Tx2Brush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon is >= 3 and <= 36 && text.AsSpan(0, colon).IndexOfAny(".!?()".AsSpan()) < 0)
        {
            body.Inlines.Add(new Run(text[..(colon + 1)])
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)app.FindResource("TxBrush")
            });
            body.Inlines.Add(new Run(text[(colon + 1)..]));
        }
        else
        {
            body.Text = text;
        }
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
