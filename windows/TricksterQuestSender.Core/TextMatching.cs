using System.Text;
using System.Text.RegularExpressions;

namespace TricksterQuestSender.Core;

/// <summary>Matching of OCR text. OCR output varies a little between screenshots, so most checks are fuzzy.</summary>
public static class TextMatching
{
    /// <summary>Lower-case without whitespace (OCR adds/drops spaces in Thai).</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Letters and digits only: "Part-Timer G" → "parttimerg".</summary>
    public static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>1 − (edit distance / longer length).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Same dialog line, allowing for OCR noise and short keywords ("เข้าใจแล้ว").</summary>
    public static bool SameLine(string a, string b)
    {
        string x = Normalize(a), y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x.Contains(y) || y.Contains(x) || Similarity(x, y) >= 0.75;
    }

    /// <summary>"a | b | c" → ["a", "b", "c"]: a field with several alternatives (any one may match).</summary>
    public static IReadOnlyList<string> Alternatives(string field) =>
        field.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>The quest item of a tower floor: "Chaos Integer.2Byte" for floor 2. Tolerates OCR
    /// spacing and I/l/1 mix-ups; "62Byte" does not count as floor 2.</summary>
    private static string FloorItemCore(int floor) =>
        @"chaos\s*[il1|]\s*n\s*t\s*e\s*g\s*e\s*r\s*[.,·:]?\s*(?<![0-9])" + floor + @"\s*b\s*y\s*t\s*e";

    /// <param name="bracketed">Require "[" before or "]" after it (the form shown on the last page).</param>
    public static bool HasFloorItem(IEnumerable<TextHit> hits, int floor, bool bracketed)
    {
        if (floor <= 0) return false;
        string core = FloorItemCore(floor);
        string pattern = bracketed ? @"[\[\(\{]\s*" + core + "|" + core + @"\s*[\]\)\}]" : core;
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        return hits.Any(h => regex.IsMatch(h.Text));
    }

    /// <summary>Floor number in "Chaos Integer.{n}Byte" if any line shows one.</summary>
    public static int? FindFloor(IEnumerable<TextHit> hits)
    {
        var regex = new Regex(@"chaos\s*[il1|]\s*n\s*t\s*e\s*g\s*e\s*r\s*[.,·:]?\s*([0-9]{1,3})\s*b\s*y\s*t\s*e",
                              RegexOptions.IgnoreCase);
        foreach (var h in hits)
        {
            var m = regex.Match(h.Text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
        }
        return null;
    }

    /// <summary>Exact "contains" first, else the most similar line if similar enough.</summary>
    public static TextHit? FindFuzzy(string keyword, IEnumerable<TextHit> hits, double threshold = 0.75)
    {
        string needle = Normalize(keyword);
        if (needle.Length == 0) return null;
        var list = hits as IList<TextHit> ?? hits.ToList();
        var exact = list.FirstOrDefault(h => Normalize(h.Text).Contains(needle));
        if (exact is not null) return exact;
        TextHit? best = null;
        double bestScore = 0;
        foreach (var h in list)
        {
            double score = Similarity(needle, Normalize(h.Text));
            if (score >= threshold && score > bestScore) { best = h; bestScore = score; }
        }
        return best;
    }

    /// <summary>The dialog option line for a key, e.g. "[1] ทำ [เควส]". OCR sometimes reads 1 as l or I.</summary>
    public static TextHit? FindDialogOption(string key, IEnumerable<TextHit> hits)
    {
        string digit = key switch
        {
            "1" => "[1lI|]",
            "Enter" or "Space" => "[0-9lI|]",
            _ => Regex.Escape(key),
        };
        var pattern = new Regex(@"^\s*[\[\(\{|]\s*" + digit + @"\s*[\]\)\}|]");
        return hits.FirstOrDefault(h => pattern.IsMatch(h.Text));
    }

    /// <summary>
    /// Finds an NPC name label: only short lines that are (almost) exactly the name, so chat lines
    /// mentioning it don't count. Picks the match closest to <paramref name="prefer"/> (last NPC
    /// position; farther than <paramref name="maxDistance"/> is ignored) or else to the screen centre.
    /// </summary>
    public static TextHit? FindNpc(IReadOnlyList<string> names, IEnumerable<DisplayText> screens,
                                   ScreenPoint? prefer, double maxDistance = 150)
    {
        var targets = names.Select(Compact).Where(t => t.Length > 0).ToList();
        if (targets.Count == 0) return null;
        TextHit? best = null;
        double bestDistance = double.MaxValue;
        foreach (var screen in screens)
        {
            var centre = screen.Bounds.Center;
            foreach (var hit in screen.Hits)
            {
                string t = Compact(hit.Text);
                bool matches = targets.Any(n => t == n || (t.Contains(n) && t.Length <= n.Length + 3));
                if (!matches) continue;
                var reference = prefer ?? centre;
                double d = hit.Center.DistanceTo(reference);
                if (prefer is not null && d > maxDistance) continue;
                if (d < bestDistance) { best = hit; bestDistance = d; }
            }
        }
        return best;
    }
}
