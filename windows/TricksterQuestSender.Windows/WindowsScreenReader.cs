using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TricksterQuestSender.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace QuestSenderWin;

/// <summary>Screenshot (GDI) + Windows' built-in OCR. This app's own window is excluded from captures.</summary>
internal sealed class WindowsScreenReader : IScreenReader
{
    private readonly OcrEngine _ocr;
    private readonly object _lastLock = new();
    /// <summary>Screenshot behind the last frame read (for page fingerprints); replaced on the next read.</summary>
    private (Bitmap Image, ScreenRect Area, int Generation)? _last;
    private int _generation;
    /// <summary>One OCR at a time (the recorder analyses frames in the background).</summary>
    private readonly SemaphoreSlim _ocrGate = new(1, 1);

    public WindowsScreenReader()
    {
        _ocr = OcrEngine.TryCreateFromLanguage(new Language("th"))
               ?? OcrEngine.TryCreateFromUserProfileLanguages()
               ?? throw new InvalidOperationException("Windows ไม่มีภาษาสำหรับ OCR — เพิ่มภาษาไทยใน Settings → Time & language → Language");
    }

    public static bool ThaiAvailable => OcrEngine.IsLanguageSupported(new Language("th"));

    public string LanguageName => _ocr.RecognizerLanguage.DisplayName;

    public double PointScale => Native.GetDpiForSystem() / 96.0;

    // ------------------------------------------------------------------ IScreenReader

    public Task<IReadOnlyList<TextHit>> ReadScreenAsync(ScreenPoint near, CancellationToken ct) =>
        ReadAsync(MonitorAt(near), ct);

    public Task<IReadOnlyList<TextHit>> ReadRegionAsync(ScreenRect region, CancellationToken ct)
    {
        var area = region.Intersect(MonitorAt(region.Center));
        return area is { } a ? ReadAsync(a, ct) : Task.FromResult<IReadOnlyList<TextHit>>(Array.Empty<TextHit>());
    }

    public async Task<IReadOnlyList<DisplayText>> ReadAllDisplaysAsync(CancellationToken ct)
    {
        var result = new List<DisplayText>();
        foreach (var bounds in Native.MonitorBounds())
            result.Add(new DisplayText(bounds, await ReadAsync(bounds, ct)));
        return result;
    }

    public Task<ScreenFrame> ReadScreenFrameAsync(ScreenPoint near, CancellationToken ct) =>
        ReadFrameAsync(MonitorAt(near), ct);

    public async Task<ScreenFrame?> ReadRegionFrameAsync(ScreenRect region, CancellationToken ct)
    {
        var area = region.Intersect(MonitorAt(region.Center));
        return area is { } a ? await ReadFrameAsync(a, ct) : null;
    }

    // ------------------------------------------------------------------ capture / OCR / fingerprint

    public static ScreenRect MonitorAt(ScreenPoint p)
    {
        var monitors = Native.MonitorBounds();
        return monitors.FirstOrDefault(m => m.Contains(p), monitors.Count > 0 ? monitors[0] : new ScreenRect(0, 0, 1920, 1080));
    }

    /// <summary>Screenshot of an area (physical pixels). Caller disposes it.</summary>
    public static Bitmap Capture(ScreenRect area)
    {
        int w = Math.Max(1, (int)area.Width), h = Math.Max(1, (int)area.Height);
        var shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen((int)area.X, (int)area.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return shot;
    }

    /// <summary>OCR of a screenshot that covers <paramref name="area"/>.</summary>
    public async Task<IReadOnlyList<TextHit>> RecognizeAsync(Bitmap shot, ScreenRect area, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int w = shot.Width, h = shot.Height;
        if (w < 8 || h < 8) return Array.Empty<TextHit>();

        // Windows OCR has a maximum image size; shrink big screens and map coordinates back.
        double scale = 1;
        Bitmap source = shot;
        Bitmap? shrunk = null;
        uint max = OcrEngine.MaxImageDimension;
        if (w > max || h > max)
        {
            scale = Math.Min((double)max / w, (double)max / h);
            shrunk = new Bitmap(shot, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
            source = shrunk;
        }
        // Pixels per screen unit (the shot is 1:1 with the screen, so normally 1).
        double sx = shot.Width / area.Width, sy = shot.Height / area.Height;

        try
        {
            using var bitmap = ToSoftwareBitmap(source);
            OcrResult result;
            await _ocrGate.WaitAsync(ct);
            try { result = await _ocr.RecognizeAsync(bitmap); }
            finally { _ocrGate.Release(); }
            var hits = new List<TextHit>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double l = line.Words.Min(x => x.BoundingRect.X);
                double t = line.Words.Min(x => x.BoundingRect.Y);
                double r = line.Words.Max(x => x.BoundingRect.X + x.BoundingRect.Width);
                double b = line.Words.Max(x => x.BoundingRect.Y + x.BoundingRect.Height);
                hits.Add(new TextHit(line.Text, new ScreenRect(
                    area.X + l / scale / sx, area.Y + t / scale / sy, (r - l) / scale / sx, (b - t) / scale / sy)));
            }
            return hits;
        }
        finally
        {
            shrunk?.Dispose();
        }
    }

    /// <summary>Fingerprint of <paramref name="screenRect"/> inside a screenshot that covers <paramref name="area"/>.</summary>
    public static byte[]? Fingerprint(Bitmap shot, ScreenRect area, ScreenRect screenRect)
    {
        double sx = shot.Width / area.Width, sy = shot.Height / area.Height;
        var crop = new ScreenRect((screenRect.X - area.X) * sx, (screenRect.Y - area.Y) * sy,
                                  screenRect.Width * sx, screenRect.Height * sy)
            .Intersect(new ScreenRect(0, 0, shot.Width, shot.Height));
        if (crop is not { Width: >= 8, Height: >= 8 } c) return null;

        const int w = ImageFingerprint.SampleWidth, h = ImageFingerprint.SampleHeight;
        using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, new Rectangle(0, 0, w, h),
                        new RectangleF((float)c.X, (float)c.Y, (float)c.Width, (float)c.Height), GraphicsUnit.Pixel);
        }

        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * data.Stride + x * 4;
                    gray[y * w + x] = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
                }
            }
            return ImageFingerprint.FromGraySample(gray);
        }
        finally
        {
            small.UnlockBits(data);
        }
    }

    private async Task<IReadOnlyList<TextHit>> ReadAsync(ScreenRect area, CancellationToken ct)
    {
        using var shot = Capture(area);
        return await RecognizeAsync(shot, area, ct);
    }

    private async Task<ScreenFrame> ReadFrameAsync(ScreenRect area, CancellationToken ct)
    {
        var shot = Capture(area);
        int generation;
        lock (_lastLock)
        {
            _last?.Image.Dispose();
            generation = ++_generation;
            _last = (shot, area, generation);
        }
        var hits = await RecognizeAsync(shot, area, ct);
        return new ScreenFrame(hits, new LastFrameImage(this, generation));
    }

    private byte[]? FingerprintLast(int generation, ScreenRect screenRect)
    {
        lock (_lastLock)
        {
            if (_last is not { } last || last.Generation != generation) return null; // a newer read replaced it
            return Fingerprint(last.Image, last.Area, screenRect);
        }
    }

    private sealed class LastFrameImage(WindowsScreenReader reader, int generation) : IFrameImage
    {
        public byte[]? Fingerprint(ScreenRect screenRect) => reader.FingerprintLast(generation, screenRect);
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly,
                                PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
            sb.CopyFromBuffer(bytes.AsBuffer());
            return sb;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
