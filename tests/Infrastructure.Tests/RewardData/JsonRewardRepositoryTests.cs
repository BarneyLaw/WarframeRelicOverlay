namespace Infrastructure.Tests;

using FluentAssertions;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Infrastructure.RewardData;
using Xunit;

public class JsonRewardRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public JsonRewardRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RewardRepoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteTempJson(string json)
    {
        string path = Path.Combine(_tempDir, "items.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_Loads_Items_From_Valid_Json()
    {
        string path = WriteTempJson("""
        {
            "version": "2025-05-19",
            "items": {
                "value": [
                    { "name": "Ash Prime Chassis Blueprint" },
                    { "name": "Forma Blueprint" }
                ],
                "Count": 2
            }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items.Should().HaveCount(2);
        items[0].CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
        items[1].CanonicalName.Should().Be("Forma Blueprint");
    }

    [Fact]
    public void Version_Returns_String_From_Json()
    {
        string path = WriteTempJson("""
        {
            "version": "2025-05-19",
            "items": { "value": [{ "name": "Ash Prime Blueprint" }], "Count": 1 }
        }
        """);

        var repo = new JsonRewardRepository(path);

        repo.Version.Should().Be("2025-05-19");
    }

    [Fact]
    public void Untradeable_Flag_Is_Preserved()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": {
                "value": [
                    { "name": "Ash Prime Blueprint" },
                    { "name": "Forma Blueprint", "untradeable": true }
                ],
                "Count": 2
            }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items[0].IsUntradeable.Should().BeFalse();
        items[1].IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void MatchPattern_Is_Lowercased_Name()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": { "value": [{ "name": "Ash Prime Chassis Blueprint" }], "Count": 1 }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var item = repo.GetAll()[0];

        item.MatchPattern.Should().Be("ash prime chassis blueprint");
    }

    // ── Deduplication ───────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_Items_Are_Deduplicated()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": {
                "value": [
                    { "name": "Vectis Prime Barrel" },
                    { "name": "Vectis Prime Barrel" },
                    { "name": "Ash Prime Blueprint" }
                ],
                "Count": 3
            }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items.Should().HaveCount(2);
        items.Select(i => i.CanonicalName)
             .Should().Contain("Vectis Prime Barrel")
             .And.Contain("Ash Prime Blueprint");
    }

    [Fact]
    public void Deduplication_Is_Case_Insensitive()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": {
                "value": [
                    { "name": "Ash Prime Blueprint" },
                    { "name": "ash prime blueprint" }
                ],
                "Count": 2
            }
        }
        """);

        var repo = new JsonRewardRepository(path);
        repo.GetAll().Should().HaveCount(1);
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Missing_File_Returns_Empty_List()
    {
        string path = Path.Combine(_tempDir, "does_not_exist.json");

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items.Should().BeEmpty();
        repo.Version.Should().BeNull();
    }

    [Fact]
    public void Corrupt_Json_Returns_Empty_List()
    {
        string path = WriteTempJson("{ not valid json at all!!!");

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items.Should().BeEmpty();
    }

    [Fact]
    public void Empty_Items_Array_Returns_Empty_List()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": { "value": [], "Count": 0 }
        }
        """);

        var repo = new JsonRewardRepository(path);
        repo.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Blank_Names_Are_Skipped()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": {
                "value": [
                    { "name": "" },
                    { "name": "   " },
                    { "name": "Ash Prime Blueprint" }
                ],
                "Count": 3
            }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var items = repo.GetAll();

        items.Should().HaveCount(1);
        items[0].CanonicalName.Should().Be("Ash Prime Blueprint");
    }

    [Fact]
    public void Names_Are_Trimmed()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": { "value": [{ "name": "  Ash Prime Blueprint  " }], "Count": 1 }
        }
        """);

        var repo = new JsonRewardRepository(path);
        repo.GetAll()[0].CanonicalName.Should().Be("Ash Prime Blueprint");
    }

    // ── Caching & Invalidation ──────────────────────────────────────────────

    [Fact]
    public void GetAll_Caches_Result_Across_Calls()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": { "value": [{ "name": "Ash Prime Blueprint" }], "Count": 1 }
        }
        """);

        var repo = new JsonRewardRepository(path);
        var first = repo.GetAll();
        var second = repo.GetAll();

        ReferenceEquals(first, second).Should().BeTrue("repeated calls should return the same cached list");
    }

    [Fact]
    public void Invalidate_Forces_Reload_On_Next_Call()
    {
        string path = WriteTempJson("""
        {
            "version": "1",
            "items": { "value": [{ "name": "Ash Prime Blueprint" }], "Count": 1 }
        }
        """);

        var repo = new JsonRewardRepository(path);
        repo.GetAll().Should().HaveCount(1);

        // Overwrite with a different file
        File.WriteAllText(path, """
        {
            "version": "2",
            "items": {
                "value": [
                    { "name": "Ash Prime Blueprint" },
                    { "name": "Atlas Prime Blueprint" }
                ],
                "Count": 2
            }
        }
        """);

        repo.Invalidate();
        repo.GetAll().Should().HaveCount(2);
        repo.Version.Should().Be("2");
    }

    // ── Loads the actual shipped items.json ──────────────────────────────────

    [Fact]
    public void Loads_Real_Items_Json_If_Present()
    {
        // Walk up from the test bin directory to find the repo root's data/items.json
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return; // skip if we can't find it (e.g. CI without the data file)

        string itemsPath = Path.Combine(repoRoot, "data", "items.json");
        if (!File.Exists(itemsPath))
            return;

        var repo = new JsonRewardRepository(itemsPath);
        var items = repo.GetAll();

        items.Should().NotBeEmpty("the shipped items.json should contain items");
        items.Count.Should().BeGreaterThan(100, "the shipped pool should have hundreds of items");
        repo.Version.Should().NotBeNullOrWhiteSpace();

        // Spot-check a known item
        items.Should().Contain(i => i.CanonicalName == "Ash Prime Chassis Blueprint");
    }

    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "WarframeRelicOverlay.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}