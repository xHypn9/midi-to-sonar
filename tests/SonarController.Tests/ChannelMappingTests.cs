using SonarController.Core;

namespace SonarController.Tests;

public sealed class ChannelMappingTests
{
    [Theory]
    [InlineData(SonarChannel.Master, "master")]
    [InlineData(SonarChannel.Game, "game")]
    [InlineData(SonarChannel.Chat, "chatRender")]
    [InlineData(SonarChannel.Media, "media")]
    [InlineData(SonarChannel.Aux, "aux")]
    [InlineData(SonarChannel.Mic, "chatCapture")]
    public void MapsGuiChannelsToSonarIds(SonarChannel channel, string expected)
    {
        Assert.Equal(expected, SonarChannelMap.ToApiId(channel));
    }
}
