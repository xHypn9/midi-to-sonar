using System.Diagnostics;
using System.Windows;
using SonarMidiController.App.Services;

namespace SonarMidiController.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _settingsStore;
    private readonly StartupRegistrationService _startupRegistration = new();

    public AppSettings SavedSettings { get; private set; } = new();

    public SettingsWindow(AppSettingsStore settingsStore)
    {
        InitializeComponent();
        _settingsStore = settingsStore;

        var settings = _settingsStore.Load();
        var startupEnabled = settings.StartWithWindows;
        try
        {
            startupEnabled |= _startupRegistration.IsEnabled();
        }
        catch
        {
        }

        StartWithWindowsCheckBox.IsChecked = startupEnabled;
        if (RuntimeProfile.IsDiagnostic)
        {
            StartWithWindowsCheckBox.IsChecked = false;
            StartWithWindowsCheckBox.IsEnabled = false;
            StartWithWindowsCheckBox.ToolTip = "Disabled in the separate diagnostic build.";
        }
        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
        ShowWidgetCheckBox.IsChecked = settings.ShowWidget;
        LockWidgetCheckBox.IsChecked = settings.LockWidget;
        BackgroundOpacitySlider.Value = settings.WidgetBackgroundOpacity;
        MuteOpacitySlider.Value = settings.WidgetMuteOpacity;
        UpdateOpacityLabels();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsStore.EnsureDataDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = _settingsStore.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Unable to open the data folder.\n\n{ex.Message}",
                "MIDI to Sonar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsStore.Load();
        settings.StartWithWindows = !RuntimeProfile.IsDiagnostic &&
            StartWithWindowsCheckBox.IsChecked == true;
        settings.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        settings.ShowWidget = ShowWidgetCheckBox.IsChecked == true;
        settings.LockWidget = LockWidgetCheckBox.IsChecked == true;
        settings.WidgetBackgroundOpacity = Math.Round(BackgroundOpacitySlider.Value);
        settings.WidgetMuteOpacity = Math.Round(MuteOpacitySlider.Value);

        try
        {
            if (!RuntimeProfile.IsDiagnostic)
            {
                _startupRegistration.SetEnabled(settings.StartWithWindows);
            }
            _settingsStore.Save(settings);
            SavedSettings = settings;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Unable to save the settings.\n\n{ex.Message}",
                "MIDI to Sonar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpacitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        UpdateOpacityLabels();

    private void UpdateOpacityLabels()
    {
        if (BackgroundOpacityValueText is not null)
        {
            BackgroundOpacityValueText.Text = $"{BackgroundOpacitySlider.Value:0}%";
        }
        if (MuteOpacityValueText is not null)
        {
            MuteOpacityValueText.Text = $"{MuteOpacitySlider.Value:0}%";
        }
    }
}
