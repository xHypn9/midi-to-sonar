using SonarController.Core;

namespace SonarController.Midi;

public enum MidiMessageType
{
    ControlChange,
    Note
}

public enum MidiButtonBehavior
{
    Momentary,
    Toggle
}

public enum MidiMappingTarget
{
    Volume,
    Mute
}

public sealed record MidiMessage(
    string Device,
    int Channel,
    MidiMessageType MessageType,
    int Number,
    int Value);

public sealed record MidiMapping(
    string Device,
    int Channel,
    MidiMessageType MessageType,
    int Number,
    MidiButtonBehavior ButtonBehavior = MidiButtonBehavior.Momentary,
    int ActivationValue = 127)
{
    public MidiMapping(string device, int channel, int cc)
        : this(device, channel, MidiMessageType.ControlChange, cc)
    {
    }
}

public sealed class MidiMappingConfiguration
{
    public Dictionary<SonarChannel, MidiMapping> VolumeMappings { get; } = new();

    public Dictionary<SonarChannel, MidiMapping> MuteMappings { get; } = new();

    public MidiMappingConfiguration Clone()
    {
        var clone = new MidiMappingConfiguration();
        foreach (var pair in VolumeMappings)
        {
            clone.VolumeMappings[pair.Key] = pair.Value;
        }
        foreach (var pair in MuteMappings)
        {
            clone.MuteMappings[pair.Key] = pair.Value;
        }
        return clone;
    }
}

public sealed record MidiMappingLearned(
    SonarChannel Channel,
    MidiMappingTarget Target,
    MidiMapping Mapping);

public sealed record MidiMuteChanged(SonarChannel Channel, bool IsMuted);

public sealed record MidiVolumeChanged(SonarChannel Channel, double Volume);

public interface IMidiInput : IAsyncDisposable
{
    event EventHandler<MidiMessage>? MessageReceived;

    event EventHandler<IReadOnlyList<string>>? DevicesChanged;

    void Start();

    IReadOnlyList<string> GetConnectedDeviceNames();
}
