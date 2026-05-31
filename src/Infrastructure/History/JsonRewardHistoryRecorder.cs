namespace WarframeRelicOverlay.Infrastructure.History;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarframeRelicOverlay.Infrastructure.Logging;

/// <summary>
/// Persists reward runs to a single human-readable JSON array file
/// (<c>reward-history.json</c>). Each <see cref="Record"/> call reads the
/// existing array, appends the new run, and rewrites the file atomically
/// (temp file + move) so an interrupted write can never corrupt the history.
/// Reward runs occur minutes apart, so the read-modify-write cost is
/// negligible.
///
/// The file defaults to the application's <c>data</c> directory, alongside
/// <c>settings.json</c> and <c>items.json</c>. New fields on
/// <see cref="RewardRunRecord"/> / <see cref="RewardRunItem"/> are
/// forward/backward compatible — unknown properties are ignored on read.
/// </summary>
public sealed class JsonRewardHistoryRecorder : IRewardHistoryRecorder
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <param name="filePath">
    /// Destination file. When <c>null</c>, defaults to
    /// <c>{AppContext.BaseDirectory}\data\reward-history.json</c>.
    /// </param>
    /// <param name="logger">Optional logger for read/write failures.</param>
    public JsonRewardHistoryRecorder(string? filePath = null, ILogger? logger = null)
    {
        _filePath = filePath ?? DefaultFilePath();
        _logger = logger;
    }

    /// <summary>Absolute path of the active history file.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public void Record(RewardRunRecord record)
    {
        try
        {
            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var history = ReadExisting();
                history.Add(record);
                WriteAtomic(history);
            }

            _logger?.LogInfo(
                $"[RewardHistory] Recorded run with {record.Items.Count} item(s) to '{_filePath}'.");
        }
        catch (Exception ex)
        {
            // Best-effort: never let history writing break a pricing run.
            _logger?.LogError("[RewardHistory] Failed to record reward run.", ex);
        }
    }

    private List<RewardRunRecord> ReadExisting()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            string json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            return JsonSerializer.Deserialize<List<RewardRunRecord>>(json, _options) ?? [];
        }
        catch (JsonException ex)
        {
            // Corrupt file: preserve it for inspection and start fresh rather
            // than losing the new run too.
            _logger?.LogWarning(
                $"[RewardHistory] History file '{_filePath}' is corrupt ({ex.Message}); " +
                "backing it up to .bak and starting a new history.");
            try { File.Move(_filePath, _filePath + ".bak", overwrite: true); } catch { }
            return [];
        }
    }

    private void WriteAtomic(List<RewardRunRecord> history)
    {
        string tmpPath = _filePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(history, _options));
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static string DefaultFilePath() =>
        Path.Combine(AppContext.BaseDirectory, "data", "reward-history.json");
}
