using System.IO;

namespace SonarMidiController.App.Services;

public static class RuntimeProfile
{
#if DIAGNOSTIC_BUILD
    public static bool IsDiagnostic => true;
    public const string DisplayName = "MIDI to Sonar [Diagnostic 1.3.5]";
    public const string MutexName = "Local\\SimoWork.Midi2Sonar.Diagnostic.SingleInstance";
    public const string PipeName = "SimoWork.Midi2Sonar.Diagnostic.Activation";
    private const string DataDirectoryName = "SonarMidiController-Diagnostic";
#else
    public static bool IsDiagnostic => false;
    public const string DisplayName = "MIDI to Sonar";
    public const string MutexName = "Local\\SimoWork.Midi2Sonar.SingleInstance";
    public const string PipeName = "SimoWork.Midi2Sonar.Activation";
    private const string DataDirectoryName = "SonarMidiController";
#endif

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataDirectoryName);

    public static string LogPath { get; } = Path.Combine(
        DataDirectory,
        "logs",
        "sonar-controller.log");

    public static string MidiMappingPath { get; } = Path.Combine(
        DataDirectory,
        "midi-mappings.json");
}
