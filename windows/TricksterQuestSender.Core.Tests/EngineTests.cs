using TricksterQuestSender.Core;
using Xunit;

namespace TricksterQuestSender.Core.Tests;

public class EngineTests
{
    private static readonly ScreenPoint EmptySpace = new(200, 200);

    private static QuestConfig MainFlow(int loops, string pages = "")
    {
        var cfg = QuestConfig.CreateDefault();
        cfg.Unlimited = false;
        cfg.Loops = loops;
        cfg.JitterMs = 0;
        cfg.Steps[1].Pages = pages;
        cfg.Steps[2].X = EmptySpace.X; // "click empty space" step
        cfg.Steps[2].Y = EmptySpace.Y;
        return cfg;
    }

    private static (QuestEngine engine, FakeClock clock) Make(FakeGame game)
    {
        var clock = new FakeClock();
        return (new QuestEngine(game, game, clock, new Random(1)), clock);
    }

    [Fact]
    public async Task One_round_presses_each_page_exactly_once()
    {
        var game = new FakeGame();
        var (engine, _) = Make(game);

        var result = await engine.RunAsync(MainFlow(1), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(1, result.Completed);
        Assert.Equal(1, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages, game.PressedOn);
        Assert.Equal(-1, game.Page); // closed by the empty-space click
    }

    [Fact]
    public async Task Slow_server_does_not_make_it_skip_pages()
    {
        var game = new FakeGame { LagReads = 6 };
        var (engine, _) = Make(game);

        var result = await engine.RunAsync(MainFlow(3), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(3, result.Completed);
        Assert.Equal(3, game.CompletedTurnIns);
        Assert.Equal(FakeGame.RookiePages.Length * 3, game.PressedOn.Count);
    }

    [Fact]
    public async Task Lost_key_press_is_retried()
    {
        var game = new FakeGame();
        game.DropPresses.Add(2); // third press never reaches the game
        var (engine, _) = Make(game);

        var result = await engine.RunAsync(MainFlow(1), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(1, result.Completed);
        Assert.Equal(FakeGame.RookiePages.Length + 1, game.PressedOn.Count);
        Assert.Equal(FakeGame.RookiePages[2], game.PressedOn[3]); // pressed the same page again
    }

    [Fact]
    public async Task Last_page_is_never_pressed_twice_even_if_dialog_stays_open()
    {
        var game = new FakeGame { LagReads = 30 };
        var (engine, _) = Make(game);

        await engine.RunAsync(MainFlow(1), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Equal(1, game.PressedOn.Count(p => p == FakeGame.RookiePages[^1]));
        Assert.Equal(1, game.CompletedTurnIns);
    }

    [Fact]
    public async Task Changed_wording_stops_the_run()
    {
        var notEnough = new[] { FakeGame.RookiePages[0], FakeGame.RookiePages[1], "[1] ยังรวบรวมไม่ครบเลยนะ" };
        var game = new FakeGame(notEnough);
        var (engine, _) = Make(game);

        var result = await engine.RunAsync(MainFlow(5, string.Join("\n", FakeGame.RookiePages)),
                                           new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ข้อความเปลี่ยน", result.Message);
        Assert.Equal(0, result.Completed);
    }

    [Fact]
    public async Task Dialog_closing_before_the_end_page_stops_the_run()
    {
        var shortDialog = new[] { FakeGame.RookiePages[0], "[1] ไว้คราวหน้านะ" };
        var game = new FakeGame(shortDialog) { StaysOpenUntilEmptyClick = false };
        var (engine, _) = Make(game);

        var result = await engine.RunAsync(MainFlow(5), new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ปิดก่อนถึงหน้าสุดท้าย", result.Message);
    }

    [Fact]
    public async Task Missing_npc_stops_with_a_message()
    {
        var game = new FakeGame();
        var (engine, _) = Make(game);
        var cfg = MainFlow(1);
        cfg.Steps[0].Text = "Somebody Else";

        var result = await engine.RunAsync(cfg, new ScreenPoint(10, 10), CancellationToken.None);

        Assert.Contains("ไม่เจอ", result.Message);
        Assert.Empty(game.PressedOn);
    }
}
