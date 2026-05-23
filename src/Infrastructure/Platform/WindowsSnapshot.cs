namespace WarframeRelicOverlay.Infrastructure.Platform;
 
/// <summary>
/// Immutable snapshot of a window's client-area position and size.
/// Carries both physical-pixel values (for GDI screen capture) and
/// DPI scale factors (so callers can derive logical DIPs for WPF).
///
/// All pixel values refer to the <b>client area</b> — the renderable
/// surface inside the window chrome — which is what Warframe fills.
/// </summary>
public readonly record struct WindowSnapshot(
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight,
    double DpiScaleX,
    double DpiScaleY)
{
    /// <summary>Client width in WPF logical units (DIPs).</summary>
    public double LogicalWidth  => ClientWidth  / DpiScaleX;
 
    /// <summary>Client height in WPF logical units (DIPs).</summary>
    public double LogicalHeight => ClientHeight / DpiScaleY;
 
    /// <summary>Client left in WPF logical units.</summary>
    public double LogicalX => ClientX / DpiScaleX;
 
    /// <summary>Client top in WPF logical units.</summary>
    public double LogicalY => ClientY / DpiScaleY;
 
    /// <summary>Aspect ratio (e.g. ~1.778 for 16:9).</summary>
    public double AspectRatio => ClientHeight == 0 ? 0 : (double)ClientWidth / ClientHeight;
 
    /// <summary>
    /// Whether the dimensions look like a valid game window
    /// (filters out minimized or impossibly small windows).
    /// </summary>
    public bool IsValid => ClientWidth >= 320 && ClientHeight >= 240;
}
