namespace WarframeRelicOverlay.Tests.Domain;

using System.IO;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using WarframeRelicOverlay.Core;

public class AppSettingsTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    // ── Load behaviour ────────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = AppSettings.Load(TempPath());

        settings.DetectionMode.Should().Be("EELog");
        settings.EeLogPathOverride.Should().BeNull();
        settings.DetectionIntervalMs.Should().Be(250);
        settings.DetectionStreak.Should().Be(2);
        settings.StabilizationDelayMs.Should().Be(1200);
        settings.PriceCacheTtlMinutes.Should().Be(5);
        settings.OverlayOpacity.Should().Be(1.0);
        settings.PriceFontSizeOverride.Should().Be(0);
        settings.ToggleHotkey.Should().Be("Shift+F9");
        settings.DebugMode.Should().BeFalse();
        settings.SaveDebugImages.Should().BeFalse();
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults_AndCreatesBak()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not json {{{");

            var settings = AppSettings.Load(path);

            settings.DetectionMode.Should().Be("EELog");
            File.Exists(path + ".bak").Should().BeTrue("corrupt file should be renamed to .bak");
            File.Exists(path).Should().BeFalse("original corrupt file should no longer exist");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservesAllProperties()
    {
        string path = TempPath();
        try
        {
            var original = new AppSettings
            {
                DetectionMode = "OCR",
                EeLogPathOverride = @"C:\custom\EE.log",
                DetectionIntervalMs = 500,
                DetectionStreak = 3,
                StabilizationDelayMs = 300,
                PriceCacheTtlMinutes = 10,
                OverlayOpacity = 0.8,
                PriceFontSizeOverride = 16,
                ToggleHotkey = "Ctrl+F10",
                DebugMode = true,
                SaveDebugImages = true,
            };

            original.Save(path);
            var loaded = AppSettings.Load(path);

            loaded.DetectionMode.Should().Be("OCR");
            loaded.EeLogPathOverride.Should().Be(@"C:\custom\EE.log");
            loaded.DetectionIntervalMs.Should().Be(500);
            loaded.DetectionStreak.Should().Be(3);
            loaded.StabilizationDelayMs.Should().Be(300);
            loaded.PriceCacheTtlMinutes.Should().Be(10);
            loaded.OverlayOpacity.Should().Be(0.8);
            loaded.PriceFontSizeOverride.Should().Be(16);
            loaded.ToggleHotkey.Should().Be("Ctrl+F10");
            loaded.DebugMode.Should().BeTrue();
            loaded.SaveDebugImages.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_PartialJson_FillsMissingPropertiesWithDefaults()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """{ "DebugMode": true }""");

            var settings = AppSettings.Load(path);

            settings.DebugMode.Should().BeTrue();
            settings.DetectionMode.Should().Be("EELog");
            settings.DetectionIntervalMs.Should().Be(250);
            settings.OverlayOpacity.Should().Be(1.0);
            settings.ToggleHotkey.Should().Be("Shift+F9");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_ProducesValidJson_ThatLoadsCleanly()
    {
        string path = TempPath();
        try
        {
            var original = new AppSettings { DebugMode = true };
            original.Save(path);

            File.Exists(path).Should().BeTrue();
            File.Exists(path + ".tmp").Should().BeFalse("temp file should be cleaned up after save");

            var act = () => AppSettings.Load(path);
            act.Should().NotThrow();
            act().DebugMode.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Validate / clamping ───────────────────────────────────────────────────

    [Fact]
    public void Validate_DetectionIntervalMs_ClampsLow()
    {
        var s = new AppSettings { DetectionIntervalMs = 50 };
        s.Validate();
        s.DetectionIntervalMs.Should().Be(100);
    }

    [Fact]
    public void Validate_DetectionIntervalMs_ClampsHigh()
    {
        var s = new AppSettings { DetectionIntervalMs = 5000 };
        s.Validate();
        s.DetectionIntervalMs.Should().Be(1000);
    }

    [Fact]
    public void Validate_OverlayOpacity_ClampsLow()
    {
        var s = new AppSettings { OverlayOpacity = 0.2 };
        s.Validate();
        s.OverlayOpacity.Should().Be(0.5);
    }

    [Fact]
    public void Validate_UnknownDetectionMode_FallsBackToEELog()
    {
        var s = new AppSettings { DetectionMode = "Banana" };
        var warnings = s.Validate();
        s.DetectionMode.Should().Be("EELog");
        warnings.Should().ContainMatch("*Banana*");
    }

    [Fact]
    public void Validate_EmptyHotkey_FallsBackToDefault()
    {
        var s = new AppSettings { ToggleHotkey = "" };
        s.Validate();
        s.ToggleHotkey.Should().Be("Shift+F9");
    }

    [Fact]
    public void Validate_NegativeFontSize_ClampsToZero()
    {
        var s = new AppSettings { PriceFontSizeOverride = -5 };
        s.Validate();
        s.PriceFontSizeOverride.Should().Be(0);
    }

    [Fact]
    public void Validate_ValidValues_ReturnsNoWarnings()
    {
        var s = new AppSettings();
        var warnings = s.Validate();
        warnings.Should().BeEmpty();
    }
}
