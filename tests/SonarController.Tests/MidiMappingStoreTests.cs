using SonarController.Core;
using SonarController.Midi;

namespace SonarController.Tests;

public sealed class MidiMappingStoreTests
{
    [Fact]
    public async Task LoadsLegacyVolumeMappingsWithoutLosingBindings()
    {
        var path = CreateTemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "game": {
                "device": "WORLDE",
                "channel": 1,
                "cc": 19
              }
            }
            """);

        var loaded = await new MidiMappingStore(path).LoadAsync();

        var mapping = Assert.Single(loaded.VolumeMappings);
        Assert.Equal(SonarChannel.Game, mapping.Key);
        Assert.Equal(MidiMessageType.ControlChange, mapping.Value.MessageType);
        Assert.Equal(19, mapping.Value.Number);
        Assert.Empty(loaded.MuteMappings);
    }

    [Fact]
    public async Task SavesAndLoadsSeparateVolumeAndMuteMappings()
    {
        var path = CreateTemporaryPath();
        var store = new MidiMappingStore(path);
        var mappings = new MidiMappingConfiguration();
        mappings.VolumeMappings[SonarChannel.Game] = new MidiMapping("WORLDE", 1, 19);
        mappings.MuteMappings[SonarChannel.Game] = new MidiMapping(
            "WORLDE",
            1,
            MidiMessageType.Note,
            42,
            MidiButtonBehavior.Momentary,
            100);

        await store.SaveAsync(mappings);
        var loaded = await store.LoadAsync();

        Assert.Equal(mappings.VolumeMappings[SonarChannel.Game], loaded.VolumeMappings[SonarChannel.Game]);
        Assert.Equal(mappings.MuteMappings[SonarChannel.Game], loaded.MuteMappings[SonarChannel.Game]);
        Assert.Contains("\"version\": 2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SavingAnEmptySetClearsExistingMappings()
    {
        var path = CreateTemporaryPath();
        var store = new MidiMappingStore(path);
        var mappings = new MidiMappingConfiguration();
        mappings.VolumeMappings[SonarChannel.Game] = new MidiMapping("Test Controller", 1, 16);

        await store.SaveAsync(mappings);
        await store.SaveAsync(new MidiMappingConfiguration());
        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.VolumeMappings);
        Assert.Empty(loaded.MuteMappings);
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static string CreateTemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        "SonarMidiControllerTests",
        Guid.NewGuid().ToString("N"),
        "midi-mappings.json");
}
