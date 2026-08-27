using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using SonarController.Core;
using SonarMidiController.App;
using SonarMidiController.App.Services;

namespace SonarController.Tests;

public sealed class WidgetWindowTests
{
    [Fact]
    public void ShowsFiveChannelsWithLockedClickThroughIndicators()
    {
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;

        var windowThread = new Thread(() =>
        {
            WidgetWindow? widget = null;
            try
            {
                var settingsStore = new AppSettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "SonarMidiControllerTests",
                    Guid.NewGuid().ToString("N")));
                settingsStore.Save(new AppSettings
                {
                    WidgetBackgroundOpacity = 40,
                    WidgetMuteOpacity = 55,
                    LockWidget = true,
                    HasWidgetSize = true,
                    WidgetWidth = 720
                });
                widget = new WidgetWindow(settingsStore);
                widget.Show();
                PumpDispatcherFor(TimeSpan.FromMilliseconds(80));

                Assert.Equal(720, widget.Width);
                Assert.InRange(widget.Height, 720 / (590d / 380d) - 1d, 720 / (590d / 380d) + 1d);
                var background = Assert.IsType<SolidColorBrush>(
                    Find<Border>(widget, "WidgetBackground").Background);
                Assert.Equal(102, background.Color.A);
                Assert.Equal(0.55, Find<ToggleButton>(widget, "MicMute").Opacity, 3);
                Assert.False(Find<ToggleButton>(widget, "MicMute").IsHitTestVisible);
                Assert.False(Find<ToggleButton>(widget, "MicMute").Focusable);
                var windowHandle = new WindowInteropHelper(widget).Handle;
                var lockedStyle = GetWindowLongPtr(windowHandle, -20).ToInt64();
                Assert.NotEqual(0, lockedStyle & 0x00000020L);
                Assert.NotEqual(0, lockedStyle & 0x08000000L);

                var positioningSettings = settingsStore.Load();
                positioningSettings.LockWidget = false;
                widget.ApplyVisualSettings(positioningSettings);
                widget.Left = 100;
                widget.Top = 100;
                PumpDispatcherFor(TimeSpan.FromMilliseconds(500));
                var stableSettings = settingsStore.Load();
                Assert.Equal(100, stableSettings.WidgetLeft, 1);
                Assert.Equal(100, stableSettings.WidgetTop, 1);
                Assert.False(string.IsNullOrWhiteSpace(stableSettings.WidgetDisplayDeviceName));
                stableSettings.LockWidget = true;
                widget.ApplyVisualSettings(stableSettings);

                SendMessage(windowHandle, 0x007E, IntPtr.Zero, IntPtr.Zero);
                widget.Left = 350;
                widget.Top = 350;
                PumpDispatcherFor(TimeSpan.FromMilliseconds(600));
                var settingsDuringDisplayChange = settingsStore.Load();
                Assert.Equal(stableSettings.WidgetLeft, settingsDuringDisplayChange.WidgetLeft, 3);
                Assert.Equal(stableSettings.WidgetTop, settingsDuringDisplayChange.WidgetTop, 3);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(1100));
                Assert.Equal(100, widget.Left, 1);
                Assert.Equal(100, widget.Top, 1);

                var states = new Dictionary<SonarChannel, SonarChannelState>
                {
                    [SonarChannel.Master] = new(1d, false),
                    [SonarChannel.Game] = new(0.25d, false),
                    [SonarChannel.Chat] = new(0.5d, true),
                    [SonarChannel.Media] = new(0.75d, false),
                    [SonarChannel.Aux] = new(0.1d, false),
                    [SonarChannel.Mic] = new(0.6d, false)
                };
                widget.UpdateSnapshot(new SonarSnapshot(states, DateTimeOffset.UtcNow));

                Assert.Equal("25%", Find<TextBlock>(widget, "GameValue").Text);
                Assert.Equal("50%", Find<TextBlock>(widget, "ChatValue").Text);
                Assert.Equal("75%", Find<TextBlock>(widget, "MediaValue").Text);
                Assert.Equal("10%", Find<TextBlock>(widget, "AuxValue").Text);
                Assert.Equal("60%", Find<TextBlock>(widget, "MicValue").Text);
                Assert.True(Find<ToggleButton>(widget, "ChatMute").IsChecked);

                widget.UpdateVolume(SonarChannel.Game, 0.9d);
                Assert.Equal("90%", Find<TextBlock>(widget, "GameValue").Text);

                var resizeThumb = Find<Thumb>(widget, "ResizeThumb");
                Assert.Equal(Visibility.Collapsed, resizeThumb.Visibility);
                resizeThumb.RaiseEvent(new DragDeltaEventArgs(50, 0)
                {
                    RoutedEvent = Thumb.DragDeltaEvent
                });
                Assert.Equal(720, widget.Width, 3);

                var unlockedSettings = settingsStore.Load();
                unlockedSettings.LockWidget = false;
                widget.ApplyVisualSettings(unlockedSettings);
                Assert.Equal(Visibility.Visible, resizeThumb.Visibility);
                var unlockedStyle = GetWindowLongPtr(windowHandle, -20).ToInt64();
                Assert.Equal(0, unlockedStyle & 0x00000020L);
                Assert.Equal(0, unlockedStyle & 0x08000000L);

                Assert.True(SetWindowPos(
                    windowHandle,
                    new IntPtr(-2),
                    0,
                    0,
                    0,
                    0,
                    0x0001 | 0x0002 | 0x0010 | 0x0200));
                Assert.Equal(0, GetWindowLongPtr(windowHandle, -20).ToInt64() & 0x00000008L);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));
                Assert.NotEqual(0, GetWindowLongPtr(windowHandle, -20).ToInt64() & 0x00000008L);

                resizeThumb.RaiseEvent(new DragDeltaEventArgs(50, 0)
                {
                    RoutedEvent = Thumb.DragDeltaEvent
                });
                Assert.Equal(770, widget.Width, 3);
                Assert.InRange(widget.Height, 770 / (590d / 380d) - 1d, 770 / (590d / 380d) + 1d);

                var previewPath = Environment.GetEnvironmentVariable("MIDI_WIDGET_PREVIEW_PATH");
                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    widget.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(widget.ActualWidth),
                        (int)Math.Ceiling(widget.ActualHeight),
                        96,
                        96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(widget);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(previewPath);
                    encoder.Save(stream);
                }

                widget.CloseForApplicationExit();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (widget?.IsVisible == true)
                {
                    widget.CloseForApplicationExit();
                }
                finished.Set();
            }
        })
        {
            IsBackground = true
        };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(8)), "Widget UI test did not finish.");
        Assert.Null(failure);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Missing widget control: {name}");

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

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

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

}
