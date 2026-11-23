using System.Text.Json;
using TricksterQuestSender.Core;
using Xunit;

namespace TricksterQuestSender.Core.Tests;

public class TextMatchingTests
{
    private static TextHit Hit(string text, double x = 0, double y = 0) => new(text, new ScreenRect(x, y, 100, 20));

    [Theory]
    [InlineData("[1] ทำ [เควส]")]
    [InlineData("[l] ทำ [เควส]")]   // OCR read 1 as l
    [InlineData(" (1) ทำ [เควส]")]
    public void Finds_option_line_for_key_1(string line)
    {
        var hits = new[] { Hit("Rookie G"), Hit("ข้อความ"), Hit(line) };
        Assert.Equal(line, TextMatching.FindDialogOption("1", hits)?.Text);
    }

    [Fact]
    public void Does_not_treat_chat_lines_as_options()
    {
        var hits = new[] { Hit("ได้รับไอเทม :[[Holy] Code C] 1ชิ้น") };
        Assert.Null(TextMatching.FindDialogOption("1", hits));
    }

    [Fact]
    public void Short_end_text_matches_the_full_option_line()
    {
        Assert.True(TextMatching.SameLine("เข้าใจแล้ว", "[1] เข้าใจแล้วจะช่วยหามาให้"));
        Assert.False(TextMatching.SameLine("เข้าใจแล้ว", "[1] ทำ [เควส]"));
    }

    [Fact]
    public void Small_ocr_differences_still_count_as_the_same_line()
    {
        Assert.True(TextMatching.SameLine("[1] รับบัคโค้ด [Holy]", "[1] รับบัคโค๊ด [HoIy]"));
    }

    [Fact]
    public void Npc_label_wins_over_chat_and_dialog_title()
    {
        var screen = new DisplayText(new ScreenRect(0, 0, 1920, 1080), new[]
        {
            Hit("ได้รับไอเทมจาก Rookie G แล้ว", 10, 900),
            Hit("Rookie G", 900, 500),
            Hit("Part-Timer G", 300, 300),
        });
        var hit = TextMatching.FindNpc(new[] { "Rookie G", "Part-Timer G" }, new[] { screen }, prefer: null);
        Assert.Equal("Rookie G", hit?.Text);

        // Once the NPC position is known, a far match (the dialog title) is ignored.
        var far = TextMatching.FindNpc(new[] { "Part-Timer G" }, new[] { screen }, prefer: new ScreenPoint(950, 510));
        Assert.Null(far);
    }

    [Fact]
    public void Presets_saved_by_the_mac_app_load()
    {
        const string macJson = """
        {
          "steps": [
            { "id": "AA1A1237-F2EE-4C34-B287-FE42E3C828F1", "kind": "clickNPC", "name": "Quest G",
              "enabled": true, "key": "1", "text": "Rookie G, Part-Timer G", "timeoutMs": 3000,
              "delayAfterMs": 600, "npcOffsetY": -35 },
            { "id": "70667B7F-9EDD-48F6-86A6-6B79D88F2DEC", "kind": "dialogUntilEnd", "name": "กด 1",
              "enabled": true, "key": "1", "text": "", "timeoutMs": 3000, "delayAfterMs": 300,
              "endText": "เข้าใจแล้ว", "retryMs": 2500, "maxPresses": 15 },
            { "id": "43CDC512-6D92-4304-B2EC-6BEB998CD24C", "kind": "clickPoint", "name": "ปิด",
              "x": 950, "y": 529, "enabled": true, "key": "1", "text": "", "timeoutMs": 3000, "delayAfterMs": 100 }
          ],
          "loops": 10, "unlimited": true, "jitterMs": 150, "startDelayMs": 300, "stopIfMouseMoved": true
        }
        """;
        var cfg = JsonSerializer.Deserialize<QuestConfig>(macJson, QuestJson.Options)!;
        Assert.Equal(3, cfg.Steps.Count);
        Assert.Equal(StepKind.ClickNPC, cfg.Steps[0].Kind);
        Assert.Equal(StepKind.DialogUntilEnd, cfg.Steps[1].Kind);
        Assert.Equal("เข้าใจแล้ว", cfg.Steps[1].EndText);
        Assert.Equal(new ScreenPoint(950, 529), cfg.Steps[2].Point);

        // And round-trips with the same field names.
        string back = JsonSerializer.Serialize(cfg, QuestJson.Options);
        Assert.Contains("\"kind\": \"dialogUntilEnd\"", back);
        Assert.Contains("\"npcOffsetY\": -35", back);
    }

    [Theory]
    [InlineData("• [1] ทำ [เควส]")]
    [InlineData("'[1] ทำ [เควส]")]
    [InlineData("1] ทำ [เควส]")]
    [InlineData("[l) ทำ [เควส]")]
    public void Sloppy_windows_ocr_of_the_option_marker_still_counts(string line)
    {
        Assert.NotNull(TextMatching.FindDialogOption("1", new[] { new TextHit(line, new ScreenRect(0, 0, 100, 20)) }));
    }

    [Theory]
    [InlineData("Rookie G", true)]
    [InlineData("Rookie 6", true)]
    [InlineData("RookieG", true)]
    [InlineData("Rookle G", true)]
    [InlineData("Part-Tirner G", true)]
    [InlineData("Part Timer G", true)]
    [InlineData("Rookie A", false)]
    [InlineData("Rookie G ได้รับไอเทม Holy Code", false)]
    public void Npc_names_survive_windows_ocr_mistakes(string label, bool found)
    {
        var screens = new[] { new DisplayText(new ScreenRect(0, 0, 1920, 1080), new[] { new TextHit(label, new ScreenRect(900, 500, 80, 20)) }) };
        var hit = TextMatching.FindNpc(new[] { "Rookie G", "Part-Timer G" }, screens, null);
        Assert.Equal(found, hit is not null);
    }
}
