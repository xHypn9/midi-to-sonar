using System.Collections.Concurrent;
using SonarController.Core;
using SonarController.Midi;

namespace SonarController.Tests;

public sealed class MidiRouterTests
{
    [Fact]
    public async Task RoutesControlChangeToMappedVolume()
    {
        var store = new MidiMappingStore(CreateTemporaryPath());
        var configuration = new MidiMappingConfiguration();
        configuration.VolumeMappings[SonarChannel.Game] = new MidiMapping("WORLDE", 1, 19);
        await store.SaveAsync(configuration);

        var input = new FakeMidiInput();
        var sonar = new FakeSonarController();
        await using var router = new MidiRouter(sonar, input, store);
        await router.InitializeAsync();
        MidiVolumeChanged? immediateChange = null;
        router.VolumeChanged += (_, changed) => immediateChange = changed;

        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 19, 64));

        Assert.NotNull(immediateChange);
        Assert.Equal(SonarChannel.Game, immediateChange.Channel);
        Assert.Equal(64d / 127d, immediateChange.Volume, 6);

        await WaitUntilAsync(() => Math.Abs(sonar.GetVolume(SonarChannel.Game) - 64d / 127d) < 0.001);
    }

    [Fact]
    public async Task LearnsMomentaryCcAndIgnoresItsRelease()
    {
        var input = new FakeMidiInput();
        var sonar = new FakeSonarController();
        await using var router = new MidiRouter(
            sonar,
            input,
            new MidiMappingStore(CreateTemporaryPath()),
            muteLearnWindow: TimeSpan.FromMilliseconds(80));
        await router.InitializeAsync();
        var learned = WaitForLearnAsync(router);

        router.BeginLearn(SonarChannel.Game, MidiMappingTarget.Mute);
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 40, 127));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 40, 0));

        var mapping = await learned;
        Assert.Equal(MidiButtonBehavior.Momentary, mapping.Mapping.ButtonBehavior);
        Assert.Equal(127, mapping.Mapping.ActivationValue);

        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 40, 127));
        await WaitUntilAsync(() => sonar.GetMute(SonarChannel.Game));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 40, 0));
        await Task.Delay(30);
        Assert.True(sonar.GetMute(SonarChannel.Game));

        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 40, 127));
        await WaitUntilAsync(() => !sonar.GetMute(SonarChannel.Game));
    }

    [Fact]
    public async Task LearnsToggleCcAndTriggersOnAlternatingValues()
    {
        var input = new FakeMidiInput();
        var sonar = new FakeSonarController();
        await using var router = new MidiRouter(
            sonar,
            input,
            new MidiMappingStore(CreateTemporaryPath()),
            muteLearnWindow: TimeSpan.FromMilliseconds(40));
        await router.InitializeAsync();
        var learned = WaitForLearnAsync(router);

        router.BeginLearn(SonarChannel.Chat, MidiMappingTarget.Mute);
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 41, 127));

        var mapping = await learned;
        Assert.Equal(MidiButtonBehavior.Toggle, mapping.Mapping.ButtonBehavior);

        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 41, 0));
        await WaitUntilAsync(() => sonar.GetMute(SonarChannel.Chat));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 41, 0));
        await Task.Delay(30);
        Assert.True(sonar.GetMute(SonarChannel.Chat));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.ControlChange, 41, 127));
        await WaitUntilAsync(() => !sonar.GetMute(SonarChannel.Chat));
    }

    [Fact]
    public async Task LearnsNoteOnAndIgnoresNoteOff()
    {
        var input = new FakeMidiInput();
        var sonar = new FakeSonarController();
        await using var router = new MidiRouter(
            sonar,
            input,
            new MidiMappingStore(CreateTemporaryPath()),
            muteLearnWindow: TimeSpan.FromMilliseconds(80));
        await router.InitializeAsync();
        var learned = WaitForLearnAsync(router);

        router.BeginLearn(SonarChannel.Mic, MidiMappingTarget.Mute);
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.Note, 60, 100));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.Note, 60, 0));

        var mapping = await learned;
        Assert.Equal(MidiMessageType.Note, mapping.Mapping.MessageType);
        Assert.Equal(MidiButtonBehavior.Momentary, mapping.Mapping.ButtonBehavior);

        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.Note, 60, 100));
        await WaitUntilAsync(() => sonar.GetMute(SonarChannel.Mic));
        input.Raise(new MidiMessage("WORLDE", 1, MidiMessageType.Note, 60, 0));
        await Task.Delay(30);
        Assert.True(sonar.GetMute(SonarChannel.Mic));
    }

    [Fact]
    public void SynchronousShutdownDoesNotDeadlockSynchronizationContext()
    {
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;

        var shutdownThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                var router = new MidiRouter(
                    new FakeSonarController(),
                    new DelayedDisposeMidiInput(),
                    new MidiMappingStore(CreateTemporaryPath()));

                router.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true
        };

        shutdownThread.Start();

        Assert.True(
            finished.Wait(TimeSpan.FromSeconds(2)),
            "Synchronous application shutdown deadlocked on the UI synchronization context.");
        Assert.Null(failure);
    }

    private static Task<MidiMappingLearned> WaitForLearnAsync(MidiRouter router)
    {
        var completion = new TaskCompletionSource<MidiMappingLearned>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        router.MappingLearned += Handler;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        void Handler(object? sender, MidiMappingLearned learned)
        {
            router.MappingLearned -= Handler;
            completion.TrySetResult(learned);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected MIDI action did not complete.");
            }
            await Task.Delay(10);
        }
    }

    private static string CreateTemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        "SonarMidiControllerTests",
        Guid.NewGuid().ToString("N"),
        "midi-mappings.json");

    private sealed class FakeMidiInput : IMidiInput
    {
        public event EventHandler<MidiMessage>? MessageReceived;
        public event EventHandler<IReadOnlyList<string>>? DevicesChanged;

        public void Raise(MidiMessage message) => MessageReceived?.Invoke(this, message);
        public void Start() => DevicesChanged?.Invoke(this, new[] { "WORLDE" });
        public IReadOnlyList<string> GetConnectedDeviceNames() => new[] { "WORLDE" };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedDisposeMidiInput : IMidiInput
    {
        public event EventHandler<MidiMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<IReadOnlyList<string>>? DevicesChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public IReadOnlyList<string> GetConnectedDeviceNames() => Array.Empty<string>();

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(30).ConfigureAwait(false);
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private sealed class FakeSonarController : ISonarController
    {
        private readonly ConcurrentDictionary<SonarChannel, double> _volumes = new();
        private readonly ConcurrentDictionary<SonarChannel, bool> _mutes = new();

        public double GetVolume(SonarChannel channel) => _volumes.GetValueOrDefault(channel);
        public bool GetMute(SonarChannel channel) => _mutes.GetValueOrDefault(channel);

        public Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var channels = Enum.GetValues<SonarChannel>().ToDictionary(
                channel => channel,
                channel => new SonarChannelState(GetVolume(channel), GetMute(channel)));
            return Task.FromResult(new SonarSnapshot(channels, DateTimeOffset.UtcNow));
        }

        public Task<double> GetVolumeAsync(
            SonarChannel channel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(GetVolume(channel));

        public Task SetVolumeAsync(
            SonarChannel channel,
            double value,
            CancellationToken cancellationToken = default)
        {
            _volumes[channel] = value;
            return Task.CompletedTask;
        }

        public Task<bool> GetMuteAsync(
            SonarChannel channel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(GetMute(channel));

        public Task SetMuteAsync(
            SonarChannel channel,
            bool state,
            CancellationToken cancellationToken = default)
        {
            _mutes[channel] = state;
            return Task.CompletedTask;
        }

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
