namespace TricksterQuestSender.Core;

public enum RecordedActionKind { Click, Key }

/// <summary>One thing the user did while recording, with the text (and page image) on screen just before it.</summary>
public sealed record RecordedAction(
    RecordedActionKind Kind,
    ScreenPoint Point,
    string Key,
    IReadOnlyList<TextHit> Hits,
    byte[]? PageHash);

/// <summary>
/// Turns one recorded round into: find NPC by name → press the option key until the last page →
/// click empty space to close the quest window. Same result as the macOS app.
/// </summary>
public static class RecordingAnalyzer
{
    /// <param name="scale">Screen units per point (see IScreenReader.PointScale).</param>
    public static List<QuestStep> MakeSteps(IReadOnlyList<RecordedAction> actions, double scale = 1)
    {
        var steps = new List<QuestStep>();
        var list = actions.ToList();
        if (list.Count == 0) return steps;
        int firstKey = list.FindIndex(a => a.Kind == RecordedActionKind.Key);
        int lastKey = list.FindLastIndex(a => a.Kind == RecordedActionKind.Key);

        // 1) Start: the last click before the dialog = clicking the NPC.
        int npcIndex = list.FindLastIndex(Math.Max(0, (firstKey >= 0 ? firstKey : list.Count) - 1),
                                          firstKey >= 0 ? firstKey : list.Count,
                                          a => a.Kind == RecordedActionKind.Click);
        if (npcIndex >= 0)
        {
            var click = list[npcIndex];
            var label = NpcLabel(click.Point, click.Hits, scale);
            if (label is not null)
            {
                // Search by name: the usual NPC names plus the label read here (if different).
                var names = QuestStep.DefaultNpcNames.Split(',').Select(n => n.Trim()).ToList();
                if (!names.Any(n => TextMatching.Compact(n) == TextMatching.Compact(label.Text)))
                    names.Add(label.Text.Trim());
                steps.Add(new QuestStep
                {
                    Kind = StepKind.ClickNPC,
                    Name = "หา NPC ด้วยชื่อแล้วคลิก",
                    Text = string.Join(", ", names),
                    TimeoutMs = 3000,
                    DelayAfterMs = 300,
                    NpcOffsetY = Math.Round((click.Point.Y - label.Center.Y) / scale),
                });
            }
            else
            {
                steps.Add(QuestStep.NpcStep());
            }
        }

        // 2) Middle: one "press until the end" step that knows every page (text + image) and the last one.
        if (firstKey >= 0)
        {
            var pages = new List<string>();
            var hashes = new List<string>();
            string key = "1";
            for (int i = firstKey; i <= lastKey; i++)
            {
                var a = list[i];
                if (a.Kind != RecordedActionKind.Key) continue;
                key = a.Key;
                var option = TextMatching.FindDialogOption(a.Key, a.Hits);
                if (option is null) continue;
                pages.Add(option.Text);
                hashes.Add(a.PageHash is { } h ? Convert.ToBase64String(h) : "");
            }
            steps.Add(new QuestStep
            {
                Kind = StepKind.DialogUntilEnd,
                Name = $"กด {key} จนจบบทสนทนา",
                Key = key,
                TimeoutMs = 3000,
                DelayAfterMs = 300,
                Pages = string.Join("\n", pages),
                PageHashes = string.Join("\n", hashes),
                EndText = pages.LastOrDefault() ?? "",
                MaxPresses = Math.Max(15, pages.Count * 2),
            });
        }

        // 3) End: the click after the last option = click empty space to close the quest window.
        if (lastKey >= 0)
        {
            int close = list.FindIndex(lastKey + 1, a => a.Kind == RecordedActionKind.Click);
            if (close >= 0)
            {
                var p = list[close].Point;
                steps.Add(new QuestStep
                {
                    Kind = StepKind.ClickPoint,
                    Name = "คลิกพื้นที่ว่างเพื่อปิดเควส",
                    X = Math.Round(p.X),
                    Y = Math.Round(p.Y),
                    DelayAfterMs = 500,
                });
            }
        }

        return steps;
    }

    /// <summary>NPC name labels sit just below the sprite: a short Latin-letter line under the click.</summary>
    private static TextHit? NpcLabel(ScreenPoint p, IReadOnlyList<TextHit> hits, double s) =>
        hits.Where(h =>
            {
                double dy = h.Center.Y - p.Y;
                int letters = TextMatching.Compact(h.Text).Length;
                return dy > 5 * s && dy < 130 * s
                    && Math.Abs(h.Center.X - p.X) < 90 * s
                    && letters is >= 3 and <= 24
                    && h.Text.Any(c => c < 128 && char.IsLetter(c));
            })
            .OrderBy(h => h.Center.DistanceTo(p))
            .FirstOrDefault();
}
