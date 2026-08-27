using Microsoft.Win32;

namespace SonarMidiController.App.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Midi2Sonar";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string value &&
               string.Equals(value, BuildCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException(
                "Unable to open the Windows startup registry key.");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "Unable to determine the application executable path.");
        }

        return $"\"{executablePath}\"";
    }
}
