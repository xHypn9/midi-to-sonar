using System.Text.Json;
using System.Text.Json.Serialization;
using SonarController.Core;

namespace SonarController.Midi;

public sealed class MidiMappingStore
{
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public MidiMappingStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonarMidiController",
            "midi-mappings.json");
    }

    public string Path { get; }

    public async Task<MidiMappingConfiguration> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new MidiMappingConfiguration();
        }

        await using var stream = File.OpenRead(Path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new MidiMappingConfiguration();
        }

        return document.RootElement.TryGetProperty("volumeMappings", out _)
            ? LoadCurrent(document.RootElement)
            : LoadLegacy(document.RootElement);
    }

    public async Task SaveAsync(
        MidiMappingConfiguration mappings,
        CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The MIDI configuration path has no directory.");
        Directory.CreateDirectory(directory);

        var stored = new StoredConfiguration
        {
            Version = CurrentVersion,
            VolumeMappings = ToStoredDictionary(mappings.VolumeMappings),
            MuteMappings = ToStoredDictionary(mappings.MuteMappings)
        };

        var temporaryPath = Path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static MidiMappingConfiguration LoadCurrent(JsonElement root)
    {
        var stored = root.Deserialize<StoredConfiguration>(JsonOptions) ?? new StoredConfiguration();
        var result = new MidiMappingConfiguration();
        CopyStoredMappings(stored.VolumeMappings, result.VolumeMappings);
        CopyStoredMappings(stored.MuteMappings, result.MuteMappings);
        return result;
    }

    private static MidiMappingConfiguration LoadLegacy(JsonElement root)
    {
        var result = new MidiMappingConfiguration();
        foreach (var property in root.EnumerateObject())
        {
            if (!Enum.TryParse<SonarChannel>(property.Name, true, out var channel))
            {
                continue;
            }

            var legacy = property.Value.Deserialize<LegacyMapping>(JsonOptions);
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.Device))
            {
                continue;
            }

            result.VolumeMappings[channel] = new MidiMapping(
                legacy.Device,
                legacy.Channel,
                MidiMessageType.ControlChange,
                legacy.Cc);
        }
        return result;
    }

    private static Dictionary<string, StoredMapping> ToStoredDictionary(
        IReadOnlyDictionary<SonarChannel, MidiMapping> mappings) =>
        mappings.ToDictionary(
            pair => SonarChannelMap.ToConfigKey(pair.Key),
            pair => new StoredMapping
            {
                Device = pair.Value.Device,
                Channel = pair.Value.Channel,
                MessageType = pair.Value.MessageType,
                Number = pair.Value.Number,
                ButtonBehavior = pair.Value.ButtonBehavior,
                ActivationValue = pair.Value.ActivationValue
            },
            StringComparer.OrdinalIgnoreCase);

    private static void CopyStoredMappings(
        IReadOnlyDictionary<string, StoredMapping> source,
        IDictionary<SonarChannel, MidiMapping> destination)
    {
        foreach (var pair in source)
        {
            if (!Enum.TryParse<SonarChannel>(pair.Key, true, out var channel) ||
                string.IsNullOrWhiteSpace(pair.Value.Device))
            {
                continue;
            }

            destination[channel] = new MidiMapping(
                pair.Value.Device,
                pair.Value.Channel,
                pair.Value.MessageType,
                pair.Value.Number,
                pair.Value.ButtonBehavior,
                pair.Value.ActivationValue);
        }
    }

    private sealed class StoredConfiguration
    {
        public int Version { get; init; } = CurrentVersion;
        public Dictionary<string, StoredMapping> VolumeMappings { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StoredMapping> MuteMappings { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredMapping
    {
        public string Device { get; init; } = string.Empty;
        public int Channel { get; init; }
        public MidiMessageType MessageType { get; init; }
        public int Number { get; init; }
        public MidiButtonBehavior ButtonBehavior { get; init; } = MidiButtonBehavior.Momentary;
        public int ActivationValue { get; init; } = 127;
    }

    private sealed class LegacyMapping
    {
        public string Device { get; init; } = string.Empty;
        public int Channel { get; init; }
        public int Cc { get; init; }
    }
}
