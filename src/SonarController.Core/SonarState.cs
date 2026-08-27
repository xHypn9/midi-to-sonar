namespace SonarController.Core;

public sealed record SonarChannelState(double Volume, bool IsMuted);

public sealed record SonarSnapshot(
    IReadOnlyDictionary<SonarChannel, SonarChannelState> Channels,
    DateTimeOffset ReadAt)
{
    public SonarChannelState this[SonarChannel channel] => Channels[channel];
}
