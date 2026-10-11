using System.Drawing;
using System.Runtime.InteropServices;

namespace DuoSeat;

/// <summary>
/// Splits physical keyboards/mice between two stations.
///
/// How it works:
///  1. Raw Input (WM_INPUT) tells us WHICH physical device produced an event.
///  2. Low-level hooks swallow all real (non-injected) keyboard/mouse events so Windows itself
///     never sees them.
///  3. For every raw event we re-inject it with SendInput, after pointing the system cursor /
///     keyboard focus at the right place: station 1 = normal desktop, station 2 = the fullscreen
///     RDP window sitting on the second monitor.
///
/// Everything runs on the UI thread (hooks + WM_INPUT share the message loop), so no locking.
/// Panic switch: Ctrl+Alt+F12 toggles routing on/off.
/// </summary>
internal sealed class InputRouter : IDisposable
{
    private const string RdpWindowClass = "TscShellContainerClass";

    private readonly Config _cfg;
    private readonly Dictionary<IntPtr, string> _names = new();
    private HashSet<string> _station2 = new(StringComparer.OrdinalIgnoreCase);

    private readonly Native.HookProc _kbProc;
    private readonly Native.HookProc _msProc;
    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _msHook = IntPtr.Zero;

    private readonly Rectangle[] _bounds = new Rectangle[2];
    private readonly Point[] _pos = new Point[2];
    private double _speed = 1.5;

    private IntPtr _rdpHwnd = IntPtr.Zero;
    private IntPtr _station1Foreground = IntPtr.Zero;

    private bool _ctrl, _alt;

    public bool Active { get; private set; }

    /// <summary>True when the station-2 RDP window is currently open.</summary>
    public bool Station2Connected => RdpWindow() != IntPtr.Zero;

    /// <summary>Re-reads which devices belong to station 2 (used when the user drags a device to another workplace).</summary>
    public void ReloadAssignments()
    {
        _station2 = new HashSet<string>(_cfg.Station2Devices, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised for every raw event: (device path, isKeyboard). Used by the "identify device" feature.</summary>
    public event Action<string, bool>? DeviceSeen;

    public event Action<bool>? ActiveChanged;

    public InputRouter(Config cfg)
    {
        _cfg = cfg;
        _kbProc = KeyboardHook;
        _msProc = MouseHook;
    }

    public void RegisterRawInput(IntPtr hwnd)
    {
        var rid = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
            new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        if (!Native.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
    }

    public void Toggle()
    {
        if (Active) Stop(); else Start();
    }

    public void Start()
    {
        if (Active) return;

        var screens = Screen.AllScreens;
        int i1 = Math.Clamp(_cfg.Monitor1, 0, screens.Length - 1);
        int i2 = Math.Clamp(_cfg.Monitor2, 0, screens.Length - 1);
        _bounds[0] = screens[i1].Bounds;
        _bounds[1] = screens[i2].Bounds;
        for (int i = 0; i < 2; i++)
            _pos[i] = new Point(_bounds[i].Left + _bounds[i].Width / 2, _bounds[i].Top + _bounds[i].Height / 2);

        _speed = Math.Clamp(_cfg.MouseSpeed, 0.1, 10);
        _station2 = new HashSet<string>(_cfg.Station2Devices, StringComparer.OrdinalIgnoreCase);
        _ctrl = _alt = false;
        _station1Foreground = IntPtr.Zero;

        IntPtr mod = Native.GetModuleHandle(null);
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
        _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, mod, 0);
        if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            RemoveHooks();
            throw new InvalidOperationException("SetWindowsHookEx failed: " + err);
        }

        Active = true;
        ActiveChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!Active && _kbHook == IntPtr.Zero && _msHook == IntPtr.Zero) return;
        Active = false;
        RemoveHooks();
        ReleaseModifiers();
        ActiveChanged?.Invoke(false);
    }

    private void RemoveHooks()
    {
        if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ hooks

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Active)
        {
            var s = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((s.flags & Native.LLKHF_INJECTED) == 0)
                return (IntPtr)1; // real hardware key: swallowed, re-injected from the raw-input handler
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Active)
        {
            var s = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool injected = (s.flags & Native.LLMHF_INJECTED) != 0;
            // Touch/pen-synthesised mouse events carry this signature and have no raw mouse data: let them through.
            bool touchOrPen = (s.dwExtraInfo.ToInt64() & 0xFFFFFF00L) == 0xFF515700L;
            if (!injected && !touchOrPen)
                return (IntPtr)1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // ------------------------------------------------------------------ raw input

    public void OnRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buf, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
            if (header.hDevice == IntPtr.Zero) return; // injected / virtual input (including our own)

            if (!_names.TryGetValue(header.hDevice, out string? name))
            {
                name = DeviceList.GetDeviceName(header.hDevice);
                _names[header.hDevice] = name;
            }

            bool isKeyboard = header.dwType == Native.RIM_TYPEKEYBOARD;
            if (header.dwType != Native.RIM_TYPEKEYBOARD && header.dwType != Native.RIM_TYPEMOUSE) return;

            DeviceSeen?.Invoke(name, isKeyboard);
            if (!Active) return;

            int station = _station2.Contains(name) ? 2 : 1;
            IntPtr data = buf + (int)headerSize;

            if (isKeyboard)
                HandleKey(station, Marshal.PtrToStructure<Native.RAWKEYBOARD>(data));
            else
                HandleMouse(station, Marshal.PtrToStructure<Native.RAWMOUSE>(data));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private void HandleKey(int station, Native.RAWKEYBOARD k)
    {
        if (k.VKey == 0xFF) return; // fake keys generated by the driver

        bool up = (k.Flags & Native.RI_KEY_BREAK) != 0;
        bool e0 = (k.Flags & Native.RI_KEY_E0) != 0;
        bool e1 = (k.Flags & Native.RI_KEY_E1) != 0;

        // Panic switch: Ctrl+Alt+F12
        switch (k.VKey)
        {
            case 0x11: _ctrl = !up; break;
            case 0x12: _alt = !up; break;
            case 0x7B:
                if (!up && _ctrl && _alt)
                {
                    Stop();
                    return;
                }
                break;
        }

        if (e1 || k.MakeCode == 0) return; // Pause/Break and unknown keys are not forwarded

        FocusFor(station);

        uint flags = Native.KEYEVENTF_SCANCODE;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        if (e0) flags |= Native.KEYEVENTF_EXTENDEDKEY;

        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.U.ki = new Native.KEYBDINPUT { wVk = 0, wScan = k.MakeCode, dwFlags = flags };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    private void HandleMouse(int station, Native.RAWMOUSE m)
    {
        int i = station - 1;
        var b = _bounds[i];

        if ((m.usFlags & Native.MOUSE_MOVE_ABSOLUTE) == 0 && (m.lLastX != 0 || m.lLastY != 0))
        {
            int x = _pos[i].X + (int)Math.Round(m.lLastX * _speed);
            int y = _pos[i].Y + (int)Math.Round(m.lLastY * _speed);
            _pos[i] = new Point(Math.Clamp(x, b.Left, b.Right - 1), Math.Clamp(y, b.Top, b.Bottom - 1));
        }

        Native.SetCursorPos(_pos[i].X, _pos[i].Y);

        ushort bf = m.usButtonFlags;
        if (bf == 0) return;

        if (station == 2 && (bf & (Native.RI_MOUSE_LEFT_DOWN | Native.RI_MOUSE_RIGHT_DOWN | Native.RI_MOUSE_MIDDLE_DOWN)) != 0)
            FocusFor(2);

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
        if ((bf & Native.RI_MOUSE_WHEEL) != 0) Add(Native.MOUSEEVENTF_WHEEL, (uint)(int)(short)m.usButtonData);
        if ((bf & Native.RI_MOUSE_HWHEEL) != 0) Add(Native.MOUSEEVENTF_HWHEEL, (uint)(int)(short)m.usButtonData);

        if (inputs.Count > 0)
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    // ------------------------------------------------------------------ focus handling

    private IntPtr RdpWindow()
    {
        if (_rdpHwnd == IntPtr.Zero || !Native.IsWindow(_rdpHwnd))
            _rdpHwnd = Native.FindWindow(RdpWindowClass, null);
        return _rdpHwnd;
    }

    /// <summary>Makes sure keyboard focus is where the given station expects it.</summary>
    private void FocusFor(int station)
    {
        IntPtr rdp = RdpWindow();
        if (rdp == IntPtr.Zero) return; // station 2 not connected yet: everything goes to the normal desktop

        IntPtr fg = Native.GetForegroundWindow();
        if (station == 2)
        {
            if (fg != rdp)
            {
                if (fg != IntPtr.Zero) _station1Foreground = fg;
                ForceForeground(rdp);
            }
        }
        else if (fg == rdp && _station1Foreground != IntPtr.Zero && Native.IsWindow(_station1Foreground))
        {
            ForceForeground(_station1Foreground);
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

    /// <summary>Releases Ctrl/Alt/Shift so no modifier stays "stuck" after routing stops.</summary>
    private static void ReleaseModifiers()
    {
        ushort[] vks = { 0x10, 0x11, 0x12 }; // Shift, Ctrl, Alt
        var inputs = new Native.INPUT[vks.Length];
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[i] = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            inputs[i].U.ki = new Native.KEYBDINPUT { wVk = vks[i], dwFlags = Native.KEYEVENTF_KEYUP };
        }
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }
}
