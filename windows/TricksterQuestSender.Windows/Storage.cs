using System.IO;
using System.Text.Json;
using TricksterQuestSender.Core;

namespace QuestSenderWin;

public sealed class AppState
{
    public List<Preset> Tabs { get; set; } = new();
    public Guid? ActiveTabId { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
}

/// <summary>Saves tabs to %APPDATA%\TricksterQuestSender\tabs.json (same JSON shape as the Mac app's presets).</summary>
internal static class Storage
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TricksterQuestSender");
    private static readonly string FilePath = Path.Combine(Folder, "tabs.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), QuestJson.Options);
                if (state is { Tabs.Count: > 0 }) return state;
            }
        }
        catch
        {
            // Broken file: start fresh rather than crash. (The old file is kept as tabs.json.bak below.)
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
        }
        return new AppState
        {
            Tabs = { new Preset { Name = "กดจนจบบทสนทนา", Config = QuestConfig.CreateDefault() } },
        };
    }

    public static string Serialize(AppState state) => JsonSerializer.Serialize(state, QuestJson.Options);

    public static void Save(string json)
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
