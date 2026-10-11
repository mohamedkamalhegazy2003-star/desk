using System.Drawing;
using System.Runtime.InteropServices;

namespace DuoSeat.Agent;

/// <summary>
/// The RDP client on monitor 2 only draws a pointer where ITS local mouse is, and the local mouse is
/// the station-1 one (which the service keeps off monitor 2). So the agent draws station 2's pointer
/// itself: a click-through, always-on-top window that shows the session's current cursor shape at the
/// position the agent tracks. The shape comes from GetCursorInfo (arrow, I-beam, resize...).
/// </summary>
internal sealed class CursorOverlay : Form
{
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly Injector _injector;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    private IntPtr _shape = IntPtr.Zero;
    private int _hotX, _hotY;
    private bool _wantVisible, _shown;

    public CursorOverlay(Injector injector)
    {
        _injector = injector;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new Size(64, 64);
        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Start()
    {
        _ = Handle;                 // create the window without showing it
        _timer.Start();
    }

    public void SetConnected(bool connected)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => { _wantVisible = connected; });
    }

    private void Tick()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        bool visible = _wantVisible && GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor != IntPtr.Zero;

        if (!visible)
        {
            if (_shown) { ShowWindow(Handle, 0); _shown = false; }
            return;
        }

        if (ci.hCursor != _shape)
        {
            _shape = ci.hCursor;
            _hotX = _hotY = 0;
            if (GetIconInfo(_shape, out ICONINFO info))
            {
                _hotX = info.xHotspot;
                _hotY = info.yHotspot;
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            }
            Invalidate();
        }

        var p = _injector.Position;
        SetWindowPos(Handle, HWND_TOPMOST, p.X - _hotX, p.Y - _hotY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        if (!_shown) { ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _shown = true; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_shape == IntPtr.Zero) return;
        IntPtr hdc = e.Graphics.GetHdc();
        try { DrawIconEx(hdc, 0, 0, _shape, 0, 0, 0, IntPtr.Zero, 3 /*DI_NORMAL*/); }
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Native.POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
