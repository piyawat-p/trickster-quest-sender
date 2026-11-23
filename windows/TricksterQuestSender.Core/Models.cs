using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TricksterQuestSender.Core;

// JSON field names and enum values match the macOS app, so presets can be shared between them.

public enum StepKind
{
    /// <summary>Find the NPC by the name label under it and click the NPC.</summary>
    ClickNPC,
    /// <summary>Press the option key on every new dialog page until the last page.</summary>
    DialogUntilEnd,
    /// <summary>Click where the mouse was when the run started.</summary>
    ClickStartMouse,
    /// <summary>Click a captured screen position (e.g. empty space to close the quest window).</summary>
    ClickPoint,
    /// <summary>Press a key; optionally wait for a dialog line first.</summary>
    Key,
    /// <summary>Wait until text appears; stop the run if it never does.</summary>
    WaitText,
    /// <summary>Find text on screen and click it.</summary>
    ClickText,
}

public static class StepKinds
{
    public static string Label(StepKind kind) => kind switch
    {
        StepKind.ClickNPC => "หา NPC แล้วคลิก",
        StepKind.DialogUntilEnd => "กดตัวเลือกจนบทสนทนาจบ",
        StepKind.ClickStartMouse => "คลิกตรงเมาส์ตอนเริ่ม",
        StepKind.ClickPoint => "คลิกตำแหน่งที่จับไว้",
        StepKind.Key => "กดปุ่มคีย์บอร์ด",
        StepKind.WaitText => "เช็กข้อความบนจอ",
        StepKind.ClickText => "คลิกที่ข้อความ",
        _ => kind.ToString(),
    };

    public static bool UsesScreenReading(QuestStep s) =>
        s.Kind is StepKind.ClickNPC or StepKind.DialogUntilEnd or StepKind.WaitText or StepKind.ClickText
        || (s.Kind == StepKind.Key && !string.IsNullOrWhiteSpace(s.Text));
}

public static class KeyNames
{
    public static readonly IReadOnlyList<string> All =
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "Enter", "Space" };
}

public sealed class QuestStep : ObservableObject
{
    public const string DefaultNpcNames = "Rookie G, Part-Timer G";

    private Guid _id = Guid.NewGuid();
    private StepKind _kind;
    private string _name = "";
    private bool _enabled = true;
    private double? _x, _y;
    private string _key = "1";
    private string _text = "";
    private int _timeoutMs = 3000;
    private int _delayAfterMs = 600;
    private double _npcOffsetY = -35;
    private string _stopText = "";
    private int _retryMs = 2500;
    private int _maxPresses = 15;
    private string _endText = "";
    private string _pages = "";
    private string _pageHashes = "";

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public StepKind Kind { get => _kind; set => Set(ref _kind, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double? X { get => _x; set { if (Set(ref _x, value)) OnPropertyChanged(nameof(PointLabel)); } }
    public double? Y { get => _y; set { if (Set(ref _y, value)) OnPropertyChanged(nameof(PointLabel)); } }
    public string Key { get => _key; set => Set(ref _key, value); }
    /// <summary>
    /// WaitText/ClickText: keyword. ClickNPC: comma-separated names. DialogUntilEnd: optional text
    /// that must appear during the dialog. Key: optional dialog line to wait for before pressing.
    /// </summary>
    public string Text { get => _text; set => Set(ref _text, value); }
    public int TimeoutMs { get => _timeoutMs; set => Set(ref _timeoutMs, value); }
    public int DelayAfterMs { get => _delayAfterMs; set => Set(ref _delayAfterMs, value); }
    /// <summary>ClickNPC: distance from the name label to the click (negative = above). In points.</summary>
    public double NpcOffsetY { get => _npcOffsetY; set => Set(ref _npcOffsetY, value); }
    public string StopText { get => _stopText; set => Set(ref _stopText, value); }
    public int RetryMs { get => _retryMs; set => Set(ref _retryMs, value); }
    public int MaxPresses { get => _maxPresses; set => Set(ref _maxPresses, value); }
    /// <summary>DialogUntilEnd: option line of the last page; pressing it ends the round.</summary>
    public string EndText { get => _endText; set => Set(ref _endText, value); }
    /// <summary>DialogUntilEnd: known option lines (one per line). Unknown page → stop.</summary>
    public string Pages { get => _pages; set => Set(ref _pages, value); }
    /// <summary>DialogUntilEnd: base64 fingerprint of each recorded page, one per line, same order as Pages.</summary>
    public string PageHashes { get => _pageHashes; set => Set(ref _pageHashes, value); }

    [JsonIgnore] public IReadOnlyList<string> NpcNames => SplitList(Text, ',');
    [JsonIgnore] public IReadOnlyList<string> KnownPages => SplitList(Pages, '\n');

    /// <summary>Recorded page images aligned with KnownPages (null where a page has none).</summary>
    [JsonIgnore]
    public IReadOnlyList<byte[]?> KnownPageHashes
    {
        get
        {
            int count = KnownPages.Count;
            var lines = PageHashes.Split('\n');
            var result = new List<byte[]?>(count);
            for (int i = 0; i < count; i++)
            {
                byte[]? hash = null;
                if (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    try { hash = Convert.FromBase64String(lines[i].Trim()); } catch (FormatException) { }
                }
                result.Add(hash);
            }
            return result;
        }
    }
    [JsonIgnore] public ScreenPoint? Point => X is double x && Y is double y ? new ScreenPoint(x, y) : null;
    [JsonIgnore] public string PointLabel => Point is { } p ? $"({p.X:0}, {p.Y:0})" : "ยังไม่ตั้ง";

    public static QuestStep NpcStep(string name = "หา NPC ด้วยชื่อแล้วคลิก", int delayAfterMs = 300) => new()
    {
        Kind = StepKind.ClickNPC, Name = name, Text = DefaultNpcNames, TimeoutMs = 3000, DelayAfterMs = delayAfterMs,
    };

    public QuestStep Copy()
    {
        var copy = (QuestStep)MemberwiseClone();
        copy.ClearSubscribers();
        copy.Id = Guid.NewGuid();
        return copy;
    }

    private static IReadOnlyList<string> SplitList(string s, char sep) =>
        s.Split(sep).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

public sealed class QuestConfig : ObservableObject
{
    private int _loops = 10;
    private bool _unlimited = true;
    private int _jitterMs = 150;
    private int _startDelayMs = 300;
    private bool _stopIfMouseMoved = true;

    public ObservableCollection<QuestStep> Steps { get; set; } = new();
    public int Loops { get => _loops; set => Set(ref _loops, value); }
    public bool Unlimited { get => _unlimited; set => Set(ref _unlimited, value); }
    public int JitterMs { get => _jitterMs; set => Set(ref _jitterMs, value); }
    public int StartDelayMs { get => _startDelayMs; set => Set(ref _startDelayMs, value); }
    public bool StopIfMouseMoved { get => _stopIfMouseMoved; set => Set(ref _stopIfMouseMoved, value); }

    /// <summary>Main flow: find NPC by name → press 1 until the last page → click empty space → repeat.</summary>
    public static QuestConfig CreateDefault() => new()
    {
        Steps = new ObservableCollection<QuestStep>
        {
            QuestStep.NpcStep(),
            new()
            {
                Kind = StepKind.DialogUntilEnd, Name = "กด 1 จนจบบทสนทนา", Key = "1",
                TimeoutMs = 3000, DelayAfterMs = 300,
                // "[1] เข้าใจแล้วจะช่วยหามาให้" = accept the quest again = last page of the round.
                EndText = "เข้าใจแล้ว",
            },
            new()
            {
                Kind = StepKind.ClickPoint, Name = "คลิกพื้นที่ว่างเพื่อปิดเควส (กด “จับตำแหน่ง” ก่อนใช้ครั้งแรก)",
                DelayAfterMs = 500,
            },
        },
    };

    public QuestConfig Copy()
    {
        var copy = (QuestConfig)MemberwiseClone();
        copy.ClearSubscribers();
        copy.Steps = new ObservableCollection<QuestStep>(Steps.Select(s => s.Copy()));
        return copy;
    }
}

public sealed class Preset : ObservableObject
{
    private string _name = "";
    private bool _isActive, _isRenaming, _isRecorded;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public QuestConfig Config { get; set; } = QuestConfig.CreateDefault();
    public bool IsBuiltIn { get; set; }
    /// <summary>Made by recording a round; "start recording again" replaces its steps.</summary>
    public bool IsRecorded { get => _isRecorded; set => Set(ref _isRecorded, value); }

    // UI state (not saved).
    [JsonIgnore] public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    [JsonIgnore] public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
}

public static class QuestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
