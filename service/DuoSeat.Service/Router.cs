using System.Drawing;
using System.Runtime.InteropServices;

namespace DuoSeat.Service;

/// <summary>
/// Receives the input the driver captured from station-2 devices and delivers it to the station-2
/// session (the fullscreen RDP client window on the second monitor).
/// Station-1 devices are never touched: the driver hands their input straight to Windows.
/// </summary>
internal sealed class Router
{
    private const string RdpWindowClass = "TscShellContainerClass";
    private const ushort KEY_BREAK = 1, KEY_E0 = 2, KEY_E1 = 4;

    private readonly DriverClient _drv;
    private readonly ServiceConfig _cfg;
    private readonly object _sync = new();

    private Rectangle _bounds2;
    private Point _pos2;
    private double _speed;
    private readonly IAgentLink? _agent;
    private Rectangle _bounds1;
    private double _remX, _remY;
    private IntPtr _rdp = IntPtr.Zero;
    private IntPtr _station1Foreground = IntPtr.Zero;
    private bool _rdpHasFocusLease;
    private long _lastInputTick;

    /// <param name="agent">When set, station-2 input is sent to the agent inside the station-2 session (independent cursor). When null, legacy mode: inject into the RDP window from session 1.</param>
    public Router(DriverClient drv, ServiceConfig cfg, IAgentLink? agent = null)
    {
        _drv = drv;
        _cfg = cfg;
        _agent = agent;
    }

    public static void ApplyAssignments(DriverClient drv, ServiceConfig cfg)
    {
        foreach (var d in drv.GetDevices())
        {
            uint wanted = cfg.IsStation2(d.InstanceId) ? DriverClient.StationSeat2 : DriverClient.StationNormal;
            if (d.Station != wanted && d.Station != DriverClient.StationMonitor)
                drv.SetStation(d.Id, wanted);
        }
    }

    public void Run(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var screens = Screen.AllScreens;
        int i2 = Math.Clamp(_cfg.Monitor2, 0, screens.Length - 1);
        int i1 = Math.Clamp(_cfg.Monitor1, 0, screens.Length - 1);
        _bounds1 = screens[i1].Bounds;
        _bounds2 = screens[i2].Bounds;
        _pos2 = new Point(_bounds2.Left + _bounds2.Width / 2, _bounds2.Top + _bounds2.Height / 2);
        _speed = Math.Clamp(_cfg.MouseSpeed, 0.1, 10);

        ApplyAssignments(_drv, _cfg);
        _drv.SetRouting(true);
        _agent?.Start(ct);

        // background helpers: re-apply assignments when devices are (re)plugged, panic hotkey, focus lease
        var helpers = new[]
        {
            Task.Run(() => Loop(ct, 2000, () => ApplyAssignments(_drv, _cfg))),
            Task.Run(() => Loop(ct, 50, () => { if (PanicPressed()) cts.Cancel(); })),
            Task.Run(() => Loop(ct, 100, ReleaseLeaseIfIdle)),
            Task.Run(() => Loop(ct, 250, () => { if (_agent != null) ApplyClip(); })),
        };

        try
        {
            var buffer = new DuoEvent[64];
            while (!ct.IsCancellationRequested)
            {
                int n = _drv.ReadEvents(buffer, ct);
                for (int i = 0; i < n; i++)
                {
                    lock (_sync) Handle(buffer[i]);
                }
            }
        }
        finally
        {
            cts.Cancel();
            try { Task.WaitAll(helpers, 2000); } catch { /* ignore */ }
            try { _drv.SetRouting(false); } catch { /* driver also does this when the handle closes */ }
            if (_agent != null) ClipCursorNone(IntPtr.Zero);
            else ReleaseModifiers();
        }
    }

    private static void Loop(CancellationToken ct, int periodMs, Action action)
    {
        while (!ct.IsCancellationRequested)
        {
            try { action(); } catch { /* keep going */ }
            ct.WaitHandle.WaitOne(periodMs);
        }
    }

    // ---------------------------------------------------------------- events

    private void Handle(DuoEvent e)
    {
        if (_agent != null) { Forward(e); return; }
        if (e.Type == 1) HandleKey(e);
        else if (e.Type == 2) HandleMouse(e);
    }

    /// <summary>Agent mode: pass the event to the agent in session 2 (deltas scaled by MouseSpeed, with remainder kept).</summary>
    private void Forward(DuoEvent e)
    {
        if (e.Type == 2 && (e.MouseFlags & Native.MOUSE_MOVE_ABSOLUTE) == 0)
        {
            double fx = e.LastX * _speed + _remX, fy = e.LastY * _speed + _remY;
            int dx = (int)Math.Round(fx), dy = (int)Math.Round(fy);
            _remX = fx - dx;
            _remY = fy - dy;
            e.LastX = dx;
            e.LastY = dy;
        }
        _agent!.Send(e);   // no agent connected: the event is dropped
    }

    /// <summary>
    /// Keeps the station-1 mouse on monitor 1 so it never enters the RDP window on monitor 2
    /// (otherwise the RDP client would draw/steer by that pointer instead of the agent's).
    /// </summary>
    private void ApplyClip()
    {
        var r = new RECT { Left = _bounds1.Left, Top = _bounds1.Top, Right = _bounds1.Right, Bottom = _bounds1.Bottom };
        ClipCursorRect(ref r);
    }

    private void HandleKey(DuoEvent e)
    {
        bool up = (e.KeyFlags & KEY_BREAK) != 0;
        bool e0 = (e.KeyFlags & KEY_E0) != 0;
        bool e1 = (e.KeyFlags & KEY_E1) != 0;
        if (e1 || e.MakeCode == 0 || e.MakeCode == 0xFF) return;   // Pause/Break, overrun markers: not forwarded

        FocusStation2();

        uint flags = Native.KEYEVENTF_SCANCODE;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        if (e0) flags |= Native.KEYEVENTF_EXTENDEDKEY;

        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.U.ki = new Native.KEYBDINPUT { wVk = 0, wScan = e.MakeCode, dwFlags = flags };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
        Touch();
    }

    private void HandleMouse(DuoEvent e)
    {
        if ((e.MouseFlags & Native.MOUSE_MOVE_ABSOLUTE) == 0 && (e.LastX != 0 || e.LastY != 0))
        {
            int x = _pos2.X + (int)Math.Round(e.LastX * _speed);
            int y = _pos2.Y + (int)Math.Round(e.LastY * _speed);
            _pos2 = new Point(Math.Clamp(x, _bounds2.Left, _bounds2.Right - 1),
                              Math.Clamp(y, _bounds2.Top, _bounds2.Bottom - 1));
        }
        Native.SetCursorPos(_pos2.X, _pos2.Y);

        ushort bf = e.ButtonFlags;
        if (bf == 0) return;

        if ((bf & (Native.RI_MOUSE_LEFT_DOWN | Native.RI_MOUSE_RIGHT_DOWN | Native.RI_MOUSE_MIDDLE_DOWN)) != 0)
            FocusStation2();

        var inputs = new List<Native.INPUT>(4);
        void Add(uint flag, uint data = 0)
        {
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.U.mi = new Native.MOUSEINPUT { dwFlags = flag, mouseData = data };
            inputs.Add(inp);
        }

        if ((bf & Native.RI_MOUSE_LEFT_DOWN) != 0) Add(Native.MOUSEEVENTF_LEFTDOWN);
        if ((bf & Native.RI_MOUSE_LEFT_UP) != 0) Add(Native.MOUSEEVENTF_LEFTUP);
        if ((bf & Native.RI_MOUSE_RIGHT_DOWN) != 0) Add(Native.MOUSEEVENTF_RIGHTDOWN);
        if ((bf & Native.RI_MOUSE_RIGHT_UP) != 0) Add(Native.MOUSEEVENTF_RIGHTUP);
        if ((bf & Native.RI_MOUSE_MIDDLE_DOWN) != 0) Add(Native.MOUSEEVENTF_MIDDLEDOWN);
        if ((bf & Native.RI_MOUSE_MIDDLE_UP) != 0) Add(Native.MOUSEEVENTF_MIDDLEUP);
        if ((bf & Native.RI_MOUSE_WHEEL) != 0) Add(Native.MOUSEEVENTF_WHEEL, (uint)(int)(short)e.ButtonData);
        if ((bf & Native.RI_MOUSE_HWHEEL) != 0) Add(Native.MOUSEEVENTF_HWHEEL, (uint)(int)(short)e.ButtonData);

        if (inputs.Count > 0)
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
        Touch();
    }

    // ---------------------------------------------------------------- focus handling

    private void Touch() => _lastInputTick = Environment.TickCount64;

    private IntPtr RdpWindow()
    {
        if (_rdp == IntPtr.Zero || !Native.IsWindow(_rdp))
            _rdp = Native.FindWindow(RdpWindowClass, null);
        return _rdp;
    }

    private void FocusStation2()
    {
        IntPtr rdp = RdpWindow();
        if (rdp == IntPtr.Zero) return;                  // station 2 not connected: input goes to the normal desktop

        IntPtr fg = Native.GetForegroundWindow();
        if (fg != rdp)
        {
            if (fg != IntPtr.Zero) _station1Foreground = fg;
            ForceForeground(rdp);
        }
        _rdpHasFocusLease = true;
        Touch();
    }

    /// <summary>Station-1 input is invisible to us, so we give focus back after a short idle time.</summary>
    private void ReleaseLeaseIfIdle()
    {
        lock (_sync)
        {
            if (!_rdpHasFocusLease) return;
            if (Environment.TickCount64 - _lastInputTick < _cfg.FocusLeaseMs) return;

            _rdpHasFocusLease = false;
            IntPtr rdp = RdpWindow();
            if (rdp != IntPtr.Zero && Native.GetForegroundWindow() == rdp
                && _station1Foreground != IntPtr.Zero && Native.IsWindow(_station1Foreground))
            {
                ForceForeground(_station1Foreground);
            }
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = Native.GetForegroundWindow();
        uint me = Native.GetCurrentThreadId();
        uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
        if (attached) Native.AttachThreadInput(me, fgThread, false);
    }

    private static bool PanicPressed() =>
        (GetAsyncKeyState(0x11) & 0x8000) != 0 &&   // Ctrl
        (GetAsyncKeyState(0x12) & 0x8000) != 0 &&   // Alt
        (GetAsyncKeyState(0x7B) & 0x8000) != 0;     // F12

    private static void ReleaseModifiers()
    {
        ushort[] vks = { 0x10, 0x11, 0x12 };
        var inputs = new Native.INPUT[vks.Length];
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[i] = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            inputs[i].U.ki = new Native.KEYBDINPUT { wVk = vks[i], dwFlags = Native.KEYEVENTF_KEYUP };
        }
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ClipCursorRect(ref RECT rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    private static extern bool ClipCursorNone(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
