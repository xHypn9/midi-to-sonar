using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using SonarController.Core;

namespace SonarController.Midi;

public sealed class MidiInputService : IMidiInput
{
    private readonly ISonarLogger _logger;
    private readonly Dictionary<string, InputDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _watchTask;
    private int _lastReportedCount = -1;

    public MidiInputService(ISonarLogger? logger = null)
    {
        _logger = logger ?? NullSonarLogger.Instance;
    }

    public event EventHandler<MidiMessage>? MessageReceived;
    public event EventHandler<IReadOnlyList<string>>? DevicesChanged;

    public void Start()
    {
        if (_lifetime is not null)
        {
            return;
        }
        _lifetime = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchDevicesAsync(_lifetime.Token));
    }

    public IReadOnlyList<string> GetConnectedDeviceNames()
    {
        lock (_gate)
        {
            return _devices.Keys.OrderBy(name => name).ToArray();
        }
    }

    private async Task WatchDevicesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                RefreshDevices();
            }
            catch (Exception ex)
            {
                _logger.Error("MIDI", "Device scan failed", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RefreshDevices()
    {
        var discovered = InputDevice.GetAll().ToList();
        var availableNames = new HashSet<string>(
            discovered.Select(device => device.Name),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;

        lock (_gate)
        {
            foreach (var removed in _devices.Keys.Where(name => !availableNames.Contains(name)).ToList())
            {
                DisposeDevice(_devices[removed]);
                _devices.Remove(removed);
                changed = true;
            }

            foreach (var candidate in discovered)
            {
                if (_devices.ContainsKey(candidate.Name))
                {
                    candidate.Dispose();
                    continue;
                }

                try
                {
                    candidate.EventReceived += DeviceOnEventReceived;
                    candidate.StartEventsListening();
                    _devices[candidate.Name] = candidate;
                    changed = true;
                    _logger.Info("MIDI", $"Connected: {candidate.Name}");
                }
                catch (Exception ex)
                {
                    _logger.Error("MIDI", $"Unable to open {candidate.Name}", ex);
                    DisposeDevice(candidate);
                }
            }

            if (_devices.Count != _lastReportedCount)
            {
                _lastReportedCount = _devices.Count;
                changed = true;
                if (_devices.Count == 0)
                {
                    _logger.Info("MIDI", "No device connected");
                }
            }
        }

        if (changed)
        {
            DevicesChanged?.Invoke(this, GetConnectedDeviceNames());
        }
    }

    private void DeviceOnEventReceived(object? sender, MidiEventReceivedEventArgs args)
    {
        if (sender is not InputDevice input)
        {
            return;
        }

        MidiMessage? message = args.Event switch
        {
            ControlChangeEvent controlChange => new MidiMessage(
                input.Name,
                (int)controlChange.Channel + 1,
                MidiMessageType.ControlChange,
                (int)controlChange.ControlNumber,
                (int)controlChange.ControlValue),
            NoteOnEvent noteOn => new MidiMessage(
                input.Name,
                (int)noteOn.Channel + 1,
                MidiMessageType.Note,
                (int)noteOn.NoteNumber,
                (int)noteOn.Velocity),
            NoteOffEvent noteOff => new MidiMessage(
                input.Name,
                (int)noteOff.Channel + 1,
                MidiMessageType.Note,
                (int)noteOff.NoteNumber,
                0),
            _ => null
        };

        if (message is not null)
        {
            MessageReceived?.Invoke(this, message);
        }
    }

    private static void DisposeDevice(InputDevice device)
    {
        try
        {
            device.StopEventsListening();
        }
        catch
        {
        }
        device.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null)
        {
            _lifetime.Cancel();
        }
        if (_watchTask is not null)
        {
            try
            {
                await _watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                device.EventReceived -= DeviceOnEventReceived;
                DisposeDevice(device);
            }
            _devices.Clear();
        }
        _lifetime?.Dispose();
    }
}
