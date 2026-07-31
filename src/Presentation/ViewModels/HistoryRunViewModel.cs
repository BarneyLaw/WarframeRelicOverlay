namespace WarframeRelicOverlay.Presentation.ViewModels;

using System.Collections.ObjectModel;

/// <summary>
/// View model for a single reward-run entry in the history tab.
/// </summary>
public sealed class HistoryRunViewModel
{
    /// <summary>Formatted timestamp (e.g. "2025-06-15 14:32").</summary>
    public required string Timestamp { get; init; }

    /// <summary>Individual items in this run, each with name, price, and highlight state.</summary>
    public required ObservableCollection<HistoryItemViewModel> Items { get; init; }
}
