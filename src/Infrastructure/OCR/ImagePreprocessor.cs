namespace WarframeRelicOverlay.Infrastructure.OCR;

using System.Drawing;

public static class ImagePreprocessor
{
    /// <summary>
    /// Converts a raw reward-box screenshot to a clean binary bitmap suitable for Tesseract.
    /// Uses LockBits for performance (avoids GetPixel/SetPixel), Otsu's threshold for accuracy,
    /// and inverts when needed so the output is always dark text on white background.
    /// </summary>
    public static Bitmap Prepare(Bitmap source)
    {
        int width = source.Width;
        int height = source.Height;
        byte[] luminances = new byte[width * height];

        // --- Pass 1: read luminance values for every pixel via LockBits ---
        var rect = new Rectangle(0, 0, width, height);
        var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        unsafe
        {
            byte* src = (byte*)srcData.Scan0;
            for (int y = 0; y < height; y++)
            {
                byte* row = src + y * srcData.Stride;
                for (int x = 0; x < width; x++)
                {
                    byte b = row[x * 3];
                    byte g = row[x * 3 + 1];
                    byte r = row[x * 3 + 2];
                    luminances[y * width + x] = (byte)(0.114 * b + 0.587 * g + 0.299 * r);
                }
            }
        }

        source.UnlockBits(srcData);

        // Otsu threshold: find the optimal split between background and foreground ---
        byte threshold = OtsuThreshold(luminances);

        // Determine polarity: if most pixels are dark the background is dark (gold text on dark
        // Warframe UI) and the text is bright — invert so Tesseract gets black text on white.
        int brightCount = 0;
        foreach (byte v in luminances)
            if (v > threshold) brightCount++;
        bool invert = brightCount < luminances.Length / 2; // more dark pixels → invert

        // --- Pass 2: write binary output ---
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        unsafe
        {
            byte* dst = (byte*)dstData.Scan0;
            for (int y = 0; y < height; y++)
            {
                byte* row = dst + y * dstData.Stride;
                for (int x = 0; x < width; x++)
                {
                    bool aboveThreshold = luminances[y * width + x] > threshold;
                    byte val = (aboveThreshold ^ invert) ? (byte)255 : (byte)0;
                    row[x * 3]     = val;
                    row[x * 3 + 1] = val;
                    row[x * 3 + 2] = val;
                }
            }
        }

        result.UnlockBits(dstData);
        return result;
    }

    private static byte OtsuThreshold(byte[] pixels)
    {
        int[] hist = new int[256];
        foreach (byte p in pixels) hist[p]++;

        int total = pixels.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * hist[i];

        double sumB = 0;
        int wB = 0;
        double maxVariance = 0;
        byte threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;

            sumB += t * hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;

            double variance = (double)wB * wF * (mB - mF) * (mB - mF);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = (byte)t;
            }
        }

        return threshold;
    }
}
