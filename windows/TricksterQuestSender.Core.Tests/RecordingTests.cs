using TricksterQuestSender.Core;
using Xunit;

namespace TricksterQuestSender.Core.Tests;

public class RecordingTests
{
    private static TextHit Hit(string text, double x, double y) => new(text, new ScreenRect(x - 50, y - 10, 100, 20));

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
    }

    [Fact]
    public async Task Recorded_pages_are_recognised_by_image_even_when_ocr_text_is_garbage()
    {
        var game = new FakeGame { GarbleOcr = true, LagReads = 3 };
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 2), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages.Length * 2, game.PressedOn.Count);
    }

    [Fact]
    public async Task A_page_that_was_never_recorded_stops_the_run()
    {
        var notEnough = new[] { FakeGame.RookiePages[0], FakeGame.RookiePages[1], "[1] ยังรวบรวมไม่ครบเลยนะ" };
        var game = new FakeGame(notEnough);
        var engine = new QuestEngine(game, game, new FakeClock(), new Random(1));

        var result = await engine.RunAsync(RecordedFlow(FakeGame.RookiePages, 3), new ScreenPoint(10, 10), CancellationToken.None);

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
}
