using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DuoSeat;

internal static class Theme
{
    public static readonly Color Line = Color.FromArgb(0x8F, 0xB4, 0xDC);
    public static readonly Color LineSoft = Color.FromArgb(0xC4, 0xD6, 0xEA);
    public static readonly Color PanelGrey = Color.FromArgb(0xF3, 0xF3, 0xF3);
    public static readonly Color Orange = Color.FromArgb(0xF5, 0xA6, 0x23);
    public static readonly Color Green = Color.FromArgb(0x3D, 0xBE, 0x29);
    public static readonly Color Amber = Color.FromArgb(0xF2, 0x9B, 0x1D);
    public static readonly Color Red = Color.FromArgb(0xD9, 0x26, 0x1C);
    public static readonly Color Ink = Color.FromArgb(0x1F, 0x3A, 0x5F);
    public static readonly Color Grey = Color.FromArgb(0x55, 0x55, 0x55);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Small vector icons drawn with GDI+ (no image resources needed).</summary>
internal static class Icons
{
    public static void Keyboard(Graphics g, Rectangle r)
    {
        var body = new RectangleF(r.X + 1, r.Y + r.Height * 0.2f, r.Width - 2, r.Height * 0.6f);
        using (var path = Theme.Round(body, 3))
        using (var fill = new SolidBrush(Color.FromArgb(0xE8, 0xE8, 0xE8)))
        using (var pen = new Pen(Color.FromArgb(0x70, 0x70, 0x70), 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        using var key = new SolidBrush(Color.FromArgb(0x8C, 0x8C, 0x8C));
        float innerW = body.Width - 6, innerH = body.Height - 6;
        float kw = innerW / 7f, kh = innerH / 3f;
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 7; col++)
                g.FillRectangle(key, body.X + 3 + col * kw, body.Y + 3 + row * kh, kw - 1.5f, kh - 1.5f);
        g.FillRectangle(key, body.X + 3 + kw, body.Y + 3 + 2 * kh, kw * 5 - 1.5f, kh - 1.5f);
    }

    public static void Mouse(Graphics g, Rectangle r)
    {
        float w = Math.Min(r.Width * 0.55f, r.Height * 0.62f);
        float h = r.Height - 2;
        float x = r.X + (r.Width - w) / 2f, y = r.Y + 1;
        var body = new RectangleF(x, y, w, h);
        using (var path = Theme.Round(body, w / 2f))
        using (var fill = new SolidBrush(Color.FromArgb(0xEF, 0xEF, 0xEF)))
        using (var pen = new Pen(Color.FromArgb(0x55, 0x55, 0x55), 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            g.DrawLine(pen, x + w / 2f, y + 1, x + w / 2f, y + h * 0.36f);
            g.DrawLine(pen, x + 1, y + h * 0.36f, x + w - 1, y + h * 0.36f);
        }
        using var wheel = new SolidBrush(Color.FromArgb(0x55, 0x55, 0x55));
        g.FillRectangle(wheel, x + w / 2f - 1, y + h * 0.1f, 2, h * 0.16f);
    }

    public static void Gear(Graphics g, Rectangle r)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        float R = Math.Min(r.Width, r.Height) / 2f - 1;
        using (var teeth = new Pen(Theme.Grey, R * 0.32f))
        {
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                g.DrawLine(teeth, cx + c * R * 0.6f, cy + s * R * 0.6f, cx + c * R * 0.95f, cy + s * R * 0.95f);
            }
        }
        using var body = new SolidBrush(Theme.Grey);
        g.FillEllipse(body, cx - R * 0.72f, cy - R * 0.72f, R * 1.44f, R * 1.44f);
        g.FillEllipse(Brushes.White, cx - R * 0.3f, cy - R * 0.3f, R * 0.6f, R * 0.6f);
    }

    public static void Monitor(Graphics g, Rectangle r)
    {
        var scr = new Rectangle(r.X + 1, r.Y + 2, r.Width - 3, r.Height - 8);
        using (var fill = new SolidBrush(Color.FromArgb(0x3A, 0x7B, 0xD5)))
        using (var pen = new Pen(Color.FromArgb(0x30, 0x30, 0x30), 1.6f))
        {
            g.FillRectangle(fill, scr);
            g.DrawRectangle(pen, scr);
            int cx = r.X + r.Width / 2;
            g.DrawLine(pen, cx, scr.Bottom, cx, scr.Bottom + 3);
            g.DrawLine(pen, cx - 4, scr.Bottom + 3, cx + 4, scr.Bottom + 3);
        }
    }

    public static void Info(Graphics g, Rectangle r)
    {
        var c = new Rectangle(r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        using var pen = new Pen(Color.FromArgb(0x2D, 0x6C, 0xC0), 1.6f);
        g.DrawEllipse(pen, c);
        using var f = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var b = new SolidBrush(Color.FromArgb(0x2D, 0x6C, 0xC0));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("i", f, b, new RectangleF(c.X, c.Y, c.Width, c.Height), sf);
    }
}

internal enum TabIcon { Gear, Monitor, Info }

/// <summary>Flat tab header: icon + caption, selected tab gets a bordered white look.</summary>
internal sealed class TabButton : Control
{
    private bool _selected;
    private bool _hover;

    public TabIcon Icon { get; set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public TabButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Height = 34;
        BackColor = Color.White;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(_selected ? Color.White : (_hover ? Color.FromArgb(0xF4, 0xF8, 0xFD) : Color.White));

        using (var pen = new Pen(Theme.Line))
        {
            if (_selected)
            {
                g.DrawLine(pen, 0, 0, Width - 1, 0);
                g.DrawLine(pen, 0, 0, 0, Height - 1);
                g.DrawLine(pen, Width - 1, 0, Width - 1, Height - 1);
            }
            else
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }

        var ic = new Rectangle(10, (Height - 20) / 2, 20, 20);
        switch (Icon)
        {
            case TabIcon.Gear: Icons.Gear(g, ic); break;
            case TabIcon.Monitor: Icons.Monitor(g, ic); break;
            default: Icons.Info(g, ic); break;
        }

        using var b = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(Text, Font, b, new RectangleF(36, 0, Width - 38, Height), sf);
    }
}

/// <summary>Round status lamp (green / orange / red).</summary>
internal sealed class LedBox : Control
{
    private Color _color = Theme.Amber;

    public Color State
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    public LedBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(22, 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Math.Min(Width, Height) - 8;
        if (d < 4) return;
        var rect = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
        using (var b = new SolidBrush(_color)) g.FillEllipse(b, rect);
        using (var pen = new Pen(ControlPaint.Dark(_color, 0.25f))) g.DrawEllipse(pen, rect);
        using (var hi = new SolidBrush(Color.FromArgb(120, Color.White)))
            g.FillEllipse(hi, rect.X + d * 0.22f, rect.Y + d * 0.14f, d * 0.4f, d * 0.3f);
    }
}

/// <summary>Draggable icon for one physical keyboard or mouse.</summary>
internal sealed class DeviceTile : Control
{
    public const string DragFormat = "DuoSeat.Device";

    private Point _down;
    private bool _highlight;
    private DateTime _highlightUntil = DateTime.MinValue;

    public string DevicePath { get; }
    public bool IsKeyboard { get; }

    /// <summary>0 = System (shared), 1 / 2 = workplace number.</summary>
    public int Station { get; set; }

    public DeviceTile(string path, bool isKeyboard)
    {
        DevicePath = path;
        IsKeyboard = isKeyboard;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 36);
        Margin = new Padding(3);
        Cursor = Cursors.Hand;
        BackColor = Color.White;
    }

    /// <summary>Orange frame for ~0.7 s; used to show which physical device just produced input.</summary>
    public void Flash()
    {
        _highlightUntil = DateTime.UtcNow.AddMilliseconds(700);
        if (!_highlight) { _highlight = true; Invalidate(); }
    }

    public void Tick()
    {
        if (_highlight && DateTime.UtcNow >= _highlightUntil)
        {
            _highlight = false;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _down = e.Location;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left) return;
        var drag = SystemInformation.DragSize;
        if (Math.Abs(e.X - _down.X) < drag.Width && Math.Abs(e.Y - _down.Y) < drag.Height) return;
        DoDragDrop(new DataObject(DragFormat, DevicePath), DragDropEffects.Move);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Theme.Round(rect, 4))
        {
            using (var fill = new SolidBrush(Color.FromArgb(0xFA, 0xFA, 0xFA))) g.FillPath(fill, path);
            using var pen = _highlight ? new Pen(Theme.Orange, 2.2f) : new Pen(Theme.LineSoft, 1.2f);
            g.DrawPath(pen, path);
        }
        var icon = new Rectangle(6, 4, Width - 12, Height - 8);
        if (IsKeyboard) Icons.Keyboard(g, icon); else Icons.Mouse(g, icon);
    }
}

/// <summary>The blue "monitor" picture of a workplace. Click to choose the display.</summary>
internal sealed class MonitorTile : Control
{
    private bool _hover;
    private string _caption = "";

    public string Caption
    {
        get => _caption;
        set { _caption = value; Invalidate(); }
    }

    public MonitorTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(150, 100);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var outer = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Theme.Round(outer, 5))
        {
            using (var bg = new SolidBrush(_hover ? Color.FromArgb(0xE3, 0xEE, 0xFA) : Color.FromArgb(0xF1, 0xF6, 0xFC)))
                g.FillPath(bg, path);
            using var pen = new Pen(Theme.Line, 1.5f);
            g.DrawPath(pen, path);
        }

        int sx = 12, sy = 9, sw = Width - 24, sh = (int)(Height * 0.60);
        var bezel = new RectangleF(sx, sy, sw, sh);
        using (var path = Theme.Round(bezel, 3))
        using (var dark = new SolidBrush(Theme.Ink))
            g.FillPath(dark, path);

        var inner = new Rectangle(sx + 3, sy + 3, sw - 6, sh - 6);
        using (var grad = new LinearGradientBrush(inner, Color.FromArgb(0x35, 0x86, 0xE6), Color.FromArgb(0x0B, 0x4E, 0xA8), 90f))
            g.FillRectangle(grad, inner);

        // brand plate ("DuoSeat" - our own wordmark)
        using var logoFont = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
        using var tf = StringFormat.GenericTypographic;
        float w1 = g.MeasureString("Duo", logoFont, 1000, tf).Width;
        float w2 = g.MeasureString("Seat", logoFont, 1000, tf).Width;
        float total = w1 + w2;
        float plateW = total + 18, plateH = 26;
        var plate = new RectangleF(inner.X + (inner.Width - plateW) / 2f, inner.Y + (inner.Height - plateH) / 2f - 4, plateW, plateH);
        using (var path = Theme.Round(plate, 4))
        using (var white = new SolidBrush(Color.FromArgb(235, Color.White)))
            g.FillPath(white, path);
        float ty = plate.Y + (plate.Height - logoFont.Height) / 2f + 1;
        using (var ink = new SolidBrush(Theme.Ink)) g.DrawString("Duo", logoFont, ink, plate.X + 9, ty, tf);
        using (var org = new SolidBrush(Color.FromArgb(0xE0, 0x7B, 0x00))) g.DrawString("Seat", logoFont, org, plate.X + 9 + w1, ty, tf);

        if (_caption.Length > 0)
        {
            using var cf = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
            g.DrawString(_caption, cf, Brushes.White, new RectangleF(inner.X, inner.Y, inner.Width - 3, inner.Height - 1), sf);
        }

        int cx = Width / 2;
        using var stand = new SolidBrush(Theme.Ink);
        g.FillRectangle(stand, cx - 6, sy + sh, 12, 8);
        using var basePath = Theme.Round(new RectangleF(cx - 24, sy + sh + 7, 48, 6), 2);
        g.FillPath(stand, basePath);
    }
}

/// <summary>One workplace column: lamp + number + menu on top, monitor picture, then its devices.</summary>
internal sealed class WorkplaceCell : Panel
{
    public int Station { get; }
    public LedBox Led { get; } = new();
    public Label Number { get; }
    public Button MenuButton { get; }
    public MonitorTile Monitor { get; } = new();
    public FlowLayoutPanel Devices { get; }
    public Panel Header { get; }
    public Panel MonitorHolder { get; }

    public WorkplaceCell(int station)
    {
        Station = station;
        BackColor = Color.White;
        Padding = new Padding(1);

        Devices = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.White,
        };

        MonitorHolder = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = Color.White };
        MonitorHolder.Controls.Add(Monitor);
        MonitorHolder.Resize += (_, _) => CenterMonitor();
        Monitor.Top = 6;

        Number = new Label
        {
            Dock = DockStyle.Fill,
            Text = station.ToString(),
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        MenuButton = new Button
        {
            Dock = DockStyle.Right,
            Width = 28,
            Text = "≡",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0xF7, 0xF7, 0xF7),
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        MenuButton.FlatAppearance.BorderColor = Color.FromArgb(0x9A, 0x9A, 0x9A);
        Led.Dock = DockStyle.Left;
        Led.Width = 26;

        Header = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(4, 3, 4, 1), BackColor = Color.White };
        // Fill first, then edges (WinForms docks the last-added control first).
        Header.Controls.Add(Number);
        Header.Controls.Add(MenuButton);
        Header.Controls.Add(Led);

        Controls.Add(Devices);
        Controls.Add(MonitorHolder);
        Controls.Add(Header);
    }

    private void CenterMonitor() => Monitor.Left = Math.Max(0, (MonitorHolder.Width - Monitor.Width) / 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
