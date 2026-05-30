namespace WarframeRelicOverlay.Infrastructure.Logging;

using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// File-based logger that writes important application events to a log file.
/// Logs are written to a rolling file in the application's local data directory.
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _logFilePath;
    private readonly object _lockObject = new();
    private const int MaxLogFileSizeBytes = 5_000_000; // 5 MB

    /// <summary>Absolute path of the active log file.</summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Initializes a new instance of the FileLogger.
    /// The log file is created in the application's local AppData directory.
    /// </summary>
    public FileLogger()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string logDirectory = Path.Combine(appDataPath, "WarframeRelicOverlay");

        if (!Directory.Exists(logDirectory))
            Directory.CreateDirectory(logDirectory);

        _logFilePath = Path.Combine(logDirectory, "overlay.log");

        // If log file is too large, archive it
        RollLogFileIfNeeded();
    }

    public void LogInfo(string message)
    {
        WriteLog("INFO", message);
    }

    public void LogWarning(string message)
    {
        WriteLog("WARN", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        string errorMessage = message;
        if (exception != null)
            errorMessage += $" | Exception: {exception.GetType().Name}: {exception.Message}";

        WriteLog("ERROR", errorMessage);
    }

    public void LogOperationStart(string operationName)
    {
        WriteLog("INFO", $"[START] {operationName}");
    }

    public void LogOperationEnd(string operationName, bool success, string? details = null)
    {
        string status = success ? "SUCCESS" : "FAILED";
        string message = $"[END] {operationName}: {status}";
        if (!string.IsNullOrWhiteSpace(details))
            message += $" | {details}";

        WriteLog("INFO", message);
    }

    private void WriteLog(string level, string message)
    {
        lock (_lockObject)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] [{level}] {message}";

                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Silently fail to avoid logging infrastructure taking down the app
                Debug.WriteLine($"Failed to write log entry: {message}");
            }
        }
    }

    private void RollLogFileIfNeeded()
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length > MaxLogFileSizeBytes)
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string archivedPath = _logFilePath.Replace(".log", $".{timestamp}.log");
                    File.Move(_logFilePath, archivedPath, overwrite: false);
                }
            }
        }
        catch
        {
            // Silently fail if unable to roll log file
            Debug.WriteLine("Failed to roll log file");
        }
    }
}
