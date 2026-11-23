namespace TricksterQuestSender.Core;

/// <summary>Mouse and keyboard. Implemented per OS (SendInput on Windows, CGEvent on macOS, fakes in tests).</summary>
public interface IInputDriver
{
    ScreenPoint CursorPosition { get; }
    Task ClickAsync(ScreenPoint point, CancellationToken ct);
    Task PressKeyAsync(string key, CancellationToken ct);
}

/// <summary>The screenshot behind an OCR result; valid until the next read.</summary>
public interface IFrameImage
{
    /// <summary>Fingerprint of a screen area (screen coordinates), or null if it can't be taken.</summary>
    byte[]? Fingerprint(ScreenRect screenRect);
}

/// <summary>OCR result plus (optionally) the image it came from.</summary>
public sealed record ScreenFrame(IReadOnlyList<TextHit> Hits, IFrameImage? Image);

/// <summary>Screenshot + OCR.</summary>
public interface IScreenReader
{
    /// <summary>Like ReadScreenAsync, plus the screenshot (for page fingerprints).</summary>
    Task<ScreenFrame> ReadScreenFrameAsync(ScreenPoint near, CancellationToken ct);
    /// <summary>Like ReadRegionAsync, plus the screenshot. Null if the region is off-screen.</summary>
    Task<ScreenFrame?> ReadRegionFrameAsync(ScreenRect region, CancellationToken ct);

    /// <summary>Screen units per point (Windows: DPI / 96; macOS: 1). Scales offsets like NpcOffsetY.</summary>
    double PointScale { get; }
    Task<IReadOnlyList<TextHit>> ReadScreenAsync(ScreenPoint near, CancellationToken ct);
    Task<IReadOnlyList<TextHit>> ReadRegionAsync(ScreenRect region, CancellationToken ct);
    Task<IReadOnlyList<DisplayText>> ReadAllDisplaysAsync(CancellationToken ct);
}

/// <summary>Time source, replaceable in tests so they run instantly.</summary>
public interface IClock
{
    DateTime Now { get; }
    Task Delay(int milliseconds, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.UtcNow;
    public Task Delay(int milliseconds, CancellationToken ct) => Task.Delay(Math.Max(0, milliseconds), ct);
}
