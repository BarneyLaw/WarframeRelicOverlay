namespace WarframeRelicOverlay.Tests.Infrastructure.History;

using System.Text.Json;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.History;
using Xunit;

public sealed class JsonRewardHistoryRecorderTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"reward-history-test-{Guid.NewGuid():N}.json");

    // The file is written with camelCase keys; read it back the same way.
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private List<RewardRunRecord> ReadBack() =>
        JsonSerializer.Deserialize<List<RewardRunRecord>>(File.ReadAllText(_file), ReadOpts)!;

    public void Dispose()
    {
        foreach (var f in new[] { _file, _file + ".bak", _file + ".tmp" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static RewardRunRecord Run(params (string? Name, int? Price)[] items) =>
        new()
        {
            Timestamp = DateTimeOffset.Now,
            Items = items.Select(i => new RewardRunItem { Name = i.Name, Price = i.Price }).ToList(),
        };

    [Fact]
    public void Record_WritesItemsAndPrices_AsJsonArray()
    {
        var recorder = new JsonRewardHistoryRecorder(_file);

        recorder.Record(Run(("Tekko Prime Gauntlet", 8), ("Forma Blueprint", null)));

        File.Exists(_file).Should().BeTrue();
        var history = ReadBack();

        history.Should().HaveCount(1);
        history[0].Items.Should().HaveCount(2);
        history[0].Items[0].Name.Should().Be("Tekko Prime Gauntlet");
        history[0].Items[0].Price.Should().Be(8);
        history[0].Items[1].Name.Should().Be("Forma Blueprint");
        history[0].Items[1].Price.Should().BeNull();
    }

    [Fact]
    public void Record_AppendsToExistingHistory()
    {
        var recorder = new JsonRewardHistoryRecorder(_file);

        recorder.Record(Run(("A Prime Blueprint", 1)));
        recorder.Record(Run(("B Prime Blueprint", 2)));

        var history = ReadBack();
        history.Should().HaveCount(2, "each run appends to the array rather than overwriting it");
    }

    [Fact]
    public void Record_UsesCamelCaseKeys_AndPreservesTimestamp()
    {
        var recorder = new JsonRewardHistoryRecorder(_file);
        var when = DateTimeOffset.Now;

        recorder.Record(new RewardRunRecord
        {
            Timestamp = when,
            Items = [new RewardRunItem { Name = "Paris Prime String", Price = 4 }],
        });

        string json = File.ReadAllText(_file);
        json.Should().Contain("\"timestamp\"").And.Contain("\"items\"").And.Contain("\"name\"").And.Contain("\"price\"");

        var history = JsonSerializer.Deserialize<List<RewardRunRecord>>(json, ReadOpts)!;
        history[0].Timestamp.Should().BeCloseTo(when, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Record_RecoversFromCorruptFile_BackingItUp()
    {
        File.WriteAllText(_file, "{ this is not valid json");
        var recorder = new JsonRewardHistoryRecorder(_file);

        recorder.Record(Run(("Recovered Prime Blueprint", 5)));

        File.Exists(_file + ".bak").Should().BeTrue("the corrupt file is preserved for inspection");
        var history = ReadBack();
        history.Should().ContainSingle();
        history[0].Items[0].Name.Should().Be("Recovered Prime Blueprint");
    }
}
