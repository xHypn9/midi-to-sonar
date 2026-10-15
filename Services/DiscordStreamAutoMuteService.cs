using System.Runtime.InteropServices;
using SonarController.Core;
using static SonarMidiController.App.Services.CoreAudioInterop;

namespace SonarMidiController.App.Services;

public sealed class DiscordStreamAutoMuteService : IDisposable
{
    public const string EndpointName = "SteelSeries Sonar - Microphone";
    private readonly ISonarLogger _logger;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();
    private Thread? _worker;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private DateTime _nextErrorLog;

    public DiscordStreamAutoMuteService(ISonarLogger logger) => _logger = logger;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled || _worker is not null) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "DiscordAutoMute" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
    }

    private void Run()
    {
        var initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            initialized = true;
            do
            {
                try
                {
                    if (_disposed) break;
                    if (_enabled) Scan();
                }
                catch (Exception ex) { LogError(ex); }
            } while (!_stop.WaitOne(1000));
        }
        catch (Exception ex) { LogError(ex); }
        finally { if (initialized) CoUninitialize(); }
    }

    private void Scan()
    {
        IDeviceEnumerator? enumerator = null;
        IDeviceCollection? devices = null;
        try
        {
            enumerator = (IDeviceEnumerator)new DeviceEnumerator();
            // eRender only: NEVER enumerate/mute capture sessions (Discord voice input).
            enumerator.EnumAudioEndpoints(0, 1, out devices);
            devices.GetCount(out var count);
            for (uint index = 0; index < count; index++)
            {
                IDevice? device = null;
                try
                {
                    devices.Item(index, out device);
                    if (MatchesEndpoint(ReadName(device))) ScanSessions(device);
                }
                catch (Exception ex) { LogError(ex); }
                finally { Release(device); }
            }
        }
        finally { Release(devices); Release(enumerator); }
    }

    public static bool MatchesEndpoint(string? name) =>
        string.Equals(name, EndpointName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, EndpointName + " (SteelSeries Sonar Virtual Audio Device)",
            StringComparison.OrdinalIgnoreCase);

    private static string? ReadName(IDevice device)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            device.OpenPropertyStore(0, out store);
            var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            store.GetValue(ref key, out value);
            return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally { PropVariantClear(ref value); Release(store); }
    }

    private void ScanSessions(IDevice device)
    {
        object? managerObject = null;
        ISessionEnumerator? sessions = null;
        try
        {
            var iid = SessionManagerId;
            device.Activate(ref iid, 23, 0, out managerObject);
            ((ISessionManager)managerObject).GetSessionEnumerator(out sessions);
            sessions.GetCount(out var count);
            for (var index = 0; index < count; index++)
            {
                if (!_enabled || _disposed) break;
                object? session = null;
                try
                {
                    sessions.GetSession(index, out session);
                    var control = (ISessionControl2)session;
                    control.GetState(out var state);
                    // AUDCLNT_S_NO_SINGLE_PROCESS is positive: reject it as ambiguous.
                    if (state == 2 || control.GetProcessId(out var pid) != 0 || pid == 0) continue;
                    var volume = (ISimpleVolume)session;
                    volume.GetMute(out var muted);
                    if (muted || !DiscordProcessIdentity.IsDiscord(checked((int)pid))) continue;
                    // Recheck ownership after process/signature inspection (process may have exited).
                    if (control.GetProcessId(out var currentPid) != 0 || currentPid != pid) continue;
                    var context = Guid.Empty;
                    lock (_gate)
                    {
                        if (!_enabled || _disposed) return;
                        volume.SetMute(true, ref context);
                    }
                    try { _logger.Info("DiscordAutoMute", $"Muted Discord session PID {pid} on {EndpointName}"); }
                    catch { /* Logging cannot interrupt MIDI or audio cleanup. */ }
                }
                catch (Exception ex) { LogError(ex); }
                finally { Release(session); }
            }
        }
        finally { Release(sessions); Release(managerObject); }
    }

    private void LogError(Exception ex)
    {
        if (DateTime.UtcNow < _nextErrorLog) return;
        _nextErrorLog = DateTime.UtcNow.AddMinutes(1);
        try { _logger.Error("DiscordAutoMute", "Audio scan failed; will retry", ex); }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _stop.Set();
        }
        _worker?.Join();
        _stop.Dispose();
        // Never restore/unmute sessions on disable or shutdown.
    }
}
