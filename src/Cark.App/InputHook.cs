using System.Runtime.InteropServices;

namespace Cark.App;

public enum WheelMode { Convert, Tools, Send }

/// <summary>
/// Global low-level mouse and keyboard hooks. Detects "a modifier was pressed while the
/// left button is held and the mouse has moved", which is how a wheel is summoned.
/// Passes every event on, except Alt and Escape while a click-mode wheel is open
/// (<see cref="SwallowClickKeys"/>), so Explorer does not open its menu bar under the wheel.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const int DragThreshold = 8;

    private readonly Native.LowLevelProc _mouseProc;
    private readonly Native.LowLevelProc _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;

    private bool _leftDown;
    private bool _dragging;
    private Point _downAt;
    private readonly HashSet<uint> _held = new();
    private readonly HashSet<uint> _swallowed = new();

    /// <summary>A modifier was pressed during a drag. Point is in physical screen pixels.</summary>
    public event Action<WheelMode, Point>? ModifierDuringDrag;

    /// <summary>The left button was released.</summary>
    public event Action? LeftButtonUp;

    /// <summary>A key went down (repeats excluded). Raised always.</summary>
    public event Action<uint>? KeyDown;

    /// <summary>The left or right button went down. Point is in physical screen pixels. Raised always.</summary>
    public event Action<Point>? ButtonDown;

    public bool IsDragging => _dragging;

    /// <summary>
    /// While true, a fresh Alt or Escape press is swallowed. A key-up is swallowed only when
    /// its key-down was, so no key is ever left stuck.
    /// </summary>
    public volatile bool SwallowClickKeys;

    private static bool IsClickKey(uint vk) => vk is 0x12 or 0xA4 or 0xA5 or 0x1B;

    public InputHook()
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
    }

    public void Install()
    {
        IntPtr module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        Log.Write($"hooks installed mouse={_mouseHook != IntPtr.Zero} key={_keyHook != IntPtr.Zero} err={Marshal.GetLastWin32Error()}");
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var p = new Point(data.pt.X, data.pt.Y);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _leftDown = true;
                    _dragging = false;
                    _downAt = p;
                    ButtonDown?.Invoke(p);
                    break;
                case Native.WM_RBUTTONDOWN:
                    ButtonDown?.Invoke(p);
                    break;
                case Native.WM_MOUSEMOVE:
                    if (_leftDown && !_dragging &&
                        (Math.Abs(p.X - _downAt.X) > DragThreshold || Math.Abs(p.Y - _downAt.Y) > DragThreshold))
                    {
                        _dragging = true;
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    _leftDown = false;
                    _dragging = false;
                    LeftButtonUp?.Invoke();
                    break;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

            if (up)
            {
                _held.Remove(data.vkCode);
                if (_swallowed.Remove(data.vkCode)) return 1;
            }
            else if (down)
            {
                if (_held.Add(data.vkCode))
                {
                    if (_dragging)
                    {
                        WheelMode? mode = data.vkCode switch
                        {
                            0xA0 or 0xA1 or 0x10 => WheelMode.Convert, // Shift
                            0xA2 or 0xA3 or 0x11 => WheelMode.Tools,   // Ctrl
                            0xA4 or 0xA5 or 0x12 => WheelMode.Send,    // Alt
                            _ => null
                        };
                        if (mode is not null)
                        {
                            ModifierDuringDrag?.Invoke(mode.Value, Cursor.Position);
                        }
                    }
                    KeyDown?.Invoke(data.vkCode);
                    if (SwallowClickKeys && IsClickKey(data.vkCode)) _swallowed.Add(data.vkCode);
                }
                if (_swallowed.Contains(data.vkCode)) return 1; // repeats follow their first press
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
