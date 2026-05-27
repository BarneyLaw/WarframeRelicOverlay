namespace WarframeRelicOverlay.Core;

using WarframeRelicOverlay.OverlayApp.Pipeline;

/// <summary>
/// Output boundary between the coordinator and the presentation layer.
/// The coordinator calls these methods from background threads; the
/// implementation (typically the WPF ViewModel) is responsible for
/// dispatching to the UI thread.
///
/// Each method is intentionally fire-and-forget from the coordinator's
/// perspective — the coordinator never awaits a UI response.
/// </summary>
public interface IOverlayOutput
{
    /// <summary>
    /// Display the priced rewards on the overlay.  The implementation
    /// uses <see cref="CardResult.BoundsInWindow"/> and
    /// <see cref="PipelineResult.Window"/> to position price labels
    /// over the detected reward cards.
    /// </summary>
    void ShowPrices(PipelineResult result);

    /// <summary>
    /// Remove all price labels from the overlay (e.g. when the reward
    /// screen exits or Warframe stops).
    /// </summary>
    void ClearPrices();

    /// <summary>
    /// Show a loading indicator while the pipeline is running.
    /// </summary>
    void ShowLoading();

    /// <summary>
    /// Hide the loading indicator.
    /// </summary>
    void HideLoading();
}