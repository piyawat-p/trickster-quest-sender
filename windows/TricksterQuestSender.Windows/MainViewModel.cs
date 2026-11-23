using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TricksterQuestSender.Core;

namespace QuestSenderWin;

/// <summary>State the window binds to. Tabs are presets; the active tab's config is what runs.</summary>
public sealed class MainViewModel : ObservableObject
{
    private Preset? _activeTab;
    private bool _isRunning;
    private string _status = "พร้อม — ยืนใกล้ NPC แล้วกด F8 หรือปุ่มเริ่ม";
    private int _completed;
    private string _ocrResult = "";
    private bool _alwaysOnTop = true;
    private string? _updateVersion;
    private bool _isRecording, _isAnalyzing, _autoRunAfterRecording = true;
    private int _recordedCount;

    public ObservableCollection<Preset> Tabs { get; } = new();

    public Preset? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab is not null)
            {
                _activeTab.IsActive = false;
                _activeTab.Config.Steps.CollectionChanged -= OnStepsChanged;
            }
            if (Set(ref _activeTab, value))
            {
                if (value is not null) value.IsActive = true;
                OnPropertyChanged(nameof(Config));
                RaiseModeChanged();
            }
            if (value is not null) value.Config.Steps.CollectionChanged += OnStepsChanged;
            else if (value is not null)
            {
                value.IsActive = true;
            }
        }
    }

    public QuestConfig? Config => ActiveTab?.Config;

    public bool IsRunning
    {
        get => _isRunning;
        set { if (Set(ref _isRunning, value)) RaiseModeChanged(); }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set { if (Set(ref _isRecording, value)) RaiseModeChanged(); }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set { if (Set(ref _isAnalyzing, value)) RaiseModeChanged(); }
    }

    public int RecordedCount { get => _recordedCount; set => Set(ref _recordedCount, value); }
    public bool AutoRunAfterRecording { get => _autoRunAfterRecording; set => Set(ref _autoRunAfterRecording, value); }

    /// <summary>Not running, recording or analysing: settings and tabs can be changed.</summary>
    public bool IsIdle => !IsRunning && !IsRecording && !IsAnalyzing;

    private bool HasSteps => Config is { Steps.Count: > 0 };

    /// <summary>Empty tab: offer "add step" / "record a round".</summary>
    public bool ShowEmptyChoices => !HasSteps && IsIdle;

    /// <summary>Empty tab while recording into it.</summary>
    public bool ShowRecordingHint => !HasSteps && (IsRecording || IsAnalyzing);

    /// <summary>Tab with steps: offer "start recording again" (replaces the steps).</summary>
    public bool ShowReRecord => HasSteps && !IsRecording && !IsAnalyzing;

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseModeChanged();

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ShowEmptyChoices));
        OnPropertyChanged(nameof(ShowRecordingHint));
        OnPropertyChanged(nameof(ShowReRecord));
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public int Completed { get => _completed; set => Set(ref _completed, value); }
    public string OcrResult { get => _ocrResult; set => Set(ref _ocrResult, value); }
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }

    public string? UpdateVersion
    {
        get => _updateVersion;
        set { if (Set(ref _updateVersion, value)) OnPropertyChanged(nameof(HasUpdate)); }
    }

    public bool HasUpdate => UpdateVersion is not null;

    public string OcrInfo { get; set; } = "";

    public AppState ToState() => new()
    {
        Tabs = Tabs.ToList(),
        ActiveTabId = ActiveTab?.Id,
        AlwaysOnTop = AlwaysOnTop,
    };
}
