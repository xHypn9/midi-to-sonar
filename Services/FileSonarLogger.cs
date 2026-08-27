using System.Text;
using System.IO;
using SonarController.Core;

namespace SonarMidiController.App.Services;

public sealed class FileSonarLogger : ISonarLogger
{
    private readonly object _gate = new();

    public FileSonarLogger(string? path = null)
    {
        Path = path ?? RuntimeProfile.LogPath;
    }

    public string Path { get; }

    public void Info(string category, string message) => Write("INFO", category, message);

    public void Error(string category, string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}: {exception.Message}";
        Write("ERROR", category, detail);
    }

    private void Write(string level, string category, string message)
    {
        // The local API currently has no token. If SteelSeries adds one later,
        // callers must never pass it to the logger.
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} [{category}] {message}";
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)
                ?? throw new InvalidOperationException("Log path has no directory.");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
