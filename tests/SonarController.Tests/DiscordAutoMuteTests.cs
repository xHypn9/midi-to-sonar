using System.Runtime.InteropServices;
using SonarMidiController.App.Services;
using static SonarMidiController.App.Services.CoreAudioInterop;

namespace SonarController.Tests;

public sealed class DiscordAutoMuteTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public DiscordAutoMuteTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("SteelSeries Sonar - Microphone", true)]
    [InlineData("SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", true)]
    [InlineData("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", false)]
    [InlineData("SteelSeries Sonar - Microphone (Other Device)", false)]
    [InlineData("steelseries sonar - microphone", true)]
    [InlineData("SteelSeries Sonar - Chat", false)]
    [InlineData("SteelSeries Sonar - Gaming", false)]
    [InlineData("SteelSeries Sonar - Media", false)]
    [InlineData("SteelSeries Sonar - Aux", false)]
    [InlineData("Microphone (SteelSeries Sonar - Microphone)", false)]
    [InlineData(null, false)]
    public void EndpointFilterIsExact(string? name, bool expected) =>
        Assert.Equal(expected, DiscordStreamAutoMuteService.MatchesEndpoint(name));

    [Theory]
    [InlineData("Discord.exe", true)]
    [InlineData("DiscordPTB.exe", true)]
    [InlineData("DiscordCanary.exe", true)]
    [InlineData("DiscordHelper.exe", false)]
    [InlineData("Discord.exe.bak", false)]
    [InlineData("Update.exe", false)]
    public void ProcessNamesAreRestricted(string name, bool expected) =>
        Assert.Equal(expected, DiscordProcessIdentity.IsDiscordExecutableName(name));

    [Fact]
    public void TestRunnerIsNotDiscord() => Assert.False(DiscordProcessIdentity.IsDiscord(Environment.ProcessId));

    [Fact]
    public void LegacySettingsDefaultToEnabledAndExplicitFalseSurvivesSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var store = new AppSettingsStore(directory);
            Assert.True(store.Load().DiscordStreamAutoMute);
            File.WriteAllText(store.SettingsPath, "{\"showWidget\":true}");
            var settings = store.Load();
            Assert.True(settings.DiscordStreamAutoMute);
            settings.DiscordStreamAutoMute = false;
            store.Save(settings);
            Assert.False(store.Load().DiscordStreamAutoMute);
            Assert.True(store.Load().ShowWidget);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NativeRenderEnumerationAndSessionQueriesAreReadOnly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            IDeviceEnumerator? enumerator = null;
            IDeviceCollection? devices = null;
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
                initialized = true;
                enumerator = (IDeviceEnumerator)new DeviceEnumerator();
                enumerator.EnumAudioEndpoints(0, 1, out devices);
                devices.GetCount(out var count);
                _output.WriteLine($"Active render endpoints: {count}");
                for (uint i = 0; i < count; i++)
                {
                    IDevice? device = null;
                    IPropertyStore? store = null;
                    object? manager = null;
                    ISessionEnumerator? sessions = null;
                    PropVariant value = default;
                    try
                    {
                        devices.Item(i, out device);
                        device.OpenPropertyStore(0, out store);
                        var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                        store.GetValue(ref key, out value);
                        Assert.Equal(31, value.Type);
                        Assert.False(string.IsNullOrEmpty(Marshal.PtrToStringUni(value.Pointer)));
                        _output.WriteLine($"Endpoint: {Marshal.PtrToStringUni(value.Pointer)}");
                        var iid = SessionManagerId;
                        device.Activate(ref iid, 23, 0, out manager);
                        ((ISessionManager)manager).GetSessionEnumerator(out sessions);
                        sessions.GetCount(out var sessionCount);
                        for (var j = 0; j < sessionCount; j++)
                        {
                            object? session = null;
                            try
                            {
                                sessions.GetSession(j, out session);
                                var control = (ISessionControl2)session;
                                control.GetState(out var state);
                                Assert.InRange(state, 0, 2);
                                Marshal.ThrowExceptionForHR(control.GetProcessId(out _));
                                control.GetProcessId(out var processId);
                                _output.WriteLine($"Session PID: {processId}");
                                control.GetSessionInstanceIdentifier(out var id);
                                Assert.False(string.IsNullOrEmpty(id));
                                ((ISimpleVolume)session).GetMute(out var muted);
                                _output.WriteLine($"Session mute: {muted}");
                            }
                            finally { Release(session); }
                        }
                    }
                    finally
                    {
                        PropVariantClear(ref value);
                        Release(sessions); Release(manager); Release(store); Release(device);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Release(devices); Release(enumerator);
                if (initialized) CoUninitialize();
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Null(failure);
    }
}
