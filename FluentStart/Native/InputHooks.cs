using System;
using System.Runtime.InteropServices;
using System.Threading;
using static FluentStart.Native.NativeMethods;

namespace FluentStart.Native;

/// <summary>
/// Low-level keyboard + mouse hooks on a dedicated thread with its own message loop (so UI stalls never delay system input).
/// Raises <see cref="Trigger"/> (on the hook thread - marshal it!) for a lone Win tap or a left click on the
/// native Start button (rects from <see cref="StartButtonLocator"/>). Callbacks must stay fast and never throw.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private static readonly UIntPtr Marker = (UIntPtr)0x46535431; // tags our injected keys
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207;

    private readonly StartButtonLocator _locator;
    private readonly HookProc _kbProc;
    private readonly HookProc _msProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _kbHook, _msHook;
    private bool _winDown, _otherKey, _swallowedDown;
    private volatile bool _stopRequested;
    private int _disposed;

    public volatile bool EnableWinKey = true;
    public volatile bool EnableStartButton = true;

    public bool Installed { get; private set; }
    public event Action? Trigger;
    public event Action? SearchTrigger;
    /// <summary>Raised on the hook thread for every mouse button down, with physical screen coordinates.</summary>
    public event Action<int, int>? GlobalMouseDown;

    public InputHooks(StartButtonLocator locator)
    {
        _locator = locator;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    public bool Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "InputHooks" };
        _thread.Start();
        if (!_ready.Wait(3000))
        {
            Log.Warn("Timed out waiting for input hooks to initialize");
            Dispose();
            return false;
        }
        return Installed;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0x0400, 0x0400, 0); // force creation of this thread's message queue

        try
        {
            IntPtr mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            Installed = _kbHook != IntPtr.Zero && _msHook != IntPtr.Zero;
            if (!Installed) Log.Warn($"Hook install failed (kb={_kbHook}, ms={_msHook}, err={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) { Log.Warn("Input hook initialization failed", ex); }
        finally { _ready.Set(); }

        if (!Installed || _stopRequested)
        {
            UninstallHooks();
            return;
        }

        try
        {
            while (!_stopRequested && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally { UninstallHooks(); }
    }

    private void UninstallHooks()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        _kbHook = _msHook = IntPtr.Zero;
        Installed = false;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                // KBDLLHOOKSTRUCT: vkCode @0, flags @8, dwExtraInfo @16 - read the two fields we need instead of marshaling the struct (hot path: every key).
                uint vk = (uint)Marshal.ReadInt32(lParam);
                var extra = (UIntPtr)(ulong)Marshal.ReadIntPtr(lParam, 16).ToInt64();
                if (extra != Marker)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    bool isWin = vk == VK_LWIN || vk == VK_RWIN;

                    if (isWin)
                    {
                        if (EnableWinKey)
                        {
                            if (down && !_winDown) { _winDown = true; _otherKey = false; }
                            else if (up && _winDown)
                            {
                                _winDown = false;
                                if (!_otherKey)
                                {
                                    // Make the shell see Win+<key> so it does not open its own Start on Win-up.
                                    SendDummyKey();
                                    Trigger?.Invoke();
                                }
                            }
                        }
                    }
                    else if (down)
                    {
                        if (_winDown && vk == 0x53)   // Win+S -> open search instead of native search
                        {
                            _otherKey = true;
                            SendDummyKey();
                            SearchTrigger?.Invoke();
                            return (IntPtr)1;
                        }
                        if (_winDown) _otherKey = true;
                    }
                }
            }
        }
        catch (Exception ex) { Log.WarnOnce("kbproc", "Keyboard hook error", ex); }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN)
                {
                    int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);   // MSLLHOOKSTRUCT.pt

                    if (msg == WM_LBUTTONUP && _swallowedDown)
                    {
                        _swallowedDown = false;
                        if (EnableStartButton && IsInsideStartButton(x, y)) Trigger?.Invoke();
                        return (IntPtr)1;
                    }

                    if (msg == WM_LBUTTONDOWN && EnableStartButton && Installed && IsInsideStartButton(x, y))
                    {
                        _swallowedDown = true;
                        GlobalMouseDown?.Invoke(x, y);
                        return (IntPtr)1;
                    }

                    if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                        GlobalMouseDown?.Invoke(x, y);
                }
            }
        }
        catch (Exception ex) { Log.WarnOnce("msproc", "Mouse hook error", ex); }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private bool IsInsideStartButton(int x, int y)
    {
        foreach (var b in _locator.Current)
            if (x >= b.L && x < b.R && y >= b.T && y < b.B) return true;
        return false;
    }

    private static void SendDummyKey()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0xE8, dwExtraInfo = Marker } } };
        inputs[1] = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0xE8, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = Marker } } };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopRequested = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1500);
    }
}
