using Aura.Core.Library;
using Xunit;

namespace InstantReplay.Tests;

// Тесты кольцевого буфера живут в AuraArenaBufferTests: он хранит кадры в арене
// из блоков, и проверять там надо границы блоков, обход по кругу и защиту снимка
// во время записи файла.


public class ClipGroupingTests
{
    private static readonly DateTime Today = new(2026, 7, 30);

    [Fact]
    public void СегодняИВчера()
    {
        Assert.Equal("Сегодня", ClipGrouping.DateTitle(Today.AddHours(3), Today));
        Assert.Equal("Вчера", ClipGrouping.DateTitle(Today.AddDays(-1).AddHours(22), Today));
    }

    [Fact]
    public void ДатаВЭтомГодуБезГода()
    {
        string title = ClipGrouping.DateTitle(new DateTime(2026, 7, 14), Today);
        Assert.DoesNotContain("2026", title);
        Assert.Contains("14", title);
    }

    [Fact]
    public void ПрошлогодняяДатаСГодом() =>
        Assert.Contains("2025", ClipGrouping.DateTitle(new DateTime(2025, 12, 31), Today));
}
