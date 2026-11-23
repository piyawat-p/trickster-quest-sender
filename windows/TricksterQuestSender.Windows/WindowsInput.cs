using TricksterQuestSender.Core;

namespace QuestSenderWin;

/// <summary>Real mouse clicks and key presses via SendInput. Does not touch the game itself.</summary>
internal sealed class WindowsInput : IInputDriver
{
    public ScreenPoint CursorPosition
    {
        get
        {
            Native.GetCursorPos(out var p);
            return new ScreenPoint(p.X, p.Y);
        }
    }

    public async Task ClickAsync(ScreenPoint point, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Native.SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        // No cancellation inside a click: never leave the button held down.
        await Task.Delay(40, CancellationToken.None);
        Native.SendMouse(Native.MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(60, CancellationToken.None);
        Native.SendMouse(Native.MOUSEEVENTF_LEFTUP);
    }

    public async Task PressKeyAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ushort vk = VirtualKey(key);
        Native.SendKey(vk, up: false);
        await Task.Delay(50, CancellationToken.None);
        Native.SendKey(vk, up: true);
    }

    public static ushort VirtualKey(string key) => key switch
    {
        "Enter" => 0x0D,
        "Space" => 0x20,
        _ when key.Length == 1 && char.IsDigit(key[0]) => key[0], // '0'..'9' = 0x30..0x39
        _ => 0x31,
    };
}
