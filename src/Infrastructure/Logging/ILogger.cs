namespace WarframeRelicOverlay.Infrastructure.Logging;

/// <summary>
/// Defines a contract for logging important application events.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Logs an informational message about a successful operation or state change.
    /// </summary>
    void LogInfo(string message);

    /// <summary>
    /// Logs a warning about a potentially problematic condition that doesn't prevent operation.
    /// </summary>
    void LogWarning(string message);

    /// <summary>
    /// Logs an error that occurred during an operation.
    /// </summary>
    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Logs the start of a critical operation.
    /// </summary>
    void LogOperationStart(string operationName);

    /// <summary>
    /// Logs the completion of a critical operation.
    /// </summary>
    void LogOperationEnd(string operationName, bool success, string? details = null);
}
