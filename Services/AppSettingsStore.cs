using System.IO;
using System.Text.Json;

namespace SonarMidiController.App.Services;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool ShowWidget { get; set; }

    public bool LockWidget { get; set; }

    public double WidgetBackgroundOpacity { get; set; } = 85d;

    public double WidgetMuteOpacity { get; set; } = 100d;

    public bool HasWidgetPosition { get; set; }

    public double WidgetLeft { get; set; }

    public double WidgetTop { get; set; }

    public string? WidgetDisplayDeviceName { get; set; }

    public bool HasWidgetSize { get; set; }

    public double WidgetWidth { get; set; } = 590d;
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AppSettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? RuntimeProfile.DataDirectory;
        SettingsPath = System.IO.Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);
    }

    public void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
