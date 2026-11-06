using System.Drawing;
using System.Drawing.Imaging;
using Reach.Core;
using Reach.Mcp;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;

namespace Reach.Worker;

/// <summary>
/// Windows' own OCR (Windows.Media.Ocr): built in, offline, no Tesseract. Uses the languages the user has
/// installed with OCR support.
/// </summary>
internal static class Ocr
{
    private static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch (Exception e) { Console.Error.WriteLine("OCR unavailable: " + e.Message); return null; }
    });

    public static bool Available => Engine.Value != null;

    /// <summary>Lines of text on [bmp] whose top-left corner is at screen point (ox, oy), in screen pixels.</summary>
    public static List<OcrLine> Lines(Bitmap bmp, int ox, int oy)
    {
        var engine = Engine.Value ?? throw new ToolError("not_supported",
            "Windows OCR has no language: Settings › Time & language › Language & region › add a language (its OCR part installs with it)");
        // Small text reads better a little bigger; very big images are shrunk to what the engine takes.
        var max = (double)OcrEngine.MaxImageDimension;
        var scale = Math.Max(bmp.Width, bmp.Height) < 1200 ? 2.0 : 1.0;
        scale = Math.Min(scale, max / Math.Max(bmp.Width, bmp.Height));
        using var img = scale == 1.0 ? (Bitmap)bmp.Clone() : new Bitmap(bmp, (int)(bmp.Width * scale), (int)(bmp.Height * scale));
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var decoder = BitmapDecoder.CreateAsync(ms.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        using var sb = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var w in line.Words)
            {
                var rc = w.BoundingRect;
                l = Math.Min(l, rc.X); t = Math.Min(t, rc.Y); r = Math.Max(r, rc.X + rc.Width); b = Math.Max(b, rc.Y + rc.Height);
            }
            lines.Add(new OcrLine(line.Text, new Box(ox + (int)(l / scale), oy + (int)(t / scale), ox + (int)(r / scale), oy + (int)(b / scale))));
        }
        return lines;
    }

    /// <summary>Numbered boxes on a copy of [bmp] (marks view). Element bounds are screen pixels; (ox, oy) is the image's corner.</summary>
    public static Bitmap Marks(Bitmap bmp, int ox, int oy, IReadOnlyList<Element> elements, int scalePercent)
    {
        var o = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(o);
        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var k = Math.Max(1.0, scalePercent / 100.0);
        Color[] colors = { Color.FromArgb(255, 64, 129), Color.FromArgb(0, 145, 234), Color.FromArgb(255, 145, 0), Color.FromArgb(0, 170, 70) };
        using var font = new Font("Segoe UI", (float)(9 * k), FontStyle.Bold, GraphicsUnit.Point);
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var c = colors[i % colors.Length];
            using var pen = new Pen(c, (float)(2 * k));
            var rect = new Rectangle(e.Bounds.Left - ox, e.Bounds.Top - oy, e.Bounds.Width, e.Bounds.Height);
            g.DrawRectangle(pen, rect);
            var n = (i + 1).ToString();
            var size = g.MeasureString(n, font);
            var tag = new RectangleF(rect.Left, Math.Max(0, rect.Top - size.Height), size.Width + 2, size.Height);
            using var fill = new SolidBrush(c);
            g.FillRectangle(fill, tag);
            g.DrawString(n, font, Brushes.White, tag.Left + 1, tag.Top);
        }
        return o;
    }
}
