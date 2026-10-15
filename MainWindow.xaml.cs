using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SonarController.Core;
using SonarController.Midi;
using SonarMidiController.App.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace SonarMidiController.App;

public partial class MainWindow : Window
{
    private readonly FileSonarLogger _logger;
    private readonly DiscordStreamAutoMuteService _discordAutoMute;
    private readonly SteelSeriesSonarController _controller;
    private readonly MidiInputService _midiInput;
    private readonly MidiRouter _midiRouter;
    private readonly AppSettingsStore _settingsStore;
    private readonly bool _startMinimizedOnLoad;
    private readonly bool _showWidgetOnLoad;
    private readonly Drawing.Icon? _trayIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _midiVolumeUiGate = new();
    private readonly Dictionary<SonarChannel, CancellationTokenSource> _volumeDebounce = new();
    private readonly Dictionary<SonarChannel, double> _pendingMidiVolumes = new();
    private readonly Dictionary<SonarChannel, long> _midiVolumeFreshUntil = new();
    private Dictionary<SonarChannel, Slider> _sliders = null!;
    private Dictionary<SonarChannel, TextBlock> _valueLabels = null!;
    private Dictionary<SonarChannel, ToggleButton> _muteButtons = null!;
    private Dictionary<SonarChannel, TextBlock> _volumeMappingLabels = null!;
    private Dictionary<SonarChannel, TextBlock> _muteMappingLabels = null!;
    private WidgetWindow? _widgetWindow;
    private SonarSnapshot? _lastSnapshot;
    private bool _uiReady;
    private bool _suppressUiEvents;
    private bool? _connected;
    private bool _closed;
    private bool _allowExit;
    private bool _midiVolumeUiUpdateScheduled;

    public MainWindow(string? dataDirectory = null)
    {
        InitializeComponent();
        Title = RuntimeProfile.DisplayName;
        CreditsText.Text = $"© SimoWork {DateTime.Now.Year}";

        var effectiveDataDirectory = dataDirectory ?? RuntimeProfile.DataDirectory;
        _logger = new FileSonarLogger(Path.Combine(
            effectiveDataDirectory,
            "logs",
            "sonar-controller.log"));
        _settingsStore = new AppSettingsStore(effectiveDataDirectory);
        var settings = _settingsStore.Load();
        _discordAutoMute = new DiscordStreamAutoMuteService(_logger);
        _discordAutoMute.SetEnabled(settings.DiscordStreamAutoMute);
        var arguments = Environment.GetCommandLineArgs();
        var forceShow = arguments.Any(argument =>
            argument.Equals("--show", StringComparison.OrdinalIgnoreCase));
        _startMinimizedOnLoad = !forceShow &&
            (settings.StartMinimized || arguments.Any(argument =>
                argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
        _showWidgetOnLoad = settings.ShowWidget;

        if (_startMinimizedOnLoad)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }

        if (!RuntimeProfile.IsDiagnostic && settings.StartWithWindows)
        {
            try
            {
                new StartupRegistrationService().SetEnabled(true);
            }
            catch (Exception ex)
            {
                _logger.Error("Settings", "Unable to refresh Windows startup registration", ex);
            }
        }

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Apri", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Esci", null, (_, _) => ExitFromTray());

        _trayIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            Text = RuntimeProfile.DisplayName,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();

        _controller = new SteelSeriesSonarController(_logger);
        _midiInput = new MidiInputService(_logger);
        _midiRouter = new MidiRouter(
            _controller,
            _midiInput,
            new MidiMappingStore(Path.Combine(effectiveDataDirectory, "midi-mappings.json")),
            _logger);

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pollTimer.Tick += async (_, _) => await RefreshSnapshotAsync();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        System.Windows.Application.Current.SessionEnding += OnSessionEnding;
        _midiInput.DevicesChanged += OnMidiDevicesChanged;
        _midiRouter.MappingLearned += OnMappingLearned;
        _midiRouter.MuteChanged += OnMidiMuteChanged;
        _midiRouter.VolumeChanged += OnMidiVolumeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildControlMaps();
        _suppressUiEvents = true;

        try
        {
            await _midiRouter.InitializeAsync();
            RefreshMappingLabels(_midiRouter.GetMappings());
        }
        catch (Exception ex)
        {
            _logger.Error("MIDI", "MIDI service startup failed", ex);
            MidiStatusText.Text = "Unavailable";
        }

        await RefreshSnapshotAsync();
        if (_showWidgetOnLoad)
        {
            ShowWidget();
        }
        _uiReady = true;
        _suppressUiEvents = false;
        _pollTimer.Start();

        if (_startMinimizedOnLoad)
        {
            HideToTray();
        }
    }

    private void BuildControlMaps()
    {
        _sliders = new()
        {
            [SonarChannel.Master] = MasterSlider,
            [SonarChannel.Game] = GameSlider,
            [SonarChannel.Chat] = ChatSlider,
            [SonarChannel.Media] = MediaSlider,
            [SonarChannel.Aux] = AuxSlider,
            [SonarChannel.Mic] = MicSlider
        };
        _valueLabels = new()
        {
            [SonarChannel.Master] = MasterValue,
            [SonarChannel.Game] = GameValue,
            [SonarChannel.Chat] = ChatValue,
            [SonarChannel.Media] = MediaValue,
            [SonarChannel.Aux] = AuxValue,
            [SonarChannel.Mic] = MicValue
        };
        _muteButtons = new()
        {
            [SonarChannel.Master] = MasterMute,
            [SonarChannel.Game] = GameMute,
            [SonarChannel.Chat] = ChatMute,
            [SonarChannel.Media] = MediaMute,
            [SonarChannel.Aux] = AuxMute,
            [SonarChannel.Mic] = MicMute
        };
        _volumeMappingLabels = new()
        {
            [SonarChannel.Master] = MasterVolumeMapping,
            [SonarChannel.Game] = GameVolumeMapping,
            [SonarChannel.Chat] = ChatVolumeMapping,
            [SonarChannel.Media] = MediaVolumeMapping,
            [SonarChannel.Aux] = AuxVolumeMapping,
            [SonarChannel.Mic] = MicVolumeMapping
        };
        _muteMappingLabels = new()
        {
            [SonarChannel.Master] = MasterMuteMapping,
            [SonarChannel.Game] = GameMuteMapping,
            [SonarChannel.Chat] = ChatMuteMapping,
            [SonarChannel.Media] = MediaMuteMapping,
            [SonarChannel.Aux] = AuxMuteMapping,
            [SonarChannel.Mic] = MicMuteMapping
        };
    }

    private async Task RefreshSnapshotAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var snapshot = await _controller.GetSnapshotAsync();
            _suppressUiEvents = true;

            foreach (var channel in Enum.GetValues<SonarChannel>())
            {
                var state = snapshot[channel];
                var slider = _sliders[channel];
                var midiValueIsFresh = IsMidiVolumeFresh(channel);
                if (!midiValueIsFresh &&
                    !slider.IsMouseCaptureWithin &&
                    !_volumeDebounce.ContainsKey(channel))
                {
                    slider.Value = state.Volume * 100d;
                    _valueLabels[channel].Text = $"{state.Volume * 100d:0}%";
                    _widgetWindow?.UpdateVolume(channel, state.Volume);
                }
                SetMuteAppearance(_muteButtons[channel], state.IsMuted);
                _widgetWindow?.UpdateMute(channel, state.IsMuted);
            }

            _lastSnapshot = snapshot;

            SetConnectionStatus(true);
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
        }
        finally
        {
            _suppressUiEvents = false;
            _refreshLock.Release();
        }
    }

    private async void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || !TryGetChannel(slider.Tag, out var channel))
        {
            return;
        }

        if (_valueLabels is not null && _valueLabels.TryGetValue(channel, out var label))
        {
            label.Text = $"{slider.Value:0}%";
        }

        if (!_uiReady || _suppressUiEvents)
        {
            return;
        }

        var requestedValue = e.NewValue / 100d;
        if (_volumeDebounce.Remove(channel, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var debounce = new CancellationTokenSource();
        _volumeDebounce[channel] = debounce;
        try
        {
            await Task.Delay(60, debounce.Token);
            await _controller.SetVolumeAsync(channel, requestedValue, debounce.Token);
            SetConnectionStatus(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
        }
        finally
        {
            if (_volumeDebounce.TryGetValue(channel, out var current) &&
                ReferenceEquals(current, debounce))
            {
                _volumeDebounce.Remove(channel);
                debounce.Dispose();
            }
        }
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_uiReady ||
            sender is not ToggleButton button ||
            !TryGetChannel(button.Tag, out var channel))
        {
            return;
        }

        var muted = button.IsChecked == true;
        SetMuteAppearance(button, muted);
        try
        {
            await _controller.SetMuteAsync(channel, muted);
            SetConnectionStatus(true);
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
            await RefreshSnapshotAsync();
        }
    }

    private void LearnVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || !TryGetChannel(button.Tag, out var channel))
        {
            return;
        }

        _midiRouter.BeginLearn(channel, MidiMappingTarget.Volume);
        MidiStatusText.Text =
            $"Learning {channel.ToString().ToUpperInvariant()} volume: move the MIDI fader...";
        SetMidiStatusColor(WpfColor.FromRgb(96, 165, 250));
    }

    private void LearnMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || !TryGetChannel(button.Tag, out var channel))
        {
            return;
        }

        _midiRouter.BeginLearn(channel, MidiMappingTarget.Mute);
        MidiStatusText.Text =
            $"Learning {channel.ToString().ToUpperInvariant()} mute: press and release the MIDI button...";
        SetMidiStatusColor(WpfColor.FromRgb(96, 165, 250));
    }

    private async void ResetVolumeMappingButton_Click(object sender, RoutedEventArgs e) =>
        await ResetMappingAsync(sender, MidiMappingTarget.Volume);

    private async void ResetMuteMappingButton_Click(object sender, RoutedEventArgs e) =>
        await ResetMappingAsync(sender, MidiMappingTarget.Mute);

    private async Task ResetMappingAsync(object sender, MidiMappingTarget target)
    {
        if (sender is not WpfButton button || !TryGetChannel(button.Tag, out var channel))
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var removed = await _midiRouter.ResetMappingAsync(channel, target);
            RefreshMappingLabels(_midiRouter.GetMappings());
            MidiStatusText.Text = removed
                ? $"{channel.ToString().ToUpperInvariant()} {target.ToString().ToUpperInvariant()} mapping reset"
                : $"{channel.ToString().ToUpperInvariant()} {target.ToString().ToUpperInvariant()} was not mapped";
            SetMidiStatusColor(
                removed
                    ? WpfColor.FromRgb(74, 222, 128)
                    : WpfColor.FromRgb(156, 163, 175));
        }
        catch (Exception ex)
        {
            _logger.Error("MIDI", $"Unable to reset {channel} {target} mapping", ex);
            MidiStatusText.Text =
                $"Unable to reset {channel.ToString().ToUpperInvariant()} {target.ToString().ToUpperInvariant()} mapping";
            SetMidiStatusColor(WpfColor.FromRgb(248, 113, 113));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_settingsStore)
        {
            Owner = this
        };
        if (settingsWindow.ShowDialog() == true)
        {
            ApplyWidgetSettings(settingsWindow.SavedSettings);
            _discordAutoMute.SetEnabled(settingsWindow.SavedSettings.DiscordStreamAutoMute);
        }
    }

    private void OnMidiDevicesChanged(object? sender, IReadOnlyList<string> devices)
    {
        Dispatcher.Invoke(() =>
        {
            MidiStatusText.Text = devices.Count == 0
                ? "No device connected"
                : string.Join(", ", devices);
            SetMidiStatusColor(
                devices.Count == 0
                    ? WpfColor.FromRgb(156, 163, 175)
                    : WpfColor.FromRgb(74, 222, 128));
        });
    }

    private void OnMappingLearned(object? sender, MidiMappingLearned learned)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshMappingLabels(_midiRouter.GetMappings());
            MidiStatusText.Text =
                $"Mapped {learned.Channel.ToString().ToUpperInvariant()} " +
                $"{learned.Target.ToString().ToUpperInvariant()} to {DescribeMessage(learned.Mapping)}";
            if (learned.Target == MidiMappingTarget.Mute)
            {
                MidiStatusText.Text += $" ({learned.Mapping.ButtonBehavior})";
            }
            SetMidiStatusColor(WpfColor.FromRgb(74, 222, 128));
        });
    }

    private void OnMidiMuteChanged(object? sender, MidiMuteChanged changed)
    {
        Dispatcher.Invoke(() =>
        {
            if (_muteButtons.TryGetValue(changed.Channel, out var button))
            {
                _suppressUiEvents = true;
                SetMuteAppearance(button, changed.IsMuted);
                _suppressUiEvents = false;
            }
            _widgetWindow?.UpdateMute(changed.Channel, changed.IsMuted);
        });
    }

    private void OnMidiVolumeChanged(object? sender, MidiVolumeChanged changed)
    {
        lock (_midiVolumeUiGate)
        {
            _pendingMidiVolumes[changed.Channel] = changed.Volume;
            _midiVolumeFreshUntil[changed.Channel] = Environment.TickCount64 + 200;
            if (_midiVolumeUiUpdateScheduled)
            {
                return;
            }
            _midiVolumeUiUpdateScheduled = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, ApplyPendingMidiVolumes);
    }

    private void ApplyPendingMidiVolumes()
    {
        Dictionary<SonarChannel, double> pending;
        lock (_midiVolumeUiGate)
        {
            pending = new Dictionary<SonarChannel, double>(_pendingMidiVolumes);
            _pendingMidiVolumes.Clear();
            _midiVolumeUiUpdateScheduled = false;
        }

        _suppressUiEvents = true;
        try
        {
            foreach (var (channel, volume) in pending)
            {
                if (_sliders.TryGetValue(channel, out var slider))
                {
                    slider.Value = volume * 100d;
                }
                if (_valueLabels.TryGetValue(channel, out var label))
                {
                    label.Text = $"{volume * 100d:0}%";
                }
                _widgetWindow?.UpdateVolume(channel, volume);
            }
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private bool IsMidiVolumeFresh(SonarChannel channel)
    {
        lock (_midiVolumeUiGate)
        {
            return _midiVolumeFreshUntil.TryGetValue(channel, out var freshUntil) &&
                Environment.TickCount64 < freshUntil;
        }
    }

    private void ApplyWidgetSettings(AppSettings settings)
    {
        if (settings.ShowWidget)
        {
            ShowWidget();
            _widgetWindow?.ApplyVisualSettings(settings);
            return;
        }

        if (_widgetWindow is not null)
        {
            _widgetWindow.CloseForApplicationExit();
            _widgetWindow = null;
        }
    }

    private void ShowWidget()
    {
        if (_widgetWindow is null)
        {
            _widgetWindow = new WidgetWindow(_settingsStore, _logger);
        }

        if (!_widgetWindow.IsVisible)
        {
            _widgetWindow.Show();
        }
        if (_lastSnapshot is not null)
        {
            _widgetWindow.UpdateSnapshot(_lastSnapshot);
        }
    }

    private void RefreshMappingLabels(MidiMappingConfiguration mappings)
    {
        foreach (var channel in Enum.GetValues<SonarChannel>())
        {
            _volumeMappingLabels[channel].Text = mappings.VolumeMappings.TryGetValue(channel, out var volume)
                ? DescribeMapping(volume, false)
                : "Not mapped";
            _muteMappingLabels[channel].Text = mappings.MuteMappings.TryGetValue(channel, out var mute)
                ? DescribeMapping(mute, true)
                : "Not mapped";
        }
    }

    private static string DescribeMapping(MidiMapping mapping, bool includeBehavior)
    {
        var description =
            $"{mapping.Device} | Ch {mapping.Channel} | {DescribeMessage(mapping)}";
        return includeBehavior
            ? $"{description} | {mapping.ButtonBehavior}"
            : description;
    }

    private static string DescribeMessage(MidiMapping mapping) =>
        mapping.MessageType == MidiMessageType.ControlChange
            ? $"CC {mapping.Number}"
            : $"Note {mapping.Number}";

    private void SetConnectionStatus(bool connected, string? error = null)
    {
        if (_connected != connected)
        {
            _connected = connected;
            _logger.Info("Sonar", connected ? "GUI connected" : "GUI disconnected");
        }

        ConnectionText.Text = connected ? "Sonar: CONNECTED" : "Sonar: DISCONNECTED";
        ConnectionDot.Fill = new SolidColorBrush(
            connected ? WpfColor.FromRgb(74, 222, 128) : WpfColor.FromRgb(248, 113, 113));
        LastErrorText.Text = connected ? string.Empty : error ?? "Unavailable";
    }

    private void SetMidiStatusColor(WpfColor color) =>
        MidiStatusText.Foreground = new SolidColorBrush(color);

    private static void SetMuteAppearance(ToggleButton button, bool muted)
    {
        button.IsChecked = muted;
        button.Content = muted ? "UNMUTE" : "MUTE";
        button.Background = new SolidColorBrush(
            muted ? WpfColor.FromRgb(153, 27, 27) : WpfColor.FromRgb(48, 54, 64));
    }

    private static bool TryGetChannel(object? tag, out SonarChannel channel) =>
        Enum.TryParse(tag?.ToString(), true, out channel);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public void RestoreFromSecondaryInstance() => ShowFromTray();

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void ExitFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _allowExit = true;
            _notifyIcon.Visible = false;
            Close();
        });
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _allowExit = true;
        _widgetWindow?.CloseForApplicationExit();
        _widgetWindow = null;
    }

    private static Drawing.Icon? LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        try
        {
            return Drawing.Icon.ExtractAssociatedIcon(processPath);
        }
        catch
        {
            return null;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _pollTimer.Stop();
        _discordAutoMute.Dispose();
        _midiRouter.VolumeChanged -= OnMidiVolumeChanged;
        _widgetWindow?.CloseForApplicationExit();
        _widgetWindow = null;
        System.Windows.Application.Current.SessionEnding -= OnSessionEnding;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
        _trayIcon?.Dispose();

        foreach (var debounce in _volumeDebounce.Values)
        {
            debounce.Cancel();
            debounce.Dispose();
        }
        _volumeDebounce.Clear();

        _midiRouter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _controller.Dispose();
        _refreshLock.Dispose();
    }
}
