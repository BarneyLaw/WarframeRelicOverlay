namespace WarframeRelicOverlay.Infrastructure.OCR;

public interface IOcrEngine
{
    string Recognize(System.Drawing.Bitmap image);
}
