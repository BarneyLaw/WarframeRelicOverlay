namespace WarframeRelicOverlay.Infrastructure.OCR;

/// <summary>
/// Interface for OCR engines. Abstracts away the specific OCR library (e.g. Tesseract) so it can be swapped out or mocked for testing.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Recognizes text from the given image. The image is expected to be preprocessed (e.g. binarized, cropped) for best results.
    /// </summary>
    /// <param name="image"></param>
    /// <returns></returns>
    string Recognize(System.Drawing.Bitmap image);
}
