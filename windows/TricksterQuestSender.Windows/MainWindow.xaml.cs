using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TricksterQuestSender.Core;

namespace QuestSenderWin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly WindowsInput _input = new();
    private readonly Updater _updater = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly Stack<(Preset Tab, int Index)> _closedTabs = new();
    private WindowsScreenReader? _screen;
    private HotkeyManager? _hotkeys;
    private int _escHotkey = -1;
    private CancellationTokenSource? _runCts;
    private string _lastSaved;
    private string _nameBeforeRename = "";
    private ActionRecorder? _recorder;
    private Preset? _recordingTab;
    private CancellationTokenSource? _autoStartCts;

    /// <summary>Version set by the release build from the git tag (v0.1.2 → 0.1.2).</summary>
    public static string AppVersion
    {
        get
        {
            var info = typeof(MainWindow).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            return (info ?? "dev").Split('+')[0];
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Title = $"Trickster Quest Sender v{AppVersion}";
        VersionText.Text = $"v{AppVersion}";

        var state = Storage.Load();
        foreach (var tab in state.Tabs) _vm.Tabs.Add(tab);
        _vm.AlwaysOnTop = state.AlwaysOnTop;
        _vm.ActiveTab = _vm.Tabs.FirstOrDefault(t => t.Id == state.ActiveTabId) ?? _vm.Tabs[0];
        _lastSaved = Storage.Serialize(_vm.ToState());

        try
        {
            _screen = new WindowsScreenReader();
            if (!WindowsScreenReader.ThaiAvailable)
                _vm.Status = $"OCR ยังอ่านภาษาไทยไม่ได้ (ใช้ {_screen.LanguageName}) — เพิ่มภาษาไทยใน Settings → Time & language → Language & region";
        }
        catch (Exception ex)
        {
            _vm.Status = ex.Message;
        }

        // Save at most once a second, only when something changed.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveIfChanged();
        _saveTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await CheckForUpdatesAsync();
        Closing += (_, _) =>
        {
            _runCts?.Cancel();
            _recorder?.Dispose();
            SaveIfChanged();
            _hotkeys?.Dispose();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Keep this window out of screenshots, so the keywords typed here are never "found" on screen.
        Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);

        _hotkeys = new HotkeyManager(this);
        if (_hotkeys.Register(Native.VK_F8, ToggleRun) < 0)
            _vm.Status = "ปุ่ม F8 ถูกโปรแกรมอื่นใช้อยู่ — ใช้ปุ่มเริ่มในหน้าต่างแทน";
    }

    // ------------------------------------------------------------------ run

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning) StopRun();
        else StartRun(fromHotkey: false);
    }

    private void ToggleRun()
    {
        if (_vm.IsRecording) StopRecording();
        else if (_vm.IsRunning) StopRun();
        else StartRun(fromHotkey: true);
    }

    private void StopRun() => _runCts?.Cancel();

    /// <summary>Esc: cancel recording, cancel a pending auto-start, or stop the run.</summary>
    private void OnEscape()
    {
        if (_vm.IsRecording) CancelRecording();
        else if (_autoStartCts is not null) CancelAutoStart();
        else StopRun();
    }

    private void RegisterEscape()
    {
        if (_escHotkey < 0) _escHotkey = _hotkeys?.Register(Native.VK_ESCAPE, OnEscape) ?? -1;
    }

    private void UnregisterEscape()
    {
        _hotkeys?.Unregister(_escHotkey);
        _escHotkey = -1;
    }

    private async void StartRun(bool fromHotkey)
    {
        if (!_vm.IsIdle || _vm.Config is null) return;
        if (_screen is null)
        {
            _vm.Status = "อ่านหน้าจอไม่ได้ (OCR ไม่พร้อม)";
            return;
        }

        var config = _vm.Config.Copy(); // the run works on a snapshot
        var steps = config.Steps.Where(s => s.Enabled).ToList();
        if (steps.Count == 0) { _vm.Status = "ยังไม่มีขั้นตอนที่เปิดใช้"; return; }
        if (!fromHotkey && steps.Any(s => s.Kind == StepKind.ClickStartMouse))
        {
            _vm.Status = "ชี้เมาส์ที่ NPC แล้วกด F8 เพื่อเริ่ม";
            return;
        }
        if (steps.FirstOrDefault(s => s.Kind == StepKind.ClickPoint && s.Point is null) is { } noPoint)
        {
            _vm.Status = $"ยังไม่ได้จับตำแหน่ง: {noPoint.Name}";
            return;
        }
        if (steps.FirstOrDefault(s => s.Kind is StepKind.ClickNPC or StepKind.WaitText or StepKind.ClickText
                                      && string.IsNullOrWhiteSpace(s.Text)) is { } noText)
        {
            _vm.Status = $"ยังไม่ได้ใส่ข้อความ: {noText.Name}";
            return;
        }

        var startMouse = _input.CursorPosition;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _vm.IsRunning = true;
        _vm.Completed = 0;
        StartButton.Content = "หยุด (Esc / F8)";
        RegisterEscape();

        var engine = new QuestEngine(_input, _screen);
        engine.StatusChanged += s => Dispatcher.InvokeAsync(() => _vm.Status = s);
        engine.CompletedChanged += n => Dispatcher.InvokeAsync(() => _vm.Completed = n);

        RunResult result;
        try
        {
            result = await Task.Run(() => engine.RunAsync(config, startMouse, cts.Token));
        }
        catch (Exception ex)
        {
            result = new RunResult("ผิดพลาด: " + ex.Message, engine.CompletedCount);
        }
        finally
        {
            UnregisterEscape();
            _runCts = null;
            cts.Dispose();
            _vm.IsRunning = false;
            StartButton.Content = "เริ่ม (F8)";
        }

        // Let queued status updates land first, then show the final message.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        _vm.Status = result.Message;
        _vm.Completed = result.Completed;
        SystemSounds.Beep.Play();
    }

    // ------------------------------------------------------------------ tabs

    private static Preset? TabOf(object sender) => (sender as FrameworkElement)?.Tag as Preset;

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is not { } tab || tab.IsRenaming) return;
        if (e.ClickCount >= 2) BeginRename(tab); // first click of the double-click already selected it
        else SelectTab(tab);
    }

    private void SelectTab(Preset tab)
    {
        if (!_vm.IsIdle || ReferenceEquals(tab, _vm.ActiveTab)) return;
        _vm.ActiveTab = tab;
        _vm.Status = $"ใช้แท็บ “{tab.Name}”";
    }

    private void AddTab_Click(object sender, RoutedEventArgs e) => AddTab();

    private void AddTab()
    {
        if (!_vm.IsIdle) return;
        var empty = QuestConfig.CreateDefault();
        empty.Steps.Clear();
        var tab = new Preset { Name = NextUntitledName(), Config = empty };
        _vm.Tabs.Add(tab);
        SelectTab(tab);
    }

    /// <summary>untitled-1, untitled-2, … (one more than the highest number in use).</summary>
    private string NextUntitledName()
    {
        int max = _vm.Tabs
            .Select(t => t.Name.StartsWith("untitled-") && int.TryParse(t.Name["untitled-".Length..], out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"untitled-{max + 1}";
    }

    private void DuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsIdle || TabOf(sender) is not { } tab) return;
        var copy = new Preset { Name = tab.Name + " (สำเนา)", Config = tab.Config.Copy(), IsRecorded = tab.IsRecorded };
        _vm.Tabs.Insert(_vm.Tabs.IndexOf(tab) + 1, copy);
        SelectTab(copy);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) CloseTab(tab);
        e.Handled = true; // don't also select the tab being closed
    }

    private void CloseTabMenu_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) CloseTab(tab);
    }

    private void CloseTab(Preset tab)
    {
        if (!_vm.IsIdle || _vm.Tabs.Count <= 1) return;
        int index = _vm.Tabs.IndexOf(tab);
        if (index < 0) return;
        _vm.Tabs.RemoveAt(index);
        _closedTabs.Push((tab, index));
        if (ReferenceEquals(tab, _vm.ActiveTab))
            _vm.ActiveTab = _vm.Tabs[Math.Min(index, _vm.Tabs.Count - 1)];
        _vm.Status = $"ปิดแท็บ “{tab.Name}” แล้ว (Ctrl+Shift+T เพื่อเปิดคืน)";
    }

    private void ReopenClosedTab()
    {
        if (!_vm.IsIdle || _closedTabs.Count == 0) return;
        var (tab, index) = _closedTabs.Pop();
        _vm.Tabs.Insert(Math.Min(index, _vm.Tabs.Count), tab);
        _vm.ActiveTab = tab;
        _vm.Status = $"เปิดแท็บ “{tab.Name}” คืนแล้ว";
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) BeginRename(tab);
    }

    private void BeginRename(Preset tab)
    {
        if (!_vm.IsIdle) return;
        foreach (var t in _vm.Tabs) t.IsRenaming = false;
        _nameBeforeRename = tab.Name;
        tab.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            Dispatcher.InvokeAsync(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        if (e.Key == Key.Enter)
        {
            EndRename(tab, keep: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(tab, keep: false);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TabOf(sender) is { IsRenaming: true } tab) EndRename(tab, keep: true);
    }

    private void EndRename(Preset tab, bool keep)
    {
        if (!keep || string.IsNullOrWhiteSpace(tab.Name)) tab.Name = _nameBeforeRename;
        else tab.Name = tab.Name.Trim();
        tab.IsRenaming = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl) return;

        if (e.Key == Key.R && shift) { StartRecording(); e.Handled = true; }
        else if (e.Key == Key.T && shift) { ReopenClosedTab(); e.Handled = true; }
        else if (e.Key == Key.T) { AddTab(); e.Handled = true; }
        else if (e.Key == Key.W && _vm.ActiveTab is { } active) { CloseTab(active); e.Handled = true; }
        else if (e.Key == Key.Tab && _vm.ActiveTab is { } current && _vm.Tabs.Count > 1)
        {
            int i = _vm.Tabs.IndexOf(current);
            int next = (i + (shift ? -1 : 1) + _vm.Tabs.Count) % _vm.Tabs.Count;
            SelectTab(_vm.Tabs[next]);
            e.Handled = true;
        }
        else if (e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (index < _vm.Tabs.Count) SelectTab(_vm.Tabs[index]);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ steps

    private static QuestStep? StepOf(object sender) => (sender as FrameworkElement)?.Tag as QuestStep;

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Config is not { } config) return;
        // A round starts at the NPC, so an empty tab's first step is "find NPC by name".
        config.Steps.Add(config.Steps.Count == 0
            ? QuestStep.NpcStep()
            : new QuestStep { Kind = StepKind.Key, Name = $"ขั้นตอน {config.Steps.Count + 1}" });
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveStep(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveStep(sender, 1);

    private void MoveStep(object sender, int offset)
    {
        if (_vm.Config is not { } config || StepOf(sender) is not { } step) return;
        int i = config.Steps.IndexOf(step), j = i + offset;
        if (i >= 0 && j >= 0 && j < config.Steps.Count) config.Steps.Move(i, j);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Config is { } config && StepOf(sender) is { } step) config.Steps.Remove(step);
    }

    /// <summary>3 seconds to put the mouse on the spot, then remember it.</summary>
    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is not { } step) return;
        for (int i = 3; i >= 1; i--)
        {
            _vm.Status = $"วางเมาส์ไว้บนจุดที่จะคลิก... {i}";
            await Task.Delay(1000);
        }
        var p = _input.CursorPosition;
        step.X = p.X;
        step.Y = p.Y;
        _vm.Status = $"บันทึก “{step.Name}” ที่ ({p.X:0}, {p.Y:0})";
    }

    // ------------------------------------------------------------------ recording (belongs to the active tab)

    private void Record_Click(object sender, RoutedEventArgs e) => StartRecording();
    private void StopRecording_Click(object sender, RoutedEventArgs e) => StopRecording();
    private void CancelRecording_Click(object sender, RoutedEventArgs e) => CancelRecording();

    private void StartRecording()
    {
        if (!_vm.IsIdle || _vm.ActiveTab is not { } tab) return;
        if (_screen is null)
        {
            _vm.Status = "อ่านหน้าจอไม่ได้ (OCR ไม่พร้อม)";
            return;
        }
        CancelAutoStart();
        var hwnd = new WindowInteropHelper(this).Handle;
        var recorder = new ActionRecorder(_screen, hwnd, _screen.PointScale);
        recorder.Changed += n => _vm.RecordedCount = n;
        _recorder = recorder;
        _recordingTab = tab;
        _vm.RecordedCount = 0;
        _vm.IsRecording = true;
        RegisterEscape();
        recorder.Start(_input.CursorPosition);
        _vm.Status = "กำลังบันทึก — ในเกม: คลิก NPC → กด 1 จนจบ → คลิกพื้นที่ว่างปิดเควส แล้วกด F8";
    }

    private void CancelRecording()
    {
        _recorder?.Dispose();
        _recorder = null;
        _recordingTab = null;
        _vm.IsRecording = false;
        UnregisterEscape();
        _vm.Status = "ยกเลิกการบันทึกแล้ว";
    }

    /// <summary>Stops recording, analyses the round, and replaces the tab's steps (only if that worked).</summary>
    private async void StopRecording()
    {
        if (_recorder is not { } recorder || _recordingTab is not { } tab || _screen is null) return;
        _recorder = null;
        _recordingTab = null;
        _vm.IsRecording = false;
        UnregisterEscape();

        if (recorder.Count == 0)
        {
            recorder.Dispose();
            _vm.Status = "ไม่ได้บันทึกอะไรเลย (คลิก/กดในหน้าต่างเกมระหว่างบันทึก)";
            return;
        }

        _vm.IsAnalyzing = true;
        _vm.Status = $"กำลังวิเคราะห์ {recorder.Count} การกระทำ...";
        List<QuestStep> steps;
        try
        {
            var actions = await recorder.FinishAsync();
            steps = RecordingAnalyzer.MakeSteps(actions, _screen.PointScale);
        }
        catch (Exception ex)
        {
            steps = new List<QuestStep>();
            _vm.Status = "วิเคราะห์ไม่สำเร็จ: " + ex.Message;
        }
        finally
        {
            recorder.Dispose();
            _vm.IsAnalyzing = false;
        }

        if (steps.Count == 0)
        {
            if (!_vm.Status.StartsWith("วิเคราะห์ไม่สำเร็จ"))
                _vm.Status = "วิเคราะห์ไม่ได้ขั้นตอน — กด “บันทึกการเล่น” แล้วลองอีกครั้ง (ขั้นตอนเดิมยังอยู่)";
            return;
        }

        // Replace this tab's steps with the new recording (its other settings stay).
        tab.Config.Steps.Clear();
        foreach (var step in steps) tab.Config.Steps.Add(step);
        tab.IsRecorded = true;
        _vm.ActiveTab = tab;
        SaveIfChanged();

        if (!_vm.AutoRunAfterRecording)
        {
            _vm.Status = $"บันทึกลงแท็บ “{tab.Name}” แล้ว ({steps.Count} ขั้นตอน) — ตรวจดูแล้วกด F8 เพื่อเริ่ม";
            return;
        }

        _vm.Status = $"บันทึกลงแท็บ “{tab.Name}” แล้ว — เริ่มรันใน 2 วินาที (Esc เพื่อยกเลิก)";
        var cts = new CancellationTokenSource();
        _autoStartCts = cts;
        RegisterEscape();
        try
        {
            await Task.Delay(2000, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_autoStartCts, cts)) _autoStartCts = null;
            UnregisterEscape();
        }
        StartRun(fromHotkey: true);
    }

    private void CancelAutoStart()
    {
        if (_autoStartCts is null) return;
        _autoStartCts.Cancel();
        _autoStartCts = null;
        _vm.Status = "ยกเลิกการเริ่มอัตโนมัติ";
    }

    // ------------------------------------------------------------------ tests

    private async void TestNpc_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        var step = _vm.Config?.Steps.FirstOrDefault(s => s.Kind == StepKind.ClickNPC) ?? QuestStep.NpcStep();
        _vm.Status = $"กำลังหา {string.Join(" / ", step.NpcNames)}...";
        try
        {
            var screens = await _screen.ReadAllDisplaysAsync(CancellationToken.None);
            var hit = TextMatching.FindNpc(step.NpcNames, screens, prefer: null);
            if (hit is null)
            {
                _vm.OcrResult = string.Join(Environment.NewLine, screens.SelectMany(s => s.Hits).Select(h => h.Text));
                _vm.Status = "ไม่เจอ NPC บนจอ (ดูข้อความที่อ่านได้ด้านล่าง)";
                return;
            }
            var p = new ScreenPoint(hit.Center.X, hit.Center.Y + step.NpcOffsetY * _screen.PointScale);
            Native.SetCursorPos((int)p.X, (int)p.Y);
            _vm.Status = $"เจอ “{hit.Text}” — เลื่อนเมาส์ไปจุดที่จะคลิกแล้ว (ยังไม่คลิก)";
        }
        catch (Exception ex)
        {
            _vm.Status = "อ่านจอไม่สำเร็จ: " + ex.Message;
        }
    }

    private async void TestOcr_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        _vm.Status = "กำลังอ่านข้อความบนจอ...";
        try
        {
            var hits = await _screen.ReadScreenAsync(_input.CursorPosition, CancellationToken.None);
            _vm.OcrResult = string.Join(Environment.NewLine, hits.Select(h => h.Text));
            _vm.Status = $"อ่านได้ {hits.Count} บรรทัด (OCR: {_screen.LanguageName})";
        }
        catch (Exception ex)
        {
            _vm.Status = "อ่านจอไม่สำเร็จ: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ saving & updates

    private void SaveIfChanged()
    {
        try
        {
            string json = Storage.Serialize(_vm.ToState());
            if (json == _lastSaved) return;
            Storage.Save(json);
            _lastSaved = json;
        }
        catch (Exception ex)
        {
            _vm.Status = "บันทึกการตั้งค่าไม่สำเร็จ: " + ex.Message;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _vm.UpdateVersion = await _updater.CheckAndDownloadAsync();
        }
        catch
        {
            // Offline or no releases yet: ignore.
        }
    }

    private void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning) return;
        SaveIfChanged();
        _updater.ApplyAndRestart();
    }
}
