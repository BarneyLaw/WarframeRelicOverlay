namespace WarframeRelicOverlay.Infrastructure.OCR;

using Microsoft.Extensions.ObjectPool;
using Tesseract;
using WinImageFormat = System.Drawing.Imaging.ImageFormat;

public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly ObjectPool<TesseractEngine> _pool;
    private readonly List<TesseractEngine> _created = [];
    private readonly object _lock = new();
    private bool _disposed;

    public TesseractOcrEngine(string tessDataPath, int poolSize = 4)
    {
        var policy = new EnginePoolPolicy(tessDataPath, _created, _lock);
        _pool = new DefaultObjectPool<TesseractEngine>(policy, poolSize);
    }

    public string Recognize(System.Drawing.Bitmap image)
    {
        var engine = _pool.Get();
        try
        {
            using var pix = BitmapToPix(image);
            // SingleBlock (not SingleLine): reward names wrap to two lines on
            // narrow 4-card layouts, and the header is a single line — both are
            // uniform text blocks. SingleLine crushes stacked lines into garbage.
            using var page = engine.Process(pix, PageSegMode.SingleBlock);
            return page.GetText().Trim();
        }
        finally
        {
            _pool.Return(engine);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var engine in _created)
                engine.Dispose();
            _created.Clear();
        }
    }

    // Encodes the bitmap to BMP in memory so Tesseract can load it as a Pix.
    private static Pix BitmapToPix(System.Drawing.Bitmap bitmap)
    {
        using var ms = new System.IO.MemoryStream();
        bitmap.Save(ms, WinImageFormat.Bmp);
        return Pix.LoadFromMemory(ms.ToArray());
    }

    private sealed class EnginePoolPolicy(
        string tessDataPath,
        List<TesseractEngine> created,
        object lockObj) : IPooledObjectPolicy<TesseractEngine>
    {
        public TesseractEngine Create()
        {
            var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            // Set whitelist once at creation to alphabet and digits only, improving accuracy and speed for our use case.
            engine.SetVariable("tessedit_char_whitelist",
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ");
            lock (lockObj) { created.Add(engine); }
            return engine;
        }

        public bool Return(TesseractEngine _) => true;
    }
}
