using Aura.Core.GameDetection;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameFolderNameTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT²", "_LPT²")]
    [InlineData("Game...", "Game")]
    [InlineData("..", "Desktop")]
    public void Reserved_and_empty_names_produce_usable_folders(string name, string expected) =>
        Assert.Equal(expected, GameDetector.Sanitize(name));

    [Fact]
    public void Long_names_leave_space_for_the_recording_file() =>
        Assert.True(GameDetector.Sanitize(new string('x', 400)).Length <= 100);

    [Fact]
    public void Generic_games_folder_does_not_classify_tools_as_games()
    {
        Assert.False(GameDetector.IsGameLibraryPath(@"C:\Games\tools\tool.exe"));
        Assert.True(GameDetector.IsGameLibraryPath(@"C:\Steam\steamapps\common\Game\game.exe"));
    }
}
