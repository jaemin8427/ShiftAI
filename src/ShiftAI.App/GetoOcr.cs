using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ShiftAI.App;

/// <summary>One OCR line with its bounding box in window-local pixels.</summary>
public sealed record OcrToken(string Text, Rectangle Bounds);

/// <summary>
/// Thin wrapper over the built-in Windows OCR engine (Windows.Media.Ocr). Used to locate Geto
/// product cards / the 담기 button by their Korean text instead of blind coordinate ratios, and to
/// verify the cart after adding an item. Degrades gracefully (Available == false) if no Korean OCR
/// language is installed.
/// </summary>
public static class GetoOcr
{
    private static readonly OcrEngine? Engine = CreateEngine();

    public static bool Available => Engine is not null;

    private static OcrEngine? CreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language("ko"))
                ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return null;
        }
    }

    // Small Korean UI text (product-card labels) reads far more reliably when upscaled before OCR.
    private const double Scale = 2.0;

    public static List<OcrToken> Read(Bitmap bitmap)
    {
        var tokens = new List<OcrToken>();
        if (Engine is null)
        {
            return tokens;
        }

        try
        {
            using var scaled = Upscale(bitmap, Scale);
            using var software = ToSoftwareBitmap(scaled);
            var result = Engine.RecognizeAsync(software).GetAwaiter().GetResult();
            foreach (var line in result.Lines)
            {
                var bounds = UnionWords(line, Scale); // mapped back to original window pixels
                if (bounds is { } rect)
                {
                    tokens.Add(new OcrToken(line.Text, rect));
                }
            }
        }
        catch
        {
            // OCR failure must never break the order flow; caller falls back to coordinates.
        }

        return tokens;
    }

    private static Bitmap Upscale(Bitmap source, double scale)
    {
        if (scale <= 1.0)
        {
            return new Bitmap(source);
        }

        var scaled = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale));
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        return scaled;
    }

    private static Rectangle? UnionWords(OcrLine line, double scale)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        var any = false;
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
            any = true;
        }

        return any
            ? Rectangle.FromLTRB((int)(left / scale), (int)(top / scale), (int)(right / scale), (int)(bottom / scale))
            : null;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var random = stream.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(random).GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .GetAwaiter().GetResult();
    }
}
