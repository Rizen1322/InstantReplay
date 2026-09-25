using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>
/// Советы в боковой панели разделов. Каждый заход в раздел показывает следующий
/// совет, а не один и тот же: постоянную надпись глаз перестаёт замечать на второй
/// день. Советы только про то, что в Aura действительно есть, а сочетания клавиш
/// берутся из настроек, чтобы после переназначения совет не врал.
/// </summary>
public static class Tips
{
    private static readonly Random Rng = new();
    private static readonly Dictionary<string, int> Cursor = [];

    private static IReadOnlyList<string> Pool(string topic) =>
        // Совет про сочетание, которое не назначено, звучал бы как « сохраняет…»
        Raw(topic).Where(tip => !tip.StartsWith(' ') && !tip.Contains(",  ") && !tip.Contains(" , ")).ToList();

    private static IReadOnlyList<string> Raw(string topic)
    {
        var s = Services.Settings.Current;
        return topic switch
        {
            "video" =>
            [
                "HEVC весит примерно вдвое меньше H.264 при той же картинке. H.264 нужен, только если ролик пойдёт туда, где HEVC не открывается.",
                "Если игра выдаёт кадров намного больше, чем показывает монитор, ограничь FPS в игре чуть ниже частоты экрана. Видеокарта освободится, и запись станет ровнее.",
                "120 и 144 кадра нужны для замедленного просмотра. Файл и нагрузка на видеокарту растут почти вдвое.",
                "Десять бит убирают ступеньки на градиентах неба и дыма, а места почти не добавляют.",
                "Буфер на диске выручает при повторе длиннее пяти минут: оперативная память почти не тратится.",
                "Запись 1440p на мониторе 4K уменьшается на видеокарте и почти не нагружает процессор.",
                "Курсор в записи нужен для гайдов и стратегий. В шутерах он только мешает.",
            ],
            "audio" =>
            [
                "Раздельные дорожки пишут игру и микрофон отдельно. В редакторе потом можно оставить только игру.",
                "Порог микрофона глушит всё, что тише отметки на шкале. Поставь его чуть выше шума в тишине, и клавиатура пропадёт между фразами.",
                "Шумодав убирает вентилятор и клавиатуру, но немного нагружает процессор. С хорошим микрофоном в тихой комнате его можно выключить.",
                "Если голос тонет в игре, убавь игру до 70-80%, а не поднимай микрофон выше 100%: так звук не начнёт хрипеть.",
                "Шкалы справа показывают, что попадёт в запись прямо сейчас. Жёлтая и красная зоны значат, что звук скоро начнёт хрипеть.",
            ],
            "keys" =>
            [
                $"{s.HotkeySaveLast30} сохраняет только последние 30 секунд: не придётся резать трёхминутный повтор ради одного момента.",
                "Боковые кнопки мыши удобны для сохранения: их не надо искать на клавиатуре посреди боя.",
                "Если сочетание держит другая программа, нажатие может уйти ей. Такие сочетания видно в проверке справа.",
                $"{s.HotkeyScreenshotRegion} открывает скриншот области. С зажатым Ctrl рамка сама прилипает к окнам и панелям.",
                $"{s.HotkeyOpenFolder} открывает папку с записями из любой игры.",
                "Сочетания работают и в полноэкранной игре: Aura слушает клавиатуру на уровне системы, фокус ей не нужен.",
            ],
            "files" =>
            [
                "Правый клик по клипу, «Сжать для Discord»: ролик ужмётся под лимит из этого раздела.",
                "Папку записей лучше держать на SSD. Повтор на три минуты весит сотни мегабайт и должен лечь на диск за пару секунд.",
                "Недописанные файлы от прерванных сохранений Aura удаляет сама при следующем запуске.",
                "Клипы лежат по папкам с именем игры, поэтому их легко найти и в проводнике.",
                "Кэш превью можно очистить в разделе «Приложение»: кадры карточек соберутся заново.",
            ],
            "app" =>
            [
                "Уведомление не забирает фокус у игры: клавиши и мышь продолжают работать, пока оно на экране.",
                "Свой звук сохранения: любой WAV. Короткий, до полсекунды, звучит приятнее всего.",
                "Со стартом в трее окно не выскакивает при входе в Windows, а повтор включается сам.",
                "Если уведомления перекрывают миникарту или чат, перенеси их в другой угол на схеме выше.",
            ],
            _ =>
            [
                $"{s.HotkeySaveReplay} сохраняет весь буфер повтора, {s.HotkeySaveLast30} только последние 30 секунд.",
                "Потяни по ленте буфера, чтобы сохранить только последние секунды, а не весь повтор.",
                "В редакторе клипа I и O ставят начало и конец, Ctrl+E сохраняет фрагмент без перекодирования.",
                "Скриншот области можно разметить стрелками и текстом прямо поверх экрана, до сохранения.",
                "Если запись дёргается в тяжёлой игре, загляни в раздел «Система»: там видно, успевает ли видеокарта.",
            ]
        };
    }

    /// <summary>Следующий совет раздела. Порядок перемешан, но подряд не повторяется.</summary>
    public static string Next(string topic)
    {
        var pool = Pool(topic);
        int index;
        lock (Cursor)
        {
            if (!Cursor.TryGetValue(topic, out int last)) last = Rng.Next(pool.Count);
            index = (last + 1 + Rng.Next(Math.Max(1, pool.Count - 1))) % pool.Count;
            if (index == last) index = (index + 1) % pool.Count;
            Cursor[topic] = index;
        }
        return pool[index];
    }

    /// <summary>
    /// Панель «Совет» для правой колонки: заголовок, текст и кнопка «Ещё». Совет
    /// меняется при каждом показе раздела.
    /// </summary>
    public static Border Panel(string topic, out Action refresh)
    {
        var text = new TextBlock { Style = (Style)Application.Current.FindResource("AsideNote") };
        var more = new Button
        {
            Content = "Ещё совет",
            Style = (Style)Application.Current.FindResource("BtnPlain"),
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, -8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            FontWeight = FontWeights.Normal
        };
        more.SetResourceReference(Control.ForegroundProperty, "Tx2Brush");
        var head = new Grid { Margin = new Thickness(0, -4, 0, 6) };
        head.Children.Add(new TextBlock
        {
            Text = "Совет",
            Style = (Style)Application.Current.FindResource("AsideTitle"),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(more);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(text);

        refresh = () => text.Text = Next(topic);
        var show = refresh;
        more.Click += (_, _) => show();
        refresh();
        return new Border { Style = (Style)Application.Current.FindResource("AsidePanel"), Child = body };
    }
}
