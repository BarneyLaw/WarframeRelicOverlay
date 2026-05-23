namespace WarframeRelicOverlay.Infrastructure.RewardData;
 
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarframeRelicOverlay.Domain.Models;


/// <summary>
/// Loads the reward item pool from a local <c>items.json</c> file.
///
/// The JSON schema matches what the repo ships:
/// <code>
/// {
///   "version": "2025-05-19",
///   "items": {
///     "value": [
///       { "name": "Ash Prime Chassis Blueprint" },
///       { "name": "Forma Blueprint", "untradeable": true }
///     ],
///     "Count": 566
///   }
/// }
/// </code>
///
/// Items are loaded once on the first call to <see cref="GetAll"/> and cached for the
/// lifetime of the instance.  If the file is missing or corrupt the repository returns
/// an empty list and logs the error rather than crashing the app.
/// </summary>
public sealed class JsonRewardRepository : IRewardRepository
{
    private readonly string _filePath;
    private IReadOnlyList<RewardItem>? _items;
    private string? _version;

    /// <summary>
    /// Creates a repository backed by the given JSON file path.
    /// The file is not read until <see cref="GetAll"/> is first called.
    /// </summary>
    /// <param name="filePath">
    /// Absolute or relative path to <c>items.json</c>.
    /// Typically <c>"data/items.json"</c> next to the executable.
    /// </param>
    public JsonRewardRepository(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <inheritdoc/>
    public string? Version
    {
        get
        {
            EnsureLoaded();
            return _version;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RewardItem> GetAll()
    {
        EnsureLoaded();
        return _items!;
    }


    private void EnsureLoaded()
    {
        if (_items is not null)
            return; // means we've already loaded successfully, so skip the disk read
 
        try
        {
            LoadFromDisk();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JsonRewardRepository] Failed to load '{_filePath}': {ex.Message}");
            // Log the error but don't throw, to avoid crashing the app. 
            // The matcher can work with an empty pool, albeit with no results.
            _items = Array.Empty<RewardItem>();
            _version = null;
        }
    }

    /// <summary>
    /// Forces a reload from disk on the next <see cref="GetAll"/> call.
    /// Useful after an external update to the JSON file (e.g. auto-update from the API).
    /// </summary>
    public void Invalidate()
    {
        _items = null;
        _version = null;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_filePath))
        {
            Debug.WriteLine($"[JsonRewardRepository] File not found: '{_filePath}'. Using empty reward pool.");
            _items = Array.Empty<RewardItem>();
            _version = null;
            return;
        }
        string json = File.ReadAllText(_filePath);
        var root = JsonSerializer.Deserialize<ItemsFileDto>(json, _jsonOptions);
 
        if (root is null)
        {
            Debug.WriteLine("[JsonRewardRepository] Deserialized null from items.json. Using empty reward pool.");
            _items = Array.Empty<RewardItem>();
            _version = null;
            return;
        }
 
        _version = root.Version;
 
        var dtos = root.Items?.Value;
        if (dtos is null || dtos.Count == 0)
        {
            Debug.WriteLine("[JsonRewardRepository] items.json contains no items. Using empty reward pool.");
            _items = Array.Empty<RewardItem>();
            return;
        }
 
        // Deduplicate by canonical name (case-insensitive) — the old handwritten pool
        // had duplicates and we don't want them silently doubling match candidates.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RewardItem>(dtos.Count);
 
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                continue;
 
            string trimmed = dto.Name.Trim();
            if (!seen.Add(trimmed))
            {
                Debug.WriteLine($"[JsonRewardRepository] Skipping duplicate item: '{trimmed}'");
                continue;
            }
 
            items.Add(new RewardItem(trimmed, dto.Untradeable));
        }
 
        _items = items.AsReadOnly();
        Debug.WriteLine($"[JsonRewardRepository] Loaded {_items.Count} items (version: {_version ?? "unknown"}).");

    }

    // ── JSON DTOs ───────────────────────────────────────────────────────────────
    // These mirror the items.json structure and are only used for deserialization.
 
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
 
    private sealed class ItemsFileDto
    {
        public string? Version { get; set; }
        public ItemsCollectionDto? Items { get; set; }
    }
 
    private sealed class ItemsCollectionDto
    {
        public List<ItemDto>? Value { get; set; }
        public int Count { get; set; }
    }
 
    private sealed class ItemDto
    {
        public string Name { get; set; } = string.Empty;
        public bool Untradeable { get; set; }
    }


}