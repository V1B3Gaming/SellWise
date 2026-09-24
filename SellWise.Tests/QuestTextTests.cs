using SellWise.Core;
using Xunit;

namespace SellWise.Tests;

/// <summary>Cases taken from real crafter and gatherer quest text.</summary>
public class QuestTextTests
{
    [Fact]
    public void BareCountAfterDeliverForTheOnlyItem()
    {
        // Supplies for the Sick (CRP 10)
        string[] texts =
        [
            "Before you can undertake your final trial, Timbermaster Beatin asks you to help replenish the guild's stock of ash lumber. Deliver twelve lengths to him at the Oak Atrium.",
            "Deliver lengths of ash lumber to Beatin.",
        ];
        Assert.Equal((12, false), QuestText.Requirement(texts, "Ash Lumber", "length of ash lumber", "lengths of ash lumber", onlyItem: true));
    }

    [Fact]
    public void CountBeforeTheName()
    {
        // Know Thy Land (MIN 5)
        string[] texts = ["Wishing to impress upon you the importance of knowing the land, the guildmaster bids you procure ten bone chips. Mine the required quantity beyond the Gate."];
        Assert.Equal((10, false), QuestText.Requirement(texts, "Bone Chip", "bone chip", "bone chips", onlyItem: true));
    }

    [Fact]
    public void HighQuality()
    {
        // A-hunting He Will Go (CRP 53)
        string[] texts = ["Fashion Barthovieu his desired high-quality holy cedar composite bow, so that he may join the ranks of the great dragonslayers."];
        Assert.Equal((1, true), QuestText.Requirement(texts, "Holy Cedar Composite Bow", "holy cedar composite bow", "holy cedar composite bows", onlyItem: true));
    }

    [Fact]
    public void APairIsOneItem()
    {
        // Brand Loyalty (LTW 40)
        string[] texts = ["Present a pair of boarskin smithy's gloves  to Geva."];
        Assert.Equal((1, false), QuestText.Requirement(texts, "Boarskin Smithy's Gloves", "pair of boarskin smithy's gloves", "pairs of boarskin smithy's gloves", onlyItem: true));
    }

    [Fact]
    public void OneOfSeveralItems()
    {
        // A Carpenter in Need (CRP 15): two items; a stray count elsewhere mustn't be taken for either.
        string[] texts =
        [
            "The task of preparing a vast batch of oak lumber has overwhelmed fellow adventurer Mera Pamera.",
            "Deliver a feathered harpoon to Ywain.",
            "Deliver an ash shortbow to Luciane.",
        ];
        Assert.Equal((1, false), QuestText.Requirement(texts, "Feathered Harpoon", "feathered harpoon", "feathered harpoons", onlyItem: false));
        Assert.Equal((1, false), QuestText.Requirement(texts, "Ash Shortbow", "ash shortbow", "ash shortbows", onlyItem: false));
    }

    [Fact]
    public void NotStated()
    {
        string[] texts = ["Deliver warmwater trout to Chuchuroon."];
        Assert.Equal((null, false), QuestText.Requirement(texts, "Warmwater Trout", "warmwater trout", "warmwater trout", onlyItem: true));
    }

    [Fact]
    public void Digits()
    {
        string[] texts = ["Deliver 3 high-quality bronze ingots to Brithael."];
        Assert.Equal((3, true), QuestText.Requirement(texts, "Bronze Ingot", "bronze ingot", "bronze ingots", onlyItem: true));
    }
}
