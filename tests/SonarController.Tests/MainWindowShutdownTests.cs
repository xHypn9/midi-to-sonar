using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SonarMidiController.App;

namespace SonarController.Tests;

public sealed class MainWindowShutdownTests
{
    [Fact]
    public void TrayExitClosesTheWindowThreadWithoutDeadlock()
    {
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;

        var windowThread = new Thread(() =>
        {
            System.Windows.Application? application = null;
            MainWindow? window = null;
            try
            {
                application = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var testDataDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "SonarMidiControllerTests",
                    Guid.NewGuid().ToString("N"));
                window = new MainWindow(testDataDirectory);
                application.MainWindow = window;
                window.Show();

                var pollTimerField = typeof(MainWindow).GetField(
                    "_pollTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(MainWindow), "_pollTimer");
                var pollTimer = pollTimerField.GetValue(window) as DispatcherTimer
                    ?? throw new InvalidOperationException("Main window poll timer was not created.");
                Assert.Equal(100, pollTimer.Interval.TotalMilliseconds);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));

                var exitMethod = typeof(MainWindow).GetMethod(
                    "ExitFromTray",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(nameof(MainWindow), "ExitFromTray");
                exitMethod.Invoke(window, null);
                application.Shutdown();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                failure = ex.InnerException;
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
                application?.Shutdown();
                finished.Set();
            }
        })
        {
            IsBackground = true
        };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();

        Assert.True(
            finished.Wait(TimeSpan.FromSeconds(5)),
            "Tray Exit left the application window thread blocked during MIDI shutdown.");
        Assert.Null(failure);
    }

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
