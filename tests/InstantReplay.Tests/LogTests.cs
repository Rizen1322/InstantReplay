using Aura.Core.Logging;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Регрессия на потерянные предсмертные сообщения.
///
/// Log.Fatal писал через File.AppendAllText, пока фоновый писатель держал тот же
/// файл открытым. Оба открывали его с FileShare.Read — второй получал отказ в
/// доступе, а пустой catch его глотал. В результате все три глобальных обработчика
/// падений писали в пустоту, и разбирать краши было не по чему.
///
/// Тест сначала ДОЖИДАЕТСЯ, пока фоновый писатель реально откроет файл: без этого
/// проверка проходила бы и на сломанном коде.
///
/// Log — статический на процесс, поэтому класс не должен идти параллельно другим:
/// собственная коллекция xUnit это и обеспечивает.
/// </summary>
[Collection("Log")]
public class LogTests
{
    [Fact]
    public void FatalПопадаетВФайлПокаПишетФоновыйПоток()
    {
        string dir = NewLogDir();
        Log.Init(dir);

        // Init кладёт в очередь стартовую строку — по появлению файла видно, что
        // писатель проснулся и ДЕРЖИТ дескриптор открытым.
        string path = Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(WaitForFile(path), "фоновый писатель не создал файл лога за 5 секунд");

        Log.Fatal("Test", "предсмертное сообщение");

        // Fatal пишет синхронно и сбрасывает на диск сам — ждать писателя не нужно
        Assert.Contains("предсмертное сообщение", ReadShared(path));
    }

    /// <summary>
    /// Строка, поставленная в очередь прямо перед падением, не теряется: её либо
    /// успевает забрать писатель, либо дописывает сам Fatal (см. FatalTailLines).
    ///
    /// Кто именно из двоих сработал — не проверяем: это гонка по своей природе, и
    /// требовать здесь строгий порядок значило бы написать мигающий тест. Важно,
    /// что при любом исходе строка оказывается на диске.
    /// </summary>
    [Fact]
    public void СтрокаИзОчередиНеТеряетсяПриПадении()
    {
        string dir = NewLogDir();
        Log.Init(dir);
        string path = Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(WaitForFile(path), "фоновый писатель не создал файл лога за 5 секунд");

        Log.Info("Test", "строка перед падением");
        Log.Fatal("Test", "упали");

        // Ждём с запасом: если строку забрал писатель, она доедет до диска своим ходом
        Assert.True(WaitForText(path, "строка перед падением"), "строка из очереди потерялась");
        Assert.True(WaitForText(path, "упали"), "предсмертное сообщение потерялось");
    }

    [Fact]
    public void ЛогЧитаетсяПокаПриложениеРаботает()
    {
        string dir = NewLogDir();
        Log.Init(dir);
        string path = Path.Combine(dir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(WaitForFile(path), "фоновый писатель не создал файл лога за 5 секунд");

        // FileShare.ReadWrite у писателя: иначе лог не открыть на чтение до выхода
        var ex = Record.Exception(() => ReadShared(path));
        Assert.Null(ex);
    }

    private static string NewLogDir() =>
        Path.Combine(Path.GetTempPath(), "AuraLogTests", Guid.NewGuid().ToString("N"));

    private static bool WaitForFile(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForText(string path, string needle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadShared(path).Contains(needle, StringComparison.Ordinal)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>Прочитать файл, который прямо сейчас открыт писателем на запись.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
