using TricksterQuestSender.Core;

namespace TricksterQuestSender.Core.Tests;

/// <summary>Instant clock: Delay just moves time forward.</summary>
public sealed class FakeClock : IClock
{
    public DateTime Now { get; private set; } = new(2026, 1, 1);
    public Task Delay(int milliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Now = Now.AddMilliseconds(Math.Max(1, milliseconds));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A tiny simulation of the Rookie G quest: an NPC label on screen, a dialog with pages, and
/// a slow server (a key press only shows the next page after a few screen reads).
/// </summary>
public sealed class FakeGame : IInputDriver, IScreenReader
{
    public static readonly ScreenRect Display = new(0, 0, 1920, 1080);
    public static readonly ScreenPoint NpcLabel = new(900, 500);

    public static readonly string[] RookiePages =
    {
        "[1] ทำ [เควส]",
        "[1] 1. รวบรวม Chaos Integer.62Byte",
        "[1] ดูเหมือนจะขาดอะไรไปนะ...",
        "[1] รับบัคโค้ด [Holy]",
        "[1] เข้าใจแล้วจะช่วยหามาให้",
    };

    private readonly string[] _pages;
    /// <summary>Screen reads before a pressed key shows the next page (server lag).</summary>
    public int LagReads { get; set; }
    /// <summary>Key presses to ignore (lost presses), counted from the start.</summary>
    public HashSet<int> DropPresses { get; } = new();
    /// <summary>After the last page is pressed, the dialog stays until empty space is clicked.</summary>
    public bool StaysOpenUntilEmptyClick { get; set; } = true;

    public int Page { get; private set; } = -1; // -1 = closed
    public int KeyPresses { get; private set; }
    public List<string> PressedOn { get; } = new();
    public List<ScreenPoint> Clicks { get; } = new();
    public int CompletedTurnIns { get; private set; }
    public List<string> ChatLines { get; } = new() { "ได้รับไอเทม :[[Holy] Code C] Rookie G" };
    /// <summary>OCR reads every option line as the same garbage (only "[1]" survives).</summary>
    public bool GarbleOcr { get; set; }
    /// <summary>Dialog text above the options, per page (default: "ข้อความบทสนทนาหน้า N").</summary>
    public Func<int, string>? BodyText { get; set; }

    /// <summary>Stable fake "image" of a page: depends only on its text.</summary>
    public static byte[] HashOf(string pageText)
    {
        int seed = pageText.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        var bytes = new byte[ImageFingerprint.Columns * ImageFingerprint.Rows / 8];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class FakeImage(byte[]? hash) : IFrameImage
    {
        public byte[]? Fingerprint(ScreenRect screenRect) => hash;
    }

    private int _pendingAdvanceIn = -1;
    private bool _finishedRound;

    public FakeGame(string[]? pages = null) => _pages = pages ?? RookiePages;

    // ---- IInputDriver
    public ScreenPoint CursorPosition => Clicks.Count > 0 ? Clicks[^1] : new ScreenPoint(10, 10);

    public Task ClickAsync(ScreenPoint p, CancellationToken ct)
    {
        Clicks.Add(p);
        bool onNpc = Math.Abs(p.X - NpcLabel.X) < 40 && p.Y < NpcLabel.Y && p.Y > NpcLabel.Y - 80;
        if (onNpc && Page < 0) { Page = 0; _finishedRound = false; _pendingAdvanceIn = -1; }
        else if (!onNpc && _finishedRound) { Page = -1; _pendingAdvanceIn = -1; } // empty-space click closes it
        return Task.CompletedTask;
    }

    public Task PressKeyAsync(string key, CancellationToken ct)
    {
        int index = KeyPresses++;
        if (Page < 0 || key != "1") return Task.CompletedTask;
        PressedOn.Add(_pages[Page]);
        if (DropPresses.Contains(index)) return Task.CompletedTask;
        if (Page == _pages.Length - 1 && !_finishedRound)
        {
            // Accepting the quest again: the server takes it right away.
            CompletedTurnIns++;
            _finishedRound = true;
        }
        if (_pendingAdvanceIn < 0) _pendingAdvanceIn = LagReads;
        return Task.CompletedTask;
    }

    // ---- IScreenReader
    public double PointScale => 1;

    public Task<IReadOnlyList<TextHit>> ReadScreenAsync(ScreenPoint near, CancellationToken ct) =>
        Task.FromResult(Read());

    public Task<IReadOnlyList<TextHit>> ReadRegionAsync(ScreenRect region, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TextHit>>(Read().Where(h => region.Contains(h.Center)).ToList());

    public Task<ScreenFrame> ReadScreenFrameAsync(ScreenPoint near, CancellationToken ct)
    {
        var hits = Read();
        return Task.FromResult(new ScreenFrame(hits, new FakeImage(Page >= 0 ? HashOf(_pages[Page]) : null)));
    }

    public Task<ScreenFrame?> ReadRegionFrameAsync(ScreenRect region, CancellationToken ct)
    {
        var hits = Read().Where(h => region.Contains(h.Center)).ToList();
        return Task.FromResult<ScreenFrame?>(new ScreenFrame(hits, new FakeImage(Page >= 0 ? HashOf(_pages[Page]) : null)));
    }

    public Task<IReadOnlyList<DisplayText>> ReadAllDisplaysAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DisplayText>>(new[] { new DisplayText(Display, Read()) });

    private IReadOnlyList<TextHit> Read()
    {
        Tick();
        var hits = new List<TextHit>
        {
            new("Rookie G", new ScreenRect(NpcLabel.X - 40, NpcLabel.Y - 10, 80, 20)),
        };
        hits.AddRange(ChatLines.Select((l, i) => new TextHit(l, new ScreenRect(10, 900 + i * 20, 600, 18))));
        if (Page >= 0)
        {
            hits.Add(new TextHit("Rookie G", new ScreenRect(1000, 100, 120, 20))); // dialog title
            hits.Add(new TextHit(BodyText?.Invoke(Page) ?? "ข้อความบทสนทนาหน้า " + Page, new ScreenRect(1000, 140, 300, 20)));
            hits.Add(new TextHit(GarbleOcr ? "[1] ฑฒณ?? ญญ" : _pages[Page], new ScreenRect(1010, 220, 300, 20)));
        }
        return hits;
    }

    private void Tick()
    {
        if (_pendingAdvanceIn < 0) return;
        if (_pendingAdvanceIn-- > 0) return;
        _pendingAdvanceIn = -1;
        if (Page < 0) return;
        if (Page == _pages.Length - 1)
        {
            if (!StaysOpenUntilEmptyClick) Page = -1;
            return;
        }
        Page++;
    }
}
