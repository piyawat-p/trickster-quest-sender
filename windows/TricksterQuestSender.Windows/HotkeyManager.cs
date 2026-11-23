using System.Windows;
using System.Windows.Interop;

namespace QuestSenderWin;

/// <summary>Global hotkeys (work while the browser is in front).</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
    }

    /// <returns>Id to unregister with, or -1 if another app already uses the key.</returns>
    public int Register(uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, Native.MOD_NOREPEAT, virtualKey)) return -1;
        _actions[id] = action;
        return id;
    }

    public void Unregister(int id)
    {
        if (id > 0 && _actions.Remove(id)) Native.UnregisterHotKey(_hwnd, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList()) Unregister(id);
        _source.RemoveHook(WndProc);
    }
}
