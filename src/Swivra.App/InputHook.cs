using System.Runtime.InteropServices;

namespace Swivra.App;

public enum WheelMode { Convert, Tools, Send }

/// <summary>
/// Global low-level mouse and keyboard hooks. Detects "a modifier was pressed while the
/// left button is held and the mouse has moved", which is how a wheel is summoned.
/// Never swallows input: every event is passed on.
/// The hooks live on their own thread with its own message loop. Windows silently removes a low-level
/// hook whose callback is late (LowLevelHooksTimeout, about a second); on the UI thread any stall
/// (a clipboard call, ShellExecute, WebView2) could do that and the wheel would stop opening.
/// Events are raised on the hook thread; subscribers marshal to the UI themselves.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const int DragThreshold = 8;
    private const uint WM_QUIT = 0x0012;

    private readonly Native.LowLevelProc _mouseProc;
    private readonly Native.LowLevelProc _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private Thread? _thread;
    private uint _threadId;

    private bool _leftDown;
    private volatile bool _dragging; // written on the hook thread, read on the UI thread
    private Point _downAt;
    private readonly HashSet<uint> _held = new();

    /// <summary>A modifier was pressed during a drag. Point is in physical screen pixels.</summary>
    public event Action<WheelMode, Point>? ModifierDuringDrag;

    /// <summary>The left button was released.</summary>
    public event Action? LeftButtonUp;

    /// <summary>A key went down (first press only, no auto-repeat). Carries the virtual key code.</summary>
    public event Action<uint>? KeyPressed;

    public bool IsDragging => _dragging;

    public InputHook()
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
    }

    /// <summary>Starts the hook thread and returns once both hooks are installed (or failed).</summary>
    public void Install()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Swivra input hooks" };
        _thread.Start();
        ready.Wait();
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        IntPtr module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        Log.Write($"hooks installed mouse={_mouseHook != IntPtr.Zero} key={_keyHook != IntPtr.Zero} err={Marshal.GetLastWin32Error()}");
        ready.Set();

        // Low-level hook callbacks are delivered through this thread's message loop.
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
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

            bool firstPress = down && _held.Add(data.vkCode);
            if (up) _held.Remove(data.vkCode);
            if (firstPress) KeyPressed?.Invoke(data.vkCode);

            if (firstPress && _dragging)
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
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    /// <summary>Ends the message loop; the hook thread unhooks on its way out.</summary>
    public void Dispose()
    {
        if (_thread is null) return;
        Native.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }
}
