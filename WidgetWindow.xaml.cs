using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using SonarController.Core;
using SonarMidiController.App.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace SonarMidiController.App;

public partial class WidgetWindow : Window
{
    private const double DesignAspectRatio = 590d / 380d;
    private const int ExtendedWindowStyleIndex = -20;
    private const long TransparentWindowStyle = 0x00000020L;
    private const long NoActivateWindowStyle = 0x08000000L;
    private const long TopmostWindowStyle = 0x00000008L;
    private const int WindowPositionChangedMessage = 0x0047;
    private const int DisplayChangeMessage = 0x007E;
    private const int DpiChangedMessage = 0x02E0;
    private const int DwmCompositionChangedMessage = 0x031E;
    private const uint PreviousWindow = 3;
    private const uint DoNotResizeWindow = 0x0001;
    private const uint DoNotMoveWindow = 0x0002;
    private const uint DoNotActivateWindow = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private static readonly IntPtr TopmostWindow = new(-1);
    private readonly AppSettingsStore _settingsStore;
    private readonly ISonarLogger _logger;
    private readonly DispatcherTimer _positionSaveTimer;
    private readonly DispatcherTimer _topmostHealthTimer;
    private readonly DispatcherTimer _displaySettleTimer;
    private readonly Dictionary<SonarChannel, WidgetControls> _controls;
    private readonly Dictionary<SonarChannel, double> _volumes = new();
    private bool _isLocked;
    private bool _allowClose;
    private bool _positionReady;
    private bool _displayConfigurationChanging;
    private bool _suppressGeometryPersistence;
    private bool _displayEventsSubscribed;
    private HwndSource? _windowSource;
    private SavedWidgetGeometry? _stableGeometry;

    public WidgetWindow(AppSettingsStore settingsStore, ISonarLogger? logger = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _logger = logger ?? NullSonarLogger.Instance;
        _controls = new()
        {
            [SonarChannel.Game] = new(GameTrack, GameHandle, GameValue, GameMute),
            [SonarChannel.Chat] = new(ChatTrack, ChatHandle, ChatValue, ChatMute),
            [SonarChannel.Media] = new(MediaTrack, MediaHandle, MediaValue, MediaMute),
            [SonarChannel.Aux] = new(AuxTrack, AuxHandle, AuxValue, AuxMute),
            [SonarChannel.Mic] = new(MicTrack, MicHandle, MicValue, MicMute)
        };

        _positionSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _positionSaveTimer.Tick += (_, _) =>
        {
            _positionSaveTimer.Stop();
            SaveGeometry();
        };

        _topmostHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _topmostHealthTimer.Tick += (_, _) => EnsureTopmostIfNeeded();

        _displaySettleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _displaySettleTimer.Tick += (_, _) => CompleteDisplayConfigurationChange();

        ApplyVisualSettings(_settingsStore.Load());
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        LocationChanged += OnGeometryChanged;
        SizeChanged += OnGeometryChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public void UpdateSnapshot(SonarSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));
            return;
        }

        foreach (var (channel, controls) in _controls)
        {
            if (!snapshot.Channels.TryGetValue(channel, out var state))
            {
                continue;
            }

            UpdateVolume(channel, state.Volume);
            SetMuteState(controls.Mute, state.IsMuted);
        }
    }

    public void UpdateMute(SonarChannel channel, bool isMuted)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateMute(channel, isMuted));
            return;
        }

        if (_controls.TryGetValue(channel, out var controls))
        {
            SetMuteState(controls.Mute, isMuted);
        }
    }

    public void UpdateVolume(SonarChannel channel, double volume)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateVolume(channel, volume));
            return;
        }

        if (!_controls.TryGetValue(channel, out var controls))
        {
            return;
        }

        volume = Math.Clamp(volume, 0d, 1d);
        if (_volumes.TryGetValue(channel, out var previousVolume) &&
            Math.Abs(previousVolume - volume) < 0.0001d)
        {
            return;
        }

        _volumes[channel] = volume;
        controls.Value.Text = $"{volume * 100d:0}%";
        PositionHandle(controls, volume);
    }

    public void ApplyVisualSettings(AppSettings settings)
    {
        var wasLocked = _isLocked;
        _isLocked = settings.LockWidget;
        if (wasLocked && !_isLocked)
        {
            _suppressGeometryPersistence = false;
        }
        ResizeThumb.Visibility = _isLocked ? Visibility.Collapsed : Visibility.Visible;
        ApplyClickThroughStyle();

        var backgroundOpacity = Math.Clamp(settings.WidgetBackgroundOpacity, 0d, 100d);
        var alpha = (byte)Math.Round(backgroundOpacity / 100d * byte.MaxValue);
        WidgetBackground.Background = new SolidColorBrush(
            WpfColor.FromArgb(alpha, 0x11, 0x15, 0x1C));

        var muteOpacity = Math.Clamp(settings.WidgetMuteOpacity, 0d, 100d) / 100d;
        foreach (var controls in _controls.Values)
        {
            controls.Mute.Opacity = muteOpacity;
        }
    }

    public void CloseForApplicationExit()
    {
        _allowClose = true;
        SaveGeometry();
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialSize();
        ApplyInitialPosition();
        _positionReady = true;
        InitializeStableGeometry();
        SubscribeToDisplayEvents();
        EnsureTopmostIfNeeded(force: true);
        _topmostHealthTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        ApplyClickThroughStyle();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionSaveTimer.Stop();
        _topmostHealthTimer.Stop();
        _displaySettleTimer.Stop();
        UnsubscribeFromDisplayEvents();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
    }

    private void SubscribeToDisplayEvents()
    {
        if (_displayEventsSubscribed)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _displayEventsSubscribed = true;
    }

    private void UnsubscribeFromDisplayEvents()
    {
        if (!_displayEventsSubscribed)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayEventsSubscribed = false;
    }

    private void OnDisplaySettingsChanging(object? sender, EventArgs e) =>
        DispatchDisplayConfigurationChange();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        DispatchDisplayConfigurationChange();

    private void DispatchDisplayConfigurationChange()
    {
        if (Dispatcher.CheckAccess())
        {
            BeginDisplayConfigurationChange();
            return;
        }

        Dispatcher.BeginInvoke(BeginDisplayConfigurationChange, DispatcherPriority.Send);
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch (message)
        {
            case DisplayChangeMessage:
            case DpiChangedMessage:
            case DwmCompositionChangedMessage:
                BeginDisplayConfigurationChange();
                break;
            case WindowPositionChangedMessage:
                Dispatcher.BeginInvoke(
                    () => EnsureTopmostIfNeeded(),
                    DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }

    private void BeginDisplayConfigurationChange()
    {
        _displayConfigurationChanging = true;
        _positionSaveTimer.Stop();
        _displaySettleTimer.Stop();
        _displaySettleTimer.Start();
    }

    private void CompleteDisplayConfigurationChange()
    {
        _displaySettleTimer.Stop();

        var restored = RestoreStableGeometryIfAvailable();
        _suppressGeometryPersistence = !restored && _isLocked;
        _displayConfigurationChanging = false;
        EnsureTopmostIfNeeded(force: true);

        _logger.Info(
            "Widget",
            restored
                ? "Display configuration settled; stable widget geometry restored."
                : "Display configuration settled; emergency relocation was not persisted.");
    }

    private void ApplyInitialSize()
    {
        var settings = _settingsStore.Load();
        if (!settings.HasWidgetSize || !double.IsFinite(settings.WidgetWidth))
        {
            return;
        }

        Width = Math.Clamp(settings.WidgetWidth, MinWidth, MaxWidth);
        Height = Width / DesignAspectRatio;
    }

    private void ApplyInitialPosition()
    {
        var settings = _settingsStore.Load();
        if (settings.HasWidgetPosition && IsVisibleOnDesktop(settings.WidgetLeft, settings.WidgetTop))
        {
            Left = settings.WidgetLeft;
            Top = settings.WidgetTop;
            return;
        }

        var screens = Forms.Screen.AllScreens.OrderBy(screen => screen.DeviceName).ToArray();
        var target = screens.FirstOrDefault(screen =>
                screen.DeviceName.EndsWith("DISPLAY2", StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault(screen => !screen.Primary)
            ?? Forms.Screen.PrimaryScreen;
        if (target is null)
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 24;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var workingArea = target.WorkingArea;
        var left = workingArea.Left / dpi.DpiScaleX;
        var top = workingArea.Top / dpi.DpiScaleY;
        var width = workingArea.Width / dpi.DpiScaleX;
        Left = left + Math.Max(24d, (width - Width) / 2d);
        Top = top + 28d;
    }

    private bool IsVisibleOnDesktop(double left, double top)
    {
        var candidate = new Rect(left, top, Width, Height);
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return candidate.IntersectsWith(desktop);
    }

    private void InitializeStableGeometry()
    {
        var settings = _settingsStore.Load();
        _stableGeometry = new SavedWidgetGeometry(
            Left,
            Top,
            Width,
            settings.WidgetDisplayDeviceName ?? GetCurrentDisplayDeviceName());
    }

    private bool RestoreStableGeometryIfAvailable()
    {
        if (_stableGeometry is not { } stable || !IsGeometryAvailable(stable))
        {
            return false;
        }

        Width = Math.Clamp(stable.Width, MinWidth, MaxWidth);
        Height = Width / DesignAspectRatio;
        Left = stable.Left;
        Top = stable.Top;
        return true;
    }

    private bool IsGeometryAvailable(SavedWidgetGeometry geometry)
    {
        if (!string.IsNullOrWhiteSpace(geometry.DisplayDeviceName))
        {
            return Forms.Screen.AllScreens.Any(screen =>
                string.Equals(
                    screen.DeviceName,
                    geometry.DisplayDeviceName,
                    StringComparison.OrdinalIgnoreCase));
        }

        return IsVisibleOnDesktop(geometry.Left, geometry.Top);
    }

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (!_positionReady || _displayConfigurationChanging)
        {
            return;
        }

        if (_isLocked && _stableGeometry is { DisplayDeviceName: not null } stable)
        {
            var currentDisplay = GetCurrentDisplayDeviceName();
            if (!string.Equals(
                    currentDisplay,
                    stable.DisplayDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _suppressGeometryPersistence = true;
                _positionSaveTimer.Stop();
                return;
            }
        }

        if (_suppressGeometryPersistence && _isLocked)
        {
            return;
        }

        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void SaveGeometry()
    {
        if (!_positionReady ||
            _displayConfigurationChanging ||
            (_suppressGeometryPersistence && _isLocked) ||
            double.IsNaN(Left) ||
            double.IsNaN(Top))
        {
            return;
        }

        var displayDeviceName = GetCurrentDisplayDeviceName();
        var settings = _settingsStore.Load();
        settings.HasWidgetPosition = true;
        settings.WidgetLeft = Left;
        settings.WidgetTop = Top;
        settings.WidgetDisplayDeviceName = displayDeviceName;
        settings.HasWidgetSize = true;
        settings.WidgetWidth = Width;
        _settingsStore.Save(settings);
        _stableGeometry = new SavedWidgetGeometry(Left, Top, Width, displayDeviceName);
        _suppressGeometryPersistence = false;
    }

    private string? GetCurrentDisplayDeviceName()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rectangle))
        {
            return null;
        }

        var width = Math.Max(1, rectangle.Right - rectangle.Left);
        var height = Math.Max(1, rectangle.Bottom - rectangle.Top);
        var bounds = new Drawing.Rectangle(rectangle.Left, rectangle.Top, width, height);
        return Forms.Screen.FromRectangle(bounds).DeviceName;
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isLocked && e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isLocked)
        {
            return;
        }

        var horizontalDelta = e.HorizontalChange;
        var verticalDeltaAsWidth = e.VerticalChange * DesignAspectRatio;
        var delta = Math.Abs(horizontalDelta) >= Math.Abs(verticalDeltaAsWidth)
            ? horizontalDelta
            : verticalDeltaAsWidth;
        var newWidth = Math.Clamp(ActualWidth + delta, MinWidth, MaxWidth);
        Width = newWidth;
        Height = newWidth / DesignAspectRatio;
    }

    private void ApplyClickThroughStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(handle, ExtendedWindowStyleIndex).ToInt64();
        var clickThroughStyles = TransparentWindowStyle | NoActivateWindowStyle;
        var updatedStyle = _isLocked
            ? currentStyle | clickThroughStyles
            : currentStyle & ~clickThroughStyles;
        if (updatedStyle != currentStyle)
        {
            SetWindowLongPtr(handle, ExtendedWindowStyleIndex, new IntPtr(updatedStyle));
        }

        EnsureTopmostIfNeeded();
    }

    private void EnsureTopmostIfNeeded(bool force = false)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!force && IsTopmostPlacementHealthy(handle))
        {
            return;
        }

        if (!SetWindowPos(
                handle,
                TopmostWindow,
                0,
                0,
                0,
                0,
                DoNotResizeWindow |
                DoNotMoveWindow |
                DoNotActivateWindow |
                DoNotChangeOwnerZOrder))
        {
            _logger.Error(
                "Widget",
                $"Unable to restore topmost state (Win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    private static bool IsTopmostPlacementHealthy(IntPtr handle)
    {
        var ownStyle = GetWindowLongPtr(handle, ExtendedWindowStyleIndex).ToInt64();
        if ((ownStyle & TopmostWindowStyle) == 0)
        {
            return false;
        }

        var windowAbove = GetWindow(handle, PreviousWindow);
        for (var inspected = 0; windowAbove != IntPtr.Zero && inspected < 512; inspected++)
        {
            if (IsWindowVisible(windowAbove))
            {
                var style = GetWindowLongPtr(windowAbove, ExtendedWindowStyleIndex).ToInt64();
                if ((style & TopmostWindowStyle) == 0)
                {
                    return false;
                }
            }

            windowAbove = GetWindow(windowAbove, PreviousWindow);
        }

        return true;
    }

    private static void SetMuteState(ToggleButton button, bool isMuted)
    {
        if (button.IsChecked != isMuted)
        {
            button.IsChecked = isMuted;
        }
    }

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var entry = _controls.FirstOrDefault(pair => ReferenceEquals(pair.Value.Track, sender));
        if (entry.Value is not null && _volumes.TryGetValue(entry.Key, out var volume))
        {
            PositionHandle(entry.Value, volume);
        }
    }

    private static void PositionHandle(WidgetControls controls, double volume)
    {
        var availableHeight = Math.Max(0d, controls.Track.ActualHeight - controls.Handle.Height);
        var left = Math.Max(0d, (controls.Track.ActualWidth - controls.Handle.Width) / 2d);
        Canvas.SetLeft(controls.Handle, left);
        Canvas.SetTop(controls.Handle, (1d - volume) * availableHeight);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private sealed record WidgetControls(
        Canvas Track,
        Border Handle,
        TextBlock Value,
        ToggleButton Mute);

    private sealed record SavedWidgetGeometry(
        double Left,
        double Top,
        double Width,
        string? DisplayDeviceName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
