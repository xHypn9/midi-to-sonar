namespace SonarController.Core;

public sealed class SonarApiException : Exception
{
    public SonarApiException(string message) : base(message)
    {
    }

    public SonarApiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
