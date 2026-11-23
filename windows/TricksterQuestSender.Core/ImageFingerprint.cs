namespace TricksterQuestSender.Core;

/// <summary>
/// Small "fingerprint" of a picture (difference hash), used to recognise dialog pages by how they
/// look instead of by OCR text. Same bit layout as the macOS app (32×16 bits, row by row, LSB first),
/// saved as base64 in presets.
/// </summary>
public static class ImageFingerprint
{
    public const int Columns = 32;
    public const int Rows = 16;
    /// <summary>Width of the grayscale sample the hash is computed from.</summary>
    public const int SampleWidth = Columns + 1;
    public const int SampleHeight = Rows;

    /// <summary>Distance at or below this = same page.</summary>
    public const double MatchThreshold = 0.12;
    /// <summary>The best page must beat the runner-up by this much, otherwise it's ambiguous.</summary>
    public const double RequiredMargin = 0.05;

    /// <summary>From a 33×16 grayscale sample (row-major, one byte per pixel).</summary>
    public static byte[] FromGraySample(byte[] gray)
    {
        if (gray.Length < SampleWidth * SampleHeight) throw new ArgumentException("sample too small", nameof(gray));
        var bytes = new byte[Columns * Rows / 8];
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                if (gray[y * SampleWidth + x] > gray[y * SampleWidth + x + 1])
                {
                    int bit = y * Columns + x;
                    bytes[bit / 8] |= (byte)(1 << (bit % 8));
                }
            }
        }
        return bytes;
    }

    /// <summary>0 = identical, 1 = completely different.</summary>
    public static double Distance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff += System.Numerics.BitOperations.PopCount((uint)(a[i] ^ b[i]));
        return (double)diff / (a.Length * 8);
    }

    /// <summary>
    /// The part of a dialog that identifies a page: the text above the "[1] ..." line and the options.
    /// Anchored on the option line, so it's the same box when recording and when running.
    /// </summary>
    public static ScreenRect PageRect(ScreenRect option, double scale) =>
        new(option.X - 40 * scale, option.Y - 110 * scale, 380 * scale, 220 * scale);
}
