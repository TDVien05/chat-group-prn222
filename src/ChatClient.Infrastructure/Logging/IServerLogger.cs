namespace ChatClient.Infrastructure.Logging;

public interface IServerLogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}
