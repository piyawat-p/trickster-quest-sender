using System.Drawing;
using System.Runtime.InteropServices;
using TricksterQuestSender.Core;

namespace QuestSenderWin;

/// <summary>
/// Watches the user play one round: left clicks and dialog keys anywhere except this app's window.
/// Keeps the last few screenshots, so each action is matched with what the screen showed just before it.
/// Uses low-level hooks, which must be installed from the UI thread (it pumps the messages they need).
/// </summary>
internal sealed class ActionRecorder : IDisposable
{
    private readonly WindowsScreenReader _screen;
    private readonly IntPtr _ownWindow;
    private readonly double _scale;
    private readonly object _framesLock = new();
    private readonly List<(Bitmap Image, ScreenRect Area)> _frames = new();
    private readonly List<PendingAction> _actions = new();
    private readonly HashSet<uint> _keysDown = new();
    private Native.LowLevelProc? _mouseProc, _keyProc; // fields keep the delegates alive while hooked
    private IntPtr _mouseHook, _keyHook;
    private CancellationTokenSource? _captureCts;

    private sealed record PendingAction(RecordedActionKind Kind, ScreenPoint Point, string Key,
                                        Task<(IReadOnlyList<TextHit> Hits, byte[]? PageHash)> Analysis);

    public event Action<int>? Changed;

    public int Count => _actions.Count;

    public ActionRecorder(WindowsScreenReader screen, IntPtr ownWindow, double scale)
    {
        _screen = screen;
        _ownWindow = ownWindow;
        _scale = scale;
    }

    public void Start(ScreenPoint near)
    {
        var area = WindowsScreenReader.MonitorAt(near);
        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var shot = WindowsScreenReader.Capture(area);
                    lock (_framesLock)
                    {
                        _frames.Add((shot, area));
                        if (_frames.Count > 3)
                        {
                            _frames[0].Image.Dispose();
                            _frames.RemoveAt(0);
                        }
                    }
                }
                catch
                {
                    // A failed screenshot just means one frame fewer.
                }
                try { await Task.Delay(150, ct); } catch (OperationCanceledException) { }
            }
        });

        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        IntPtr module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
    }

    /// <summary>Stops watching and returns the analysed actions (waits for their OCR).</summary>
    public async Task<List<RecordedAction>> FinishAsync()
    {
        StopWatching();
        var result = new List<RecordedAction>();
        foreach (var a in _actions)
        {
            var (hits, hash) = await a.Analysis;
            result.Add(new RecordedAction(a.Kind, a.Point, a.Key, hits, hash));
        }
        return result;
    }

    public void Dispose() => StopWatching();

    private void StopWatching()
    {
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        _captureCts?.Cancel();
        lock (_framesLock)
        {
            foreach (var f in _frames) f.Image.Dispose();
            _frames.Clear();
        }
    }

    // ------------------------------------------------------------------ hooks (keep them fast)

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == Native.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & Native.LLMHF_INJECTED) != 0;
            if (!injected && !IsOwnWindow(Native.WindowFromPoint(info.pt)))
            {
                var p = new ScreenPoint(info.pt.X, info.pt.Y);
                // Ignore the second click of a double-click.
                bool duplicate = _actions.Count > 0 && _actions[^1] is { Kind: RecordedActionKind.Click } last
                                 && last.Point.DistanceTo(p) < 6 * _scale && (DateTime.UtcNow - _lastClickAt).TotalMilliseconds < 350;
                if (!duplicate) Record(RecordedActionKind.Click, p, "");
                _lastClickAt = DateTime.UtcNow;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private DateTime _lastClickAt = DateTime.MinValue;

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP)
            {
                _keysDown.Remove(info.vkCode);
            }
            else if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN
                     && (info.flags & Native.LLKHF_INJECTED) == 0
                     && _keysDown.Add(info.vkCode) // not an auto-repeat
                     && !IsOwnWindow(Native.GetForegroundWindow())
                     && KeyName(info.vkCode) is { } name)
            {
                Record(RecordedActionKind.Key, default, name);
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Only dialog keys are recorded (1–0, Enter, Space). F8/Esc and everything else are ignored.</summary>
    private static string? KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => ((char)('0' + (vk - 0x60))).ToString(), // numpad
        0x0D => "Enter",
        0x20 => "Space",
        _ => null,
    };

    private bool IsOwnWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.GetAncestor(hwnd, Native.GA_ROOT) == _ownWindow;

    // ------------------------------------------------------------------ per action

    private void Record(RecordedActionKind kind, ScreenPoint point, string key)
    {
        // Copy the recent frames now (newest first); the copies are analysed in the background.
        List<(Bitmap Image, ScreenRect Area)> candidates;
        lock (_framesLock)
            candidates = _frames.AsEnumerable().Reverse().Select(f => (new Bitmap(f.Image), f.Area)).ToList();

        var analysis = Task.Run(() => AnalyseAsync(kind, key, candidates));
        _actions.Add(new PendingAction(kind, point, key, analysis));
        Changed?.Invoke(_actions.Count);
    }

    private async Task<(IReadOnlyList<TextHit>, byte[]?)> AnalyseAsync(
        RecordedActionKind kind, string key, List<(Bitmap Image, ScreenRect Area)> candidates)
    {
        try
        {
            IReadOnlyList<TextHit>? newest = null;
            foreach (var (image, area) in candidates)
            {
                var hits = await _screen.RecognizeAsync(image, area, CancellationToken.None);
                newest ??= hits;
                if (kind != RecordedActionKind.Key) return (hits, null);
                // For a key press, use the newest frame that still shows that option line
                // (the very latest frame may already show the next page), and remember how that page looks.
                if (TextMatching.FindDialogOption(key, hits) is { } option)
                {
                    var hash = WindowsScreenReader.Fingerprint(image, area, ImageFingerprint.PageRect(option.Rect, _scale));
                    return (hits, hash);
                }
            }
            return (newest ?? Array.Empty<TextHit>(), null);
        }
        catch
        {
            return (Array.Empty<TextHit>(), null);
        }
        finally
        {
            foreach (var (image, _) in candidates) image.Dispose();
        }
    }
}
