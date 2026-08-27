using System.Collections.Concurrent;
using SonarController.Core;

namespace SonarController.Midi;

public sealed class MidiRouter : IAsyncDisposable
{
    private readonly ISonarController _sonar;
    private readonly IMidiInput _input;
    private readonly MidiMappingStore _store;
    private readonly ISonarLogger _logger;
    private readonly TimeSpan _muteLearnWindow;
    private readonly object _stateGate = new();
    private readonly Dictionary<SonarChannel, MidiMapping> _volumeMappings = new();
    private readonly Dictionary<SonarChannel, MidiMapping> _muteMappings = new();
    private readonly ConcurrentDictionary<(string Device, int Channel, MidiMessageType Type, int Number), int>
        _lastValues = new();
    private readonly SemaphoreSlim _mappingLock = new(1, 1);
    private readonly SemaphoreSlim _muteApplyLock = new(1, 1);
    private LearnRequest? _learnRequest;
    private PendingMuteLearn? _pendingMuteLearn;
    private CancellationTokenSource? _muteLearnDelay;
    private int _learnGeneration;
    private bool _disposed;

    public MidiRouter(
        ISonarController sonar,
        IMidiInput input,
        MidiMappingStore store,
        ISonarLogger? logger = null,
        TimeSpan? muteLearnWindow = null)
    {
        _sonar = sonar;
        _input = input;
        _store = store;
        _logger = logger ?? NullSonarLogger.Instance;
        _muteLearnWindow = muteLearnWindow ?? TimeSpan.FromMilliseconds(650);
    }

    public event EventHandler<MidiMappingLearned>? MappingLearned;
    public event EventHandler<MidiMuteChanged>? MuteChanged;

    public event EventHandler<MidiVolumeChanged>? VolumeChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(cancellationToken);
        lock (_stateGate)
        {
            foreach (var pair in loaded.VolumeMappings)
            {
                _volumeMappings[pair.Key] = pair.Value;
            }
            foreach (var pair in loaded.MuteMappings)
            {
                _muteMappings[pair.Key] = pair.Value;
            }
        }

        _input.MessageReceived += OnMessageReceived;
        _input.Start();
    }

    public MidiMappingConfiguration GetMappings()
    {
        lock (_stateGate)
        {
            return SnapshotMappingsLocked();
        }
    }

    public void BeginLearn(SonarChannel channel, MidiMappingTarget target)
    {
        CancellationTokenSource? previousDelay;
        lock (_stateGate)
        {
            _learnGeneration++;
            _learnRequest = new LearnRequest(channel, target, _learnGeneration);
            _pendingMuteLearn = null;
            previousDelay = _muteLearnDelay;
            _muteLearnDelay = null;
        }
        previousDelay?.Cancel();
        previousDelay?.Dispose();

        _logger.Info(
            "MIDI",
            $"{target.ToString().ToUpperInvariant()} learn waiting for {channel.ToString().ToUpperInvariant()}");
    }

    public void CancelLearn()
    {
        CancellationTokenSource? delay;
        lock (_stateGate)
        {
            _learnGeneration++;
            _learnRequest = null;
            _pendingMuteLearn = null;
            delay = _muteLearnDelay;
            _muteLearnDelay = null;
        }
        delay?.Cancel();
        delay?.Dispose();
    }

    public async Task<bool> ResetMappingAsync(
        SonarChannel channel,
        MidiMappingTarget target,
        CancellationToken cancellationToken = default)
    {
        await _mappingLock.WaitAsync(cancellationToken);
        MidiMapping? removedMapping = null;

        try
        {
            MidiMappingConfiguration snapshot;
            lock (_stateGate)
            {
                var mappings = target == MidiMappingTarget.Volume
                    ? _volumeMappings
                    : _muteMappings;
                if (!mappings.Remove(channel, out removedMapping))
                {
                    return false;
                }
                snapshot = SnapshotMappingsLocked();
            }

            try
            {
                await _store.SaveAsync(snapshot, cancellationToken);
            }
            catch
            {
                lock (_stateGate)
                {
                    var mappings = target == MidiMappingTarget.Volume
                        ? _volumeMappings
                        : _muteMappings;
                    mappings[channel] = removedMapping;
                }
                throw;
            }

            RemoveLastValue(removedMapping);
            _logger.Info(
                "MIDI",
                $"{channel.ToString().ToUpperInvariant()} {target.ToString().ToUpperInvariant()} mapping reset");
            return true;
        }
        finally
        {
            _mappingLock.Release();
        }
    }

    private void OnMessageReceived(object? sender, MidiMessage message)
    {
        if (TryHandleLearning(message))
        {
            return;
        }

        MidiMapping? volumeMapping = null;
        MidiMapping? muteMapping = null;
        SonarChannel volumeChannel = default;
        SonarChannel muteChannel = default;

        lock (_stateGate)
        {
            foreach (var pair in _volumeMappings)
            {
                if (Matches(pair.Value, message))
                {
                    volumeChannel = pair.Key;
                    volumeMapping = pair.Value;
                    break;
                }
            }
            foreach (var pair in _muteMappings)
            {
                if (Matches(pair.Value, message))
                {
                    muteChannel = pair.Key;
                    muteMapping = pair.Value;
                    break;
                }
            }
        }

        if (volumeMapping is not null && message.MessageType == MidiMessageType.ControlChange)
        {
            var key = ToKey(message);
            if (!_lastValues.TryGetValue(key, out var previous) || previous != message.Value)
            {
                _lastValues[key] = message.Value;
                var volume = MidiValueScaler.ToNormalized(message.Value);
                VolumeChanged?.Invoke(this, new MidiVolumeChanged(volumeChannel, volume));
                _ = ApplyVolumeAsync(volumeChannel, volume);
            }
        }

        if (muteMapping is not null)
        {
            _logger.Info(
                "MIDI",
                $"MUTE input {muteChannel.ToString().ToUpperInvariant()}: " +
                $"{DescribeMessage(muteMapping)}, value {message.Value}, " +
                $"{muteMapping.ButtonBehavior}, activation {muteMapping.ActivationValue}");
            if (ShouldTriggerMute(muteMapping, message))
            {
                _ = ApplyMuteAsync(muteChannel);
            }
        }
    }

    private bool TryHandleLearning(MidiMessage message)
    {
        LearnRequest? request;
        PendingMuteLearn? pending;
        lock (_stateGate)
        {
            request = _learnRequest;
            pending = _pendingMuteLearn;
        }

        if (pending is not null)
        {
            if (Matches(pending.Mapping, message) && message.Value != pending.FirstValue)
            {
                FinalizeMuteLearn(pending.Generation, MidiButtonBehavior.Momentary);
            }
            return true;
        }

        if (request is null)
        {
            return false;
        }

        if (request.Target == MidiMappingTarget.Volume)
        {
            if (message.MessageType != MidiMessageType.ControlChange)
            {
                return true;
            }

            var mapping = new MidiMapping(
                message.Device,
                message.Channel,
                message.MessageType,
                message.Number);
            lock (_stateGate)
            {
                if (_learnRequest?.Generation != request.Generation)
                {
                    return true;
                }
                _learnRequest = null;
                AssignMappingLocked(request.Channel, request.Target, mapping);
            }
            RemoveLastValue(mapping);
            _ = SaveLearnedMappingAsync(request.Channel, request.Target, mapping);
            return true;
        }

        StartMuteLearn(request, message);
        return true;
    }

    private void StartMuteLearn(LearnRequest request, MidiMessage message)
    {
        CancellationTokenSource delay;
        PendingMuteLearn pending;
        lock (_stateGate)
        {
            if (_learnRequest?.Generation != request.Generation)
            {
                return;
            }

            var mapping = new MidiMapping(
                message.Device,
                message.Channel,
                message.MessageType,
                message.Number,
                MidiButtonBehavior.Toggle,
                message.Value);
            pending = new PendingMuteLearn(
                request.Channel,
                mapping,
                message.Value,
                request.Generation);
            _learnRequest = null;
            _pendingMuteLearn = pending;
            delay = new CancellationTokenSource();
            _muteLearnDelay = delay;
        }

        _ = FinalizeMuteLearnAfterDelayAsync(pending.Generation, delay.Token);
    }

    private async Task FinalizeMuteLearnAfterDelayAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_muteLearnWindow, cancellationToken);
            FinalizeMuteLearn(generation, MidiButtonBehavior.Toggle);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FinalizeMuteLearn(int generation, MidiButtonBehavior behavior)
    {
        PendingMuteLearn? pending;
        CancellationTokenSource? delay;
        MidiMapping mapping;
        lock (_stateGate)
        {
            pending = _pendingMuteLearn;
            if (pending is null || pending.Generation != generation)
            {
                return;
            }

            mapping = pending.Mapping with
            {
                ButtonBehavior = behavior,
                ActivationValue = pending.FirstValue
            };
            _pendingMuteLearn = null;
            delay = _muteLearnDelay;
            _muteLearnDelay = null;
            AssignMappingLocked(pending.Channel, MidiMappingTarget.Mute, mapping);
        }

        delay?.Cancel();
        delay?.Dispose();
        RemoveLastValue(mapping);
        _ = SaveLearnedMappingAsync(pending.Channel, MidiMappingTarget.Mute, mapping);
    }

    private void AssignMappingLocked(
        SonarChannel channel,
        MidiMappingTarget target,
        MidiMapping mapping)
    {
        RemoveConflictsLocked(_volumeMappings, mapping);
        RemoveConflictsLocked(_muteMappings, mapping);
        var targetMappings = target == MidiMappingTarget.Volume
            ? _volumeMappings
            : _muteMappings;
        targetMappings[channel] = mapping;
    }

    private static void RemoveConflictsLocked(
        IDictionary<SonarChannel, MidiMapping> mappings,
        MidiMapping newMapping)
    {
        foreach (var key in mappings
                     .Where(pair => SameControl(pair.Value, newMapping))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            mappings.Remove(key);
        }
    }

    private async Task SaveLearnedMappingAsync(
        SonarChannel channel,
        MidiMappingTarget target,
        MidiMapping mapping)
    {
        await _mappingLock.WaitAsync();
        try
        {
            MidiMappingConfiguration snapshot;
            lock (_stateGate)
            {
                snapshot = SnapshotMappingsLocked();
            }
            await _store.SaveAsync(snapshot);
            _logger.Info(
                "MIDI",
                $"{channel.ToString().ToUpperInvariant()} {target.ToString().ToUpperInvariant()} learned " +
                $"{mapping.Device}, channel {mapping.Channel}, {DescribeMessage(mapping)}");
            MappingLearned?.Invoke(this, new MidiMappingLearned(channel, target, mapping));
        }
        catch (Exception ex)
        {
            _logger.Error("MIDI", "Unable to save MIDI mapping", ex);
        }
        finally
        {
            _mappingLock.Release();
        }
    }

    private async Task ApplyVolumeAsync(SonarChannel channel, double volume)
    {
        try
        {
            await _sonar.SetVolumeAsync(channel, volume);
        }
        catch (Exception ex)
        {
            _logger.Error("MIDI", $"Unable to update {channel} volume", ex);
        }
    }

    private async Task ApplyMuteAsync(SonarChannel channel)
    {
        await _muteApplyLock.WaitAsync();
        try
        {
            var current = await _sonar.GetMuteAsync(channel);
            var next = !current;
            await _sonar.SetMuteAsync(channel, next);
            MuteChanged?.Invoke(this, new MidiMuteChanged(channel, next));
        }
        catch (Exception ex)
        {
            _logger.Error("MIDI", $"Unable to toggle {channel} mute", ex);
        }
        finally
        {
            _muteApplyLock.Release();
        }
    }

    private bool ShouldTriggerMute(MidiMapping mapping, MidiMessage message)
    {
        var key = ToKey(message);
        var hadPrevious = _lastValues.TryGetValue(key, out var previous);
        if (hadPrevious && previous == message.Value)
        {
            return false;
        }
        _lastValues[key] = message.Value;

        return mapping.ButtonBehavior == MidiButtonBehavior.Toggle ||
               (message.Value == mapping.ActivationValue &&
                (!hadPrevious || previous != mapping.ActivationValue));
    }

    private MidiMappingConfiguration SnapshotMappingsLocked()
    {
        var snapshot = new MidiMappingConfiguration();
        foreach (var pair in _volumeMappings)
        {
            snapshot.VolumeMappings[pair.Key] = pair.Value;
        }
        foreach (var pair in _muteMappings)
        {
            snapshot.MuteMappings[pair.Key] = pair.Value;
        }
        return snapshot;
    }

    private void RemoveLastValue(MidiMapping mapping) =>
        _lastValues.TryRemove(
            (mapping.Device, mapping.Channel, mapping.MessageType, mapping.Number),
            out _);

    private static bool Matches(MidiMapping mapping, MidiMessage message) =>
        string.Equals(mapping.Device, message.Device, StringComparison.OrdinalIgnoreCase) &&
        mapping.Channel == message.Channel &&
        mapping.MessageType == message.MessageType &&
        mapping.Number == message.Number;

    private static bool SameControl(MidiMapping left, MidiMapping right) =>
        string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase) &&
        left.Channel == right.Channel &&
        left.MessageType == right.MessageType &&
        left.Number == right.Number;

    private static (string Device, int Channel, MidiMessageType Type, int Number) ToKey(
        MidiMessage message) =>
        (message.Device, message.Channel, message.MessageType, message.Number);

    private static string DescribeMessage(MidiMapping mapping) =>
        mapping.MessageType == MidiMessageType.ControlChange
            ? $"CC {mapping.Number}"
            : $"Note {mapping.Number}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancelLearn();
        _input.MessageReceived -= OnMessageReceived;
        await _input.DisposeAsync().ConfigureAwait(false);
        _mappingLock.Dispose();
        _muteApplyLock.Dispose();
    }

    private sealed record LearnRequest(
        SonarChannel Channel,
        MidiMappingTarget Target,
        int Generation);

    private sealed record PendingMuteLearn(
        SonarChannel Channel,
        MidiMapping Mapping,
        int FirstValue,
        int Generation);
}
