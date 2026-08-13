using Aura.Core.Storage;
using Xunit;

namespace InstantReplay.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(512, "512 Б")]
    [InlineData(2048, "2 КБ")]
    [InlineData(5 * 1024 * 1024, "5 МБ")]
    public void ФорматируетРазмер(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void ГигабайтыСДесятичнойЧастью()
    {
        string text = ByteSize.Format(3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024);
        Assert.StartsWith("3", text);
        Assert.EndsWith("ГБ", text);
    }
}

public class FileNamingTests
{
    private static readonly DateTime Moment = new(2026, 7, 30, 21, 5, 9);

    [Fact]
    public void ПодставляетВсеПлейсхолдеры()
    {
        string name = FileNaming.BuildName("{game} {date} - {time} [{preset}]", "Dota 2", Moment, "1080p60", "replay");
        Assert.Equal("Dota 2 2026-07-30 - 21-05-09 [1080p60]", name);
    }

    [Fact]
    public void ЗапрещённыеСимволыЗаменяются()
    {
        string name = FileNaming.BuildName("{game}", "Hero: Siege / Beta", Moment, "1080p60", "replay");
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
    }

    [Fact]
    public void ПустойШаблонДаётИмяПоУмолчанию()
    {
        string name = FileNaming.BuildName("   ", "CS2", Moment, "1080p60", "replay");
        Assert.Equal("CS2 2026-07-30 - 21-05-09", name);
    }

    [Fact]
    public void ШаблонБезТекстаДаётЗапаснойПрефикс()
    {
        // Шаблон из одних пробелов вокруг плейсхолдера, который развернулся в пустоту
        string name = FileNaming.BuildName("{preset}", "CS2", Moment, "", "recording");
        Assert.StartsWith("recording_", name);
    }

    [Fact]
    public void РаскладкаПоПапкамИгр()
    {
        string path = FileNaming.BuildPath(@"C:\Videos", groupByGame: true, "{game}", "CS2",
                                           Moment, "1080p60", "replay", _ => false);
        Assert.Equal(Path.Combine(@"C:\Videos", "CS2", "CS2.mp4"), path);

        string flat = FileNaming.BuildPath(@"C:\Videos", groupByGame: false, "{game}", "CS2",
                                           Moment, "1080p60", "replay", _ => false);
        Assert.Equal(Path.Combine(@"C:\Videos", "CS2.mp4"), flat);
    }

    [Fact]
    public void ПриСовпаденииИмёнДобавляетНомер()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(@"C:\Videos", "CS2.mp4"),
            Path.Combine(@"C:\Videos", "CS2 (2).mp4"),
        };

        string path = FileNaming.BuildPath(@"C:\Videos", groupByGame: false, "{game}", "CS2",
                                           Moment, "1080p60", "replay", taken.Contains);

        Assert.Equal(Path.Combine(@"C:\Videos", "CS2 (3).mp4"), path);
    }
}

public class ClipIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ir-tests-" + Guid.NewGuid().ToString("N"));

    public ClipIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string CreateFile(string relativePath, int bytes, DateTime? written = null)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        if (written is not null) File.SetLastWriteTimeUtc(full, written.Value);
        return full;
    }

    [Fact]
    public void СчитаетТолькоЗаписиИСкриншоты()
    {
        CreateFile("CS2/clip.mp4", 100);
        CreateFile("CS2/shot.png", 50);
        CreateFile("CS2/notes.txt", 999);   // не наш файл

        var index = new ClipIndex();
        index.Rebuild(_root);

        Assert.Equal(2, index.Count);
        Assert.Equal(1, index.ClipCount); // скриншот клипом не считается
        Assert.Equal(150, index.TotalBytes);
    }

    [Fact]
    public void ДобавлениеИУдалениеМеняютИтоги()
    {
        var index = new ClipIndex();
        index.Rebuild(_root);
        Assert.Equal(0, index.TotalBytes);

        string clip = CreateFile("Dota 2/clip.mp4", 300);
        index.Add(clip);
        Assert.Equal(300, index.TotalBytes);
        Assert.Equal(1, index.ClipCount);

        index.Remove(clip);
        Assert.Equal(0, index.TotalBytes);
        Assert.Equal(0, index.ClipCount);
    }

    [Fact]
    public void ПовторноеДобавлениеНеУдваиваетРазмер()
    {
        string clip = CreateFile("clip.mp4", 200);
        var index = new ClipIndex();
        index.Rebuild(_root);

        index.Add(clip);
        index.Add(clip);

        Assert.Equal(1, index.Count);
        Assert.Equal(200, index.TotalBytes);
    }

    [Fact]
    public void ПереименованиеСохраняетОбъём()
    {
        string clip = CreateFile("old.mp4", 400);
        var index = new ClipIndex();
        index.Rebuild(_root);

        string renamed = Path.Combine(_root, "new.mp4");
        File.Move(clip, renamed);
        index.Rename(clip, renamed);

        Assert.Equal(1, index.Count);
        Assert.Equal(400, index.TotalBytes);
        Assert.Equal(renamed, index.OldestFirst()[0].Path);
    }

    [Fact]
    public void СамыеСтарыеИдутПервыми()
    {
        CreateFile("new.mp4", 10, DateTime.UtcNow);
        CreateFile("old.mp4", 10, DateTime.UtcNow.AddDays(-5));
        CreateFile("middle.mp4", 10, DateTime.UtcNow.AddDays(-1));

        var index = new ClipIndex();
        index.Rebuild(_root);

        var order = index.OldestFirst().Select(f => Path.GetFileName(f.Path)).ToArray();
        Assert.Equal(new[] { "old.mp4", "middle.mp4", "new.mp4" }, order);
    }

    [Fact]
    public void ИндексЗнаетСвоюПапку()
    {
        var index = new ClipIndex();
        Assert.False(index.MatchesRoot(_root));

        index.Rebuild(_root);

        Assert.True(index.IsBuilt);
        Assert.True(index.MatchesRoot(_root + Path.DirectorySeparatorChar)); // хвостовой слэш не важен
        Assert.False(index.MatchesRoot(Path.Combine(_root, "other")));
    }
}
