using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonarMidiController.App;
using SonarMidiController.App.Services;

namespace SonarController.Tests;

public sealed class SettingsWindowTests
{
    [Fact]
    public void ShowsAnimatedSwitchesAndIndependentOpacitySliders()
    {
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;

        var windowThread = new Thread(() =>
        {
            SettingsWindow? window = null;
            try
            {
                var store = new AppSettingsStore(Path.Combine(
                    Path.GetTempPath(),
                    "SonarMidiControllerTests",
                    Guid.NewGuid().ToString("N")));
                store.Save(new AppSettings
                {
                    StartMinimized = true,
                    ShowWidget = true,
                    LockWidget = true,
                    WidgetBackgroundOpacity = 42,
                    WidgetMuteOpacity = 73
                });

                window = new SettingsWindow(store);
                window.Show();
                PumpDispatcherFor(TimeSpan.FromMilliseconds(220));

                Assert.NotNull(Find<CheckBox>(window, "StartWithWindowsCheckBox").IsChecked);
                Assert.True(Find<CheckBox>(window, "StartMinimizedCheckBox").IsChecked);
                Assert.True(Find<CheckBox>(window, "ShowWidgetCheckBox").IsChecked);
                Assert.True(Find<CheckBox>(window, "LockWidgetCheckBox").IsChecked);
                Assert.Equal(42, Find<Slider>(window, "BackgroundOpacitySlider").Value);
                Assert.Equal(73, Find<Slider>(window, "MuteOpacitySlider").Value);
                Assert.Equal("42%", Find<TextBlock>(window, "BackgroundOpacityValueText").Text);
                Assert.Equal("73%", Find<TextBlock>(window, "MuteOpacityValueText").Text);

                var previewPath = Environment.GetEnvironmentVariable("MIDI_SETTINGS_PREVIEW_PATH");
                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(window.ActualWidth),
                        (int)Math.Ceiling(window.ActualHeight),
                        96,
                        96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(previewPath);
                    encoder.Save(stream);
                }

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.Close();
                }
                finished.Set();
            }
        })
        {
            IsBackground = true
        };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "Settings UI test did not finish.");
        Assert.Null(failure);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Missing settings control: {name}");

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
}
