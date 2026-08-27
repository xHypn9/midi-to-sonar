using System.Windows;
using SonarMidiController.App.Services;

namespace SonarMidiController.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstanceAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += OnActivationRequested;
        _singleInstance.StartListening();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnActivationRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.RestoreFromSecondaryInstance();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= OnActivationRequested;
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
