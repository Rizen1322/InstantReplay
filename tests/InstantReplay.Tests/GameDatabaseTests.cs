using Aura.Core.GameDetection;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Внешняя база игр.
///
/// Главное свойство — «испорченный файл ничего не ломает»: списки в первую очередь
/// нужны в момент сохранения клипа, и ошибка в чужом JSON не должна стоить папки
/// с записью.
/// </summary>
public class GameDatabaseTests
{
    [Fact]
    public void ВстроенныеСпискиНеПусты()
    {
        var db = GameDatabase.BuiltIn();
        Assert.True(db.TryGetGame("cs2", out var name));
        Assert.Equal("Counter-Strike 2", name);
        Assert.True(db.IsIgnored("discord"));
        Assert.False(db.IsIgnored("someunknowngame"));
    }

    [Fact]
    public void ИменаExeСравниваютсяБезУчётаРегистра()
    {
        var db = GameDatabase.BuiltIn();
        Assert.True(db.TryGetGame("CS2", out _));
        Assert.True(db.IsIgnored("DiScOrD"));
    }

    [Fact]
    public void ФайлДобавляетИгруИИсключение()
    {
        var db = GameDatabase.BuiltIn().Apply("""
            {
              "games":   { "mygame": "Моя Игра" },
              "ignored": ["someunknowngame"]
            }
            """);

        Assert.True(db.TryGetGame("mygame", out var name));
        Assert.Equal("Моя Игра", name);
        Assert.True(db.IsIgnored("someunknowngame"));

        // Встроенное на месте
        Assert.True(db.TryGetGame("cs2", out _));
    }

    [Fact]
    public void ФайлПереопределяетВстроенноеИмя()
    {
        var db = GameDatabase.BuiltIn().Apply("""{ "games": { "cs2": "Контра" } }""");
        Assert.True(db.TryGetGame("cs2", out var name));
        Assert.Equal("Контра", name);
    }

    [Fact]
    public void ВстроенноеИсключениеМожноСнять()
    {
        // Кто-то пишет летсплей про сам OBS — папка «OBS» ему и нужна
        Assert.True(GameDatabase.BuiltIn().IsIgnored("obs64"));

        var db = GameDatabase.BuiltIn().Apply("""{ "allowed": ["obs64"] }""");
        Assert.False(db.IsIgnored("obs64"));
    }

    [Fact]
    public void ИсходнаяБазаНеМеняетсяПриНаложении()
    {
        var builtIn = GameDatabase.BuiltIn();
        builtIn.Apply("""{ "games": { "mygame": "Моя Игра" } }""");

        // Детектор подменяет ссылку одним присваиванием — исходный объект обязан
        // остаться прежним, иначе чужой поток увидит полусобранное состояние
        Assert.False(builtIn.TryGetGame("mygame", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "games": null, "ignored": null, "allowed": null }""")]
    [InlineData("""{ "games": { "": "пусто", "  ": "пробелы" } }""")]
    [InlineData("""{ "ignored": ["", "   "] }""")]
    public void ПустыеИНеполныеФайлыНеЛомаютБазу(string json)
    {
        var db = GameDatabase.BuiltIn().Apply(json);
        Assert.True(db.TryGetGame("cs2", out _));
        Assert.True(db.IsIgnored("discord"));
    }

    [Fact]
    public void КомментарииИЛишниеЗапятыеРазрешены()
    {
        // Файл правят руками, и запятая в конце списка — самая частая опечатка
        var db = GameDatabase.BuiltIn().Apply("""
            {
              // мои игры
              "games": { "mygame": "Моя Игра", },
            }
            """);
        Assert.True(db.TryGetGame("mygame", out _));
    }

    [Fact]
    public void СломанныйJsonБросаетИЭтоЛовитЗагрузчик()
    {
        // Apply отвечает за разбор, Load — за то, чтобы ошибка не всплыла наружу
        Assert.ThrowsAny<Exception>(() => GameDatabase.BuiltIn().Apply("{ это не json"));

        // Load читает несуществующие файлы и обязан вернуть рабочую базу
        var db = GameDatabase.Load();
        Assert.True(db.TryGetGame("cs2", out _));
    }

    [Fact]
    public void ИщемФайлыРядомСПриложениемИУПользователя()
    {
        var paths = GameDatabase.FilePaths().ToList();
        Assert.Equal(2, paths.Count);
        Assert.All(paths, p => Assert.EndsWith("games.json", p));
        Assert.Contains(paths, p => p.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
