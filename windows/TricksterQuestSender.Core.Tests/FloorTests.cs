using TricksterQuestSender.Core;
using Xunit;

namespace TricksterQuestSender.Core.Tests;

public class FloorTests
{
    private static TextHit[] Lines(params string[] texts) =>
        texts.Select((t, i) => new TextHit(t, new ScreenRect(0, i * 20, 300, 18))).ToArray();

    [Theory]
    [InlineData("โค้ดยิ่งมีค่ามาก [Chaos Integer.2Byte] ช่วยหามาใหม่", 2, true)]
    [InlineData("[Chaos lnteger. 2 Byte]", 2, true)]          // OCR spacing and l for I
    [InlineData("Chaos Integer.2Byte]", 2, true)]
    [InlineData("[1] 1. รวบรวม Chaos Integer.2Byte", 2, false)] // option line: no brackets
    [InlineData("[Chaos Integer.62Byte]", 2, false)]           // 62 is not floor 2
    [InlineData("[Chaos Integer.62Byte]", 62, true)]
    public void Last_page_marker_needs_the_bracketed_item_of_that_floor(string line, int floor, bool expected)
    {
        Assert.Equal(expected, TextMatching.HasFloorItem(Lines(line), floor, bracketed: true));
    }

    [Fact]
    public void Item_without_brackets_still_counts_as_seen()
    {
        Assert.True(TextMatching.HasFloorItem(Lines("[1] 1. รวบรวม Chaos Integer.2Byte"), 2, bracketed: false));
        Assert.False(TextMatching.HasFloorItem(Lines("[1] 1. รวบรวม Chaos Integer.12Byte"), 2, bracketed: false));
    }

    [Fact]
    public void Floor_is_read_from_the_item_name()
    {
        Assert.Equal(62, TextMatching.FindFloor(Lines("ข้อความ", "[1] 1. รวบรวม Chaos Integer.62Byte")));
        Assert.Null(TextMatching.FindFloor(Lines("ไม่มีไอเทม")));
    }

    [Fact]
    public void Alternatives_split_on_bar()
    {
        Assert.Equal(new[] { "ไม่มีทาง", "เข้าใจแล้ว" }, TextMatching.Alternatives(" ไม่มีทาง | เข้าใจแล้ว |"));
    }

    private static QuestConfig DefaultFlow(int floor, string endText, int loops)
    {
        var cfg = QuestConfig.CreateDefault();
        cfg.Unlimited = false;
        cfg.Loops = loops;
        cfg.JitterMs = 0;
        cfg.Steps[1].Floor = floor;
        cfg.Steps[1].EndText = endText;
        cfg.Steps[2].X = 200;
        cfg.Steps[2].Y = 200;
        return cfg;
    }

    private static FakeGame ThaiUnreadableGame() => new()
    {
        GarbleOcr = true,   // Thai option lines come out as garbage
        LagReads = 3,
        // The last page's text mentions the item in brackets, like the real game.
        BodyText = page => page == 4 ? "โค้ดยิ่งมีค่ามาก [Chaos Integer.62Byte] ช่วยหามาใหม่อีก 50 ชิ้น" : "ฑฒณ ญญ " + page,
    };

    [Fact]
    public async Task Default_flow_finishes_rounds_by_floor_even_when_thai_is_unreadable()
    {
        var game = ThaiUnreadableGame();
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(DefaultFlow(62, "เข้าใจแล้ว", 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages.Length * 2, game.PressedOn.Count);
    }

    [Fact]
    public async Task Either_end_condition_is_enough()
    {
        // Thai readable this time: the end text matches even though no floor is set.
        var game = new FakeGame();
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(DefaultFlow(0, "ไม่มีทาง | เข้าใจแล้ว", 1), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(1, result.Completed);
        Assert.Equal(1, game.PressedOn.Count(p => p == FakeGame.RookiePages[^1]));
    }

    [Fact]
    public async Task Wrong_floor_never_counts_as_success()
    {
        var game = ThaiUnreadableGame();
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(DefaultFlow(3, "เข้าใจแล้ว", 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(0, result.Completed);
        Assert.StartsWith("หยุด", result.Message);
    }

    [Fact]
    public async Task Recorded_pages_that_no_longer_match_fall_back_to_the_floor_check()
    {
        // Recorded on another machine: none of the page images match, and Thai OCR is garbage.
        var cfg = DefaultFlow(62, "เข้าใจแล้ว", 2);
        var dialog = cfg.Steps[1];
        dialog.Pages = string.Join("\n", FakeGame.RookiePages);
        dialog.PageHashes = string.Join("\n", FakeGame.RookiePages.Select(p => Convert.ToBase64String(FakeGame.HashOf("other " + p))));
        var game = ThaiUnreadableGame();
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(cfg, new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(FakeGame.RookiePages.Length * 2, game.PressedOn.Count);
    }

    [Fact]
    public async Task Without_a_floor_an_unrecorded_page_still_stops_the_run()
    {
        var cfg = DefaultFlow(0, "เข้าใจแล้ว", 2);
        cfg.Steps[1].Pages = "[1] หน้าที่ไม่มีในเกมนี้";
        var game = new FakeGame();
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(cfg, new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ข้อความเปลี่ยน", result.Message);
    }
}
