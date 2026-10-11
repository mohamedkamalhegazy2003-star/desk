using System.Drawing;
using System.Runtime.InteropServices;

namespace DuoSeat.Agent;

[StructLayout(LayoutKind.Sequential)]
internal struct DuoEvent                       // same 28-byte frame the service sends (DUOSEAT_EVENT)
{
    public uint DeviceId;
    public uint Type;
    public ushort MakeCode;
    public ushort KeyFlags;
    public ushort MouseFlags;
    public ushort ButtonFlags;
    public ushort ButtonData;
    public ushort Reserved;
    public int LastX;
    public int LastY;
}

/// <summary>
/// Injects station-2 input INSIDE the station-2 session. Because this process lives in that session,
/// the cursor and keyboard focus it drives are the session's own, independent of the station-1 desktop.
/// </summary>
internal sealed class Injector
{
    private const uint MOVE = 0x0001, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    private const ushort KEY_BREAK = 1, KEY_E0 = 2, KEY_E1 = 4;

    private readonly object _sync = new();
    private readonly HashSet<int> _keysDown = new();
    private ushort _buttonsDown;                        // bit mask of RI_MOUSE_*_DOWN flags currently held
    private Rectangle _v;
    private volatile int _px, _py;

    public Injector()
    {
        _v = SystemInformation.VirtualScreen;
        _px = _v.Left + _v.Width / 2;
        _py = _v.Top + _v.Height / 2;
    }

    public Point Position => new(_px, _py);

    public void Handle(DuoEvent e)
    {
        lock (_sync)
        {
            if (e.Type == 1) Key(e);
            else if (e.Type == 2) Mouse(e);
        }
    }

    private void Key(DuoEvent e)
    {
        bool up = (e.KeyFlags & KEY_BREAK) != 0;
        bool e0 = (e.KeyFlags & KEY_E0) != 0;
        if ((e.KeyFlags & KEY_E1) != 0 || e.MakeCode == 0 || e.MakeCode == 0xFF) return;

        int id = e.MakeCode | (e0 ? 0x10000 : 0);
        if (up) _keysDown.Remove(id); else _keysDown.Add(id);
        SendKey(e.MakeCode, e0, up);
    }

    private static void SendKey(ushort scan, bool e0, bool up)
    {
        uint flags = Native.KEYEVENTF_SCANCODE;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        if (e0) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.U.ki = new Native.KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    private void Mouse(DuoEvent e)
    {
        _v = SystemInformation.VirtualScreen;
        double x = _px, y = _py;
        if ((e.MouseFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0)
        {
            x = _v.Left + e.LastX * (double)(_v.Width - 1) / 65535.0;
            y = _v.Top + e.LastY * (double)(_v.Height - 1) / 65535.0;
        }
        else
        {
            x += e.LastX;
            y += e.LastY;
        }
        _px = (int)Math.Clamp(Math.Round(x), _v.Left, _v.Right - 1);
        _py = (int)Math.Clamp(Math.Round(y), _v.Top, _v.Bottom - 1);

        int nx = Norm(_px - _v.Left, _v.Width);
        int ny = Norm(_py - _v.Top, _v.Height);

        var inputs = new List<Native.INPUT>(5);
        void Add(uint flag, uint data = 0)
        {
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.U.mi = new Native.MOUSEINPUT { dx = nx, dy = ny, dwFlags = flag | ABSOLUTE | VIRTUALDESK, mouseData = data };
            inputs.Add(inp);
        }

        Add(MOVE);                                   // every packet carries the position, so clicks land exactly there
        ushort bf = e.ButtonFlags;
        Button(bf, Native.RI_MOUSE_LEFT_DOWN, Native.RI_MOUSE_LEFT_UP, Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP, Add);
        Button(bf, Native.RI_MOUSE_RIGHT_DOWN, Native.RI_MOUSE_RIGHT_UP, Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP, Add);
        Button(bf, Native.RI_MOUSE_MIDDLE_DOWN, Native.RI_MOUSE_MIDDLE_UP, Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP, Add);
        if ((bf & Native.RI_MOUSE_WHEEL) != 0) Add(Native.MOUSEEVENTF_WHEEL, (uint)(int)(short)e.ButtonData);
        if ((bf & Native.RI_MOUSE_HWHEEL) != 0) Add(Native.MOUSEEVENTF_HWHEEL, (uint)(int)(short)e.ButtonData);

        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    private void Button(ushort bf, ushort downFlag, ushort upFlag, uint downEvt, uint upEvt, Action<uint, uint> add)
    {
        if ((bf & downFlag) != 0) { _buttonsDown |= downFlag; add(downEvt, 0); }
        if ((bf & upFlag) != 0) { _buttonsDown &= (ushort)~downFlag; add(upEvt, 0); }
    }

    private static int Norm(int value, int size) =>
        size <= 1 ? 0 : (int)Math.Round(value * 65535.0 / (size - 1));

    /// <summary>Called when the service goes away: nothing may stay pressed in the station-2 session.</summary>
    public void ReleaseAll()
    {
        lock (_sync)
        {
            foreach (int id in _keysDown)
                SendKey((ushort)(id & 0xFFFF), (id & 0x10000) != 0, true);
            _keysDown.Clear();

            var inputs = new List<Native.INPUT>(3);
            void Up(ushort downFlag, uint upEvt)
            {
                if ((_buttonsDown & downFlag) == 0) return;
                var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
                inp.U.mi = new Native.MOUSEINPUT { dwFlags = upEvt };
                inputs.Add(inp);
            }
            Up(Native.RI_MOUSE_LEFT_DOWN, Native.MOUSEEVENTF_LEFTUP);
            Up(Native.RI_MOUSE_RIGHT_DOWN, Native.MOUSEEVENTF_RIGHTUP);
            Up(Native.RI_MOUSE_MIDDLE_DOWN, Native.MOUSEEVENTF_MIDDLEUP);
            _buttonsDown = 0;
            if (inputs.Count > 0)
                Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
        }
    }
}
