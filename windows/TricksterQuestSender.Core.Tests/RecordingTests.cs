using TricksterQuestSender.Core;
using Xunit;

namespace TricksterQuestSender.Core.Tests;

public class RecordingTests
{
    private static TextHit Hit(string text, double x, double y) => new(text, new ScreenRect(x - 50, y - 10, 100, 20));

    /// <summary>Like the real game: the last page says "[Chaos Integer.62Byte]" in its text.</summary>
    private const string LastPageBody = "โค้ดยิ่งมีค่ามาก [Chaos Integer.62Byte] ช่วยหามาใหม่อีก 50 ชิ้น";

    private static FakeGame RealisticGame(string[]? pages = null, bool garble = false) => new(pages)
    {
        GarbleOcr = garble,
        BodyText = page => page == 4 ? LastPageBody : "ข้อความบทสนทนาหน้า " + page,
    };

    private static QuestConfig RecordedFlow(string[] recordedPages, int loops)
    {
        // What a recording of the normal Rookie G round produces.
        var actions = new List<RecordedAction>
        {
            new(RecordedActionKind.Click, new ScreenPoint(900, 465), "", new[] { Hit("Rookie G", 900, 500) }, null),
        };
        foreach (var page in recordedPages)
            actions.Add(new(RecordedActionKind.Key, default, "1", new[] { Hit(page, 1160, 230) }, FakeGame.HashOf(page)));
        actions.Add(new(RecordedActionKind.Click, new ScreenPoint(200, 200), "", Array.Empty<TextHit>(), null));

        var cfg = QuestConfig.CreateDefault();
        cfg.Steps = new(RecordingAnalyzer.MakeSteps(actions));
        cfg.Unlimited = false;
        cfg.Loops = loops;
        cfg.JitterMs = 0;
        return cfg;
    }

    [Fact]
    public void Recording_becomes_npc_then_dialog_then_close_click()
    {
        var cfg = RecordedFlow(FakeGame.RookiePages, 1);

        Assert.Equal(new[] { StepKind.ClickNPC, StepKind.DialogUntilEnd, StepKind.ClickPoint },
                     cfg.Steps.Select(s => s.Kind));
        Assert.Equal(-35, cfg.Steps[0].NpcOffsetY);
        Assert.Contains("Rookie G", cfg.Steps[0].Text);

        var dialog = cfg.Steps[1];
        Assert.Equal(FakeGame.RookiePages, dialog.KnownPages);
        Assert.Equal(FakeGame.RookiePages[^1], dialog.EndText);
        Assert.All(dialog.KnownPageHashes, h => Assert.NotNull(h));

        Assert.Equal(new ScreenPoint(200, 200), cfg.Steps[2].Point);
        Assert.Equal(62, dialog.Floor); // read from "Chaos Integer.62Byte" while recording
    }

    [Fact]
    public async Task Recorded_pages_are_recognised_by_image_even_when_ocr_text_is_garbage()
    {
        var game = RealisticGame(garble: true);
        game.LagReads = 3;
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages.Length * 2, game.PressedOn.Count);
    }

    [Fact]
    public async Task Not_enough_items_never_counts_as_a_turn_in()
    {
        // The dialog takes another turn and closes ("not enough"). The recording has a floor, so an
        // unknown page doesn't stop right away (system check takes over), but the round can't end
        // without "[Chaos Integer.62Byte]", so the run stops with nothing handed in.
        var notEnough = new[] { FakeGame.RookiePages[0], FakeGame.RookiePages[1], "[1] ยังรวบรวมไม่ครบเลยนะ" };
        var game = new FakeGame(notEnough) { StaysOpenUntilEmptyClick = false };
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 3), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.StartsWith("หยุด", result.Message);
        Assert.Equal(0, result.Completed);
    }

    [Fact]
    public async Task Without_a_floor_a_page_that_was_never_recorded_stops_right_away()
    {
        var notEnough = new[] { FakeGame.RookiePages[0], FakeGame.RookiePages[1], "[1] ยังรวบรวมไม่ครบเลยนะ" };
        var game = new FakeGame(notEnough);
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));
        var cfg = RecordedFlow(FakeGame.RookiePages, 3);
        cfg.Steps[1].Floor = 0;

        var result = await engine.RunAsync(cfg, new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ข้อความเปลี่ยน", result.Message);
        Assert.Equal(0, game.CompletedTurnIns);
    }

    [Fact]
    public void Page_images_survive_json_round_trip()
    {
        var cfg = RecordedFlow(FakeGame.RookiePages, 1);
        string json = System.Text.Json.JsonSerializer.Serialize(cfg, QuestJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<QuestConfig>(json, QuestJson.Options)!;

        Assert.Equal(cfg.Steps[1].PageHashes, back.Steps[1].PageHashes);
        Assert.Equal(FakeGame.HashOf(FakeGame.RookiePages[2]), back.Steps[1].KnownPageHashes[2]);
    }

    [Fact]
    public async Task Ocr_missing_the_option_for_a_few_reads_does_not_end_the_round()
    {
        // Windows OCR drops "[1]" on 3 out of every 4 reads; the dialog never actually closes.
        var game = RealisticGame();
        game.LagReads = 2;
        game.OcrMissesOption = read => read % 4 != 0;
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
    }

    [Fact]
    public async Task A_page_whose_option_ocr_never_reads_is_handled_by_its_picture()
    {
        var game = RealisticGame();
        game.UnreadableOptionPages.Add(2);
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages.Length * 2, game.PressedOn.Count); // still one press per page
    }

    [Fact]
    public async Task A_close_click_that_does_not_close_the_quest_window_is_clicked_again()
    {
        var game = RealisticGame();
        game.IgnoreCloseClicks = 1;
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
    }

    [Fact]
    public async Task A_quest_window_that_never_closes_stops_with_a_clear_message()
    {
        var game = RealisticGame();
        game.IgnoreCloseClicks = 99;
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ปิดเควส", result.Message);
        Assert.Equal(1, result.Completed);
    }

    [Fact]
    public async Task Npc_label_misread_by_ocr_is_still_found()
    {
        var game = RealisticGame();
        game.NpcLabelText = "Rookie 6";
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 1), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(1, result.Completed);
    }
}
