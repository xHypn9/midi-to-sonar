namespace SonarController.Core;

public interface ISonarLogger
{
    void Info(string category, string message);
    void Error(string category, string message, Exception? exception = null);
}

public sealed class NullSonarLogger : ISonarLogger
{
    public static NullSonarLogger Instance { get; } = new();

    private NullSonarLogger()
    {
    }

    public void Info(string category, string message)
    {
    }

    public void Error(string category, string message, Exception? exception = null)
    {
    }
}
