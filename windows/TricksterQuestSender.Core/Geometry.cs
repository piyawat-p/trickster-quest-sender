namespace TricksterQuestSender.Core;

/// <summary>Screen coordinates (top-left origin). Pixels on Windows, points on macOS.</summary>
public readonly record struct ScreenPoint(double X, double Y)
{
    public double DistanceTo(ScreenPoint other) =>
        Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public ScreenPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(ScreenPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public ScreenRect Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public ScreenRect? Intersect(ScreenRect other)
    {
        double l = Math.Max(Left, other.Left), t = Math.Max(Top, other.Top);
        double r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? new ScreenRect(l, t, r - l, b - t) : null;
    }
}

/// <summary>One line of text read from the screen.</summary>
public sealed record TextHit(string Text, ScreenRect Rect)
{
    public ScreenPoint Center => Rect.Center;
}

/// <summary>All text read from one display.</summary>
public sealed record DisplayText(ScreenRect Bounds, IReadOnlyList<TextHit> Hits);
