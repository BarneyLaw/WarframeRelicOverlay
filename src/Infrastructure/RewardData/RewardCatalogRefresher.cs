namespace WarframeRelicOverlay.Infrastructure.RewardData;

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarframeRelicOverlay.Infrastructure.Logging;

/// <summary>
/// Best-effort background refresher for the reward pool (<c>items.json</c>).
///
/// <para>
/// On launch the app loads whatever <c>items.json</c> is already on disk (a
/// known-good copy ships with the build), so it is fully usable offline. This
/// refresher then runs <b>asynchronously and best-effort</b> to regenerate that
/// file by joining two authoritative sources:
/// <list type="bullet">
///   <item>
///     <b>WFCD relic drop data</b> (<see cref="RelicsUrl"/>) — parsed from DE's
///     official drop tables; the distinct set of <c>rewards[].itemName</c> is the
///     universe of relic reward names exactly as they appear on the reward screen
///     (which is what OCR reads).
///   </item>
///   <item>
///     <b>warframe.market v2 catalog</b> (<see cref="MarketItemsUrl"/>) — each
///     item's <c>slug</c> (url_name) plus its localized English name.
///   </item>
/// </list>
/// The merge is a <b>left join from the WFCD reward set</b>: every reward name is
/// kept; the market slug is attached when the catalog has a name match; anything
/// without a match (Forma, Kuva, Riven Sliver, …) is flagged untradeable so the
/// pipeline never tries to price it.
/// </para>
///
/// <para>
/// The refresh is gated by <see cref="_maxAge"/> so we do not re-download ~1 MB
/// on every launch, writes atomically (temp file + replace), and <b>never throws</b>
/// — any failure leaves the existing file untouched. Because the matcher snapshots
/// the pool at startup, a successful refresh takes effect on the <b>next</b> launch.
/// </para>
/// </summary>
public sealed class RewardCatalogRefresher
{
    /// <summary>WFCD parsed relic drop tables (authoritative reward-name universe).</summary>
    private const string RelicsUrl = "https://drops.warframestat.us/data/relics.json";

    /// <summary>warframe.market v2 item catalog (authoritative slug/url_name source).</summary>
    private const string MarketItemsUrl = "https://api.warframe.market/v2/items";

    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly TimeSpan _maxAge;

    /// <summary>
    /// Creates a refresher that maintains the reward pool at <paramref name="filePath"/>.
    /// </summary>
    /// <param name="http">
    /// Shared <see cref="HttpClient"/>. Both feeds are fetched via absolute URLs, so
    /// any configured <see cref="HttpClient.BaseAddress"/> is ignored.
    /// </param>
    /// <param name="filePath">Path to the <c>items.json</c> the app reads at startup.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="maxAge">
    /// Skip the refresh when the file is younger than this. Defaults to 24 hours.
    /// </param>
    public RewardCatalogRefresher(
        HttpClient http, string filePath, ILogger? logger = null, TimeSpan? maxAge = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    /// <summary>
    /// Refreshes the pool when the on-disk file is older than <see cref="_maxAge"/>.
    /// Safe to fire-and-forget: it swallows and logs every error so it can never
    /// crash startup or leave a partially written file.
    /// </summary>
    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsStale())
            {
                _logger?.LogInfo(
                    $"[CatalogRefresh] Skipped: '{_filePath}' is younger than {_maxAge.TotalHours:0.#} h.");
                return;
            }

            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInfo("[CatalogRefresh] Cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError("[CatalogRefresh] Refresh failed; keeping existing pool.", ex);
        }
    }

    /// <summary>
    /// True when the file is missing or older than <see cref="_maxAge"/>. Uses the
    /// file's last-write time, which is reset every time we successfully rewrite it,
    /// so a refresh happens at most once per <see cref="_maxAge"/> window.
    /// </summary>
    private bool IsStale()
    {
        if (!File.Exists(_filePath)) return true;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(_filePath) >= _maxAge;
    }

    /// <summary>
    /// Fetches both feeds, performs the left join, and atomically overwrites the
    /// pool file. Returns without writing if either feed is unavailable or empty.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInfo("[CatalogRefresh] Fetching WFCD relics and warframe.market catalog.");

        var relics = await FetchJsonAsync<RelicsDto>(RelicsUrl, cancellationToken);
        var market = await FetchJsonAsync<MarketItemsDto>(MarketItemsUrl, cancellationToken);

        if (relics?.Relics is not { Count: > 0 } || market?.Data is not { Count: > 0 })
        {
            _logger?.LogWarning(
                "[CatalogRefresh] One or both feeds were empty; keeping existing pool.");
            return;
        }

        // Build a normalized name -> slug map from the market catalog.
        var slugByName = new Dictionary<string, string>(market.Data.Count);
        foreach (var item in market.Data)
        {
            string? name = item.I18n?.En?.Name;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(item.Slug))
                continue;

            string key = Normalize(name);
            // First slug wins; the catalog has no meaningful duplicate display names.
            slugByName.TryAdd(key, item.Slug);
        }

        // Left join: keep every distinct relic reward name; attach a slug if the
        // market knows it, otherwise flag untradeable.
        var names = relics.Relics
            .Where(r => r.Rewards is not null)
            .SelectMany(r => r.Rewards!)
            .Select(r => r.ItemName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<ItemDto>(names.Count);
        var unmatched = new List<string>();
        foreach (string name in names)
        {
            slugByName.TryGetValue(Normalize(name), out string? slug);
            if (slug is null) unmatched.Add(name);
            items.Add(new ItemDto { Name = name, Slug = slug, Untradeable = slug is null });
        }

        _logger?.LogInfo(
            $"[CatalogRefresh] Merged {items.Count} reward(s): " +
            $"{items.Count - unmatched.Count} with market slug, {unmatched.Count} without.");
        if (unmatched.Count > 0)
            _logger?.LogInfo($"[CatalogRefresh] No market slug for: {string.Join(", ", unmatched)}.");

        var file = new ItemsFileDto
        {
            Version = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Source = "WFCD relics.json x warframe.market v2 /items",
            Items = new ItemsCollectionDto { Value = items, Count = items.Count },
        };

        await WriteAtomicAsync(file, cancellationToken);
        _logger?.LogInfo($"[CatalogRefresh] Wrote refreshed pool to '{_filePath}'.");
    }

    /// <summary>Streams and deserializes JSON from an absolute URL.</summary>
    private async Task<T?> FetchJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _readOptions, cancellationToken);
    }

    /// <summary>Serializes to a temp file then atomically replaces the target.</summary>
    private async Task WriteAtomicAsync(ItemsFileDto file, CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = _filePath + ".tmp";
        string json = JsonSerializer.Serialize(file, _writeOptions);
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken);

        if (File.Exists(_filePath))
            File.Replace(tempPath, _filePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, _filePath);
    }

    /// <summary>
    /// Normalizes a display name for cross-source matching: trim, lowercase,
    /// collapse internal whitespace, and treat "&amp;" as "and" so e.g.
    /// "Cobra &amp; Crane Prime Hilt" matches the market name.
    /// </summary>
    private static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool lastWasSpace = false;
        foreach (char raw in name.Trim())
        {
            char c = char.ToLowerInvariant(raw);
            if (c == '&')
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                sb.Append("and");
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ── Output DTOs (must match what JsonRewardRepository reads) ─────────────

    private sealed class ItemsFileDto
    {
        public string? Version { get; set; }
        public string? Source { get; set; }
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
        public string? Slug { get; set; }
        public bool Untradeable { get; set; }
    }

    // ── Feed DTOs ────────────────────────────────────────────────────────────

    private sealed class RelicsDto
    {
        [JsonPropertyName("relics")]
        public List<RelicDto>? Relics { get; set; }
    }

    private sealed class RelicDto
    {
        [JsonPropertyName("rewards")]
        public List<RelicRewardDto>? Rewards { get; set; }
    }

    private sealed class RelicRewardDto
    {
        [JsonPropertyName("itemName")]
        public string? ItemName { get; set; }
    }

    private sealed class MarketItemsDto
    {
        [JsonPropertyName("data")]
        public List<MarketItemDto>? Data { get; set; }
    }

    private sealed class MarketItemDto
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("i18n")]
        public MarketI18nDto? I18n { get; set; }
    }

    private sealed class MarketI18nDto
    {
        [JsonPropertyName("en")]
        public MarketLangDto? En { get; set; }
    }

    private sealed class MarketLangDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
