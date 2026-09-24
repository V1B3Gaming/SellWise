using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

public class RecipeUnlocksTests
{
    // CraftType 1 = BSM
    private static RecipeInfo Recipe(int level = 50, uint book = 0, uint quest = 0)
        => new(1, 1, 1, 1, level, true, false, false, book, [new(2, 1)], quest);

    private static RecipeUnlocks Unlocks(int bsm = 100, uint[]? books = null, uint[]? quests = null) => new()
    {
        JobLevels = [100, bsm, 100, 100, 100, 100, 100, 100],
        UnlockedBooks = new HashSet<uint>(books ?? []),
        CompletedQuests = new HashSet<uint>(quests ?? []),
        BookName = id => $"Master Blacksmith {id}",
        QuestName = _ => "Hammer Time",
    };

    [Fact]
    public void PlainRecipeAtYourLevelIsUnlocked()
        => Assert.Null(Unlocks().Describe(Recipe()));

    [Fact]
    public void ReportsLevelGap()
        => Assert.Equal("Needs BSM 50 (you're 42)", Unlocks(bsm: 42).Describe(Recipe()));

    [Fact]
    public void ReportsJobNotUnlocked()
        => Assert.Equal("BSM isn't unlocked", Unlocks(bsm: 0).Describe(Recipe()));

    [Fact]
    public void ReportsMissingMasterBook()
    {
        Assert.Equal("Needs Master Blacksmith 7", Unlocks().Describe(Recipe(book: 7)));
        Assert.Null(Unlocks(books: [7]).Describe(Recipe(book: 7)));
    }

    [Fact]
    public void ReportsMissingQuest()
    {
        Assert.Equal("Needs quest \"Hammer Time\"", Unlocks().Describe(Recipe(quest: 69428)));
        Assert.True(Unlocks(quests: [69428]).IsUnlocked(Recipe(quest: 69428)));
    }

    [Fact]
    public void CombinesReasons()
        => Assert.Equal("Needs BSM 90 (you're 80); Needs Master Blacksmith 7", Unlocks(bsm: 80).Describe(Recipe(level: 90, book: 7)));
}
