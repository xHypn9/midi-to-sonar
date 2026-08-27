namespace SonarController.Core;

public enum SonarChannel
{
    Master,
    Game,
    Chat,
    Media,
    Aux,
    Mic
}

public static class SonarChannelMap
{
    public static string ToApiId(SonarChannel channel) => channel switch
    {
        SonarChannel.Master => "master",
        SonarChannel.Game => "game",
        SonarChannel.Chat => "chatRender",
        SonarChannel.Media => "media",
        SonarChannel.Aux => "aux",
        SonarChannel.Mic => "chatCapture",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
    };

    public static string ToConfigKey(SonarChannel channel) =>
        channel.ToString().ToLowerInvariant();
}
