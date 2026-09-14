using Xunit;

namespace Index2SP.Tests;

public class BeeperClientTests
{
    [Theory]
    [InlineData("telegram", "telegram")]
    [InlineData("telegram", "Telegram")]
    [InlineData("gmessages", "Google Messages")]
    [InlineData("gmessages", "google messages")]
    [InlineData("googlechat", "Google Chat")]
    [InlineData("googlechat", "Hangouts")]
    [InlineData("facebookgo", "Messenger")]
    [InlineData("twitter", "x")]
    [InlineData("local-instagram_ba_63lrIy-1uIDuwse-Go37QSwohKM", "Instagram")]
    [InlineData("local-gvoice_ba_li-GkDW1-GkR2cVtGfCT91kqnHY", "Google Voice")]
    [InlineData("discord", "Discord")]
    public void NetworkMatchesPlatform_MatchesKnownAliases(string network, string platformQuery)
    {
        Assert.True(BeeperClient.NetworkMatchesPlatform(network, platformQuery));
    }

    [Theory]
    [InlineData("telegram", "whatsapp")]
    [InlineData("gmessages", "telegram")]
    [InlineData("local-instagram_ba_63lrIy-1uIDuwse-Go37QSwohKM", "telegram")]
    public void NetworkMatchesPlatform_RejectsMismatch(string network, string platformQuery)
    {
        Assert.False(BeeperClient.NetworkMatchesPlatform(network, platformQuery));
    }

    [Fact]
    public void NetworkMatchesPlatform_FallsBackToRawSlugForUnknownNetworks()
    {
        Assert.True(BeeperClient.NetworkMatchesPlatform("mattermost", "Mattermost"));
        Assert.False(BeeperClient.NetworkMatchesPlatform("mattermost", "telegram"));
    }
}
