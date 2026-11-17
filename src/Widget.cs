// Taskbar widget: a child window of Shell_TrayWnd, right next to the notification area.
// The tray only gives 16x16 px per icon; here the device icon and its percentage get the size of the clock.
// Layered window with per-pixel alpha (UpdateLayeredWindow); a layered child needs the Win8+ manifest.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

class Item { public string Key, Name; public int Percent; public bool Charging, Stale, Headset; public DateTime Updated; }

class Widget : Form
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr c, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pDst, ref SZ sz, IntPtr src, ref PT pSrc, int key, ref BLEND b, int f);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    readonly Timer dockTimer = new Timer { Interval = 1000 };
    readonly ToolTip tip = new ToolTip { InitialDelay = 400, ShowAlways = true };
    List<Item> items = new List<Item>();
    IntPtr taskbar;
    bool hover, lastLight;
    Rectangle bounds;
    public bool Docked { get { return taskbar != IntPtr.Zero && IsWindow(taskbar) && GetParent(Handle) == taskbar; } }
    public event Action RefreshRequested;

    public Widget(ContextMenuStrip menu)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-200, -200, 10, 10);
        MouseEnter += delegate { hover = true; Render(); };
        MouseLeave += delegate { hover = false; Render(); };
        MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
            else if (e.Button == MouseButtons.Left && RefreshRequested != null) RefreshRequested();
        };
        dockTimer.Tick += delegate { DockToTaskbar(); };
        dockTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    public void ShowItems(List<Item> list) { items = list; DockToTaskbar(); Render(); }

    public static string Describe(Item it)
    {
        string s = it.Name + ": " + (it.Percent < 0 ? "?" : it.Percent + "%");
        if (it.Charging) return s + " (charging)";
        if (!it.Stale) return s;
        var age = DateTime.Now - it.Updated;
        string ago = it.Updated == DateTime.MinValue ? "" : age.TotalMinutes < 1 ? " just now" : age.TotalHours < 1 ? " " + (int)age.TotalMinutes + " min ago" : " " + (int)age.TotalHours + " h ago";
        return s + " (asleep, last reading" + ago + ")";
    }

    // Attaches the widget to the taskbar (again, if Explorer restarted) and places it left of the tray.
    public bool DockToTaskbar()
    {
        IntPtr t = FindWindow("Shell_TrayWnd", null);
        IntPtr notify = t == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(t, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) { taskbar = IntPtr.Zero; return false; }
        if (GetParent(Handle) != t)
        {
            taskbar = t;
            SetParent(Handle, t);
            SetWindowLong(Handle, -16, 0x40000000 | 0x10000000 | 0x04000000); // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
            bounds = Rectangle.Empty;
        }
        RECT rt, rn; GetWindowRect(t, out rt); GetWindowRect(notify, out rn);
        if (rt.B - rt.T > rt.R - rt.L) return true; // shortcut: vertical taskbar not supported; the widget stays put
        var size = Measure(rt.B - rt.T);
        int x = rn.L - rt.L - size.Width - Px(6);
        // Windows 10: task buttons reach the tray; shrink the task list to make room.
        IntPtr rebar = FindWindowEx(t, IntPtr.Zero, "ReBarWindow32", null);
        RECT rr;
        if (rebar != IntPtr.Zero && GetWindowRect(rebar, out rr) && rr.R - rt.L > x && rr.R > rr.L)
            MoveWindow(rebar, rr.L - rt.L, rr.T - rt.T, Math.Max(0, x - (rr.L - rt.L)), rr.B - rr.T, true);
        var target = new Rectangle(x, 0, size.Width, size.Height);
        bool light = LightTheme();
        if (target != bounds || light != lastLight)
        {
            bounds = target; lastLight = light;
            SetWindowPos(Handle, IntPtr.Zero, x, 0, size.Width, size.Height, 0x0040 | 0x0010); // SHOWWINDOW | NOACTIVATE, on top
            Render();
        }
        return true;
    }

    float scale = 1;
    int Px(float v) { return (int)Math.Round(v * scale); }

    static bool LightTheme()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            return k != null && (k.GetValue("SystemUsesLightTheme") as int? ?? 0) == 1;
    }

    // ---- layout ---------------------------------------------------------------------------------
    const float IconW = 16, IconH = 16, Gap = 5, EdgePad = 8, ColGap = 12;
    Font numFont, pctFont, iconFont;

    void Fonts()
    {
        if (numFont != null && Math.Abs(numFont.Size - 13 * scale) < 0.01) return;
        if (numFont != null) { numFont.Dispose(); pctFont.Dispose(); iconFont.Dispose(); }
        numFont = new Font("Segoe UI Variable Display", 13 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        if (numFont.Name != "Segoe UI Variable Display") { numFont.Dispose(); numFont = new Font("Segoe UI", 13 * scale, FontStyle.Bold, GraphicsUnit.Pixel); }
        pctFont = new Font(numFont.FontFamily, 10 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        // Native Windows icons: Segoe Fluent Icons (Win11) or Segoe MDL2 Assets (Win10), same code points.
        iconFont = new Font("Segoe Fluent Icons", IconH * scale, GraphicsUnit.Pixel);
        if (iconFont.Name != "Segoe Fluent Icons") { iconFont.Dispose(); iconFont = new Font("Segoe MDL2 Assets", IconH * scale, GraphicsUnit.Pixel); }
    }

    int Rows(int height) { return height >= 40 * scale && items.Count > 1 ? 2 : 1; }

    float ItemWidth(Graphics g, Item it)
    {
        string n = it.Percent < 0 ? "-" : it.Percent.ToString();
        var tf = StringFormat.GenericTypographic;
        float w = (IconW + Gap) * scale + g.MeasureString(n, numFont, 200, tf).Width + 1.5f * scale + g.MeasureString("%", pctFont, 200, tf).Width;
        if (it.Charging) w += 9 * scale;
        return w;
    }

    Size Measure(int height)
    {
        scale = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) / 96f : 1;
        if (scale <= 0) scale = 1;
        Fonts();
        if (items.Count == 0) return new Size(1, height);
        int rows = Rows(height);
        float total = 2 * EdgePad * scale;
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
            for (int i = 0; i < items.Count; i += rows)
            {
                float col = 0;
                for (int j = i; j < Math.Min(i + rows, items.Count); j++) col = Math.Max(col, ItemWidth(g, items[j]));
                total += col + (i > 0 ? ColGap * scale : 0);
            }
        return new Size((int)Math.Ceiling(total), height);
    }

    // ---- drawing --------------------------------------------------------------------------------
    void Render()
    {
        if (!IsHandleCreated || bounds.Width <= 1) return;
        bool light = LightTheme();
        Color fg = light ? Color.FromArgb(26, 26, 26) : Color.White;
        Color low = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 107);
        Color bolt = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        int W = bounds.Width, H = bounds.Height;
        using (var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType does not work on a transparent background
            // alpha 1 everywhere: invisible, but still receives clicks (alpha 0 lets clicks through)
            using (var bg = new SolidBrush(hover ? Color.FromArgb(light ? 14 : 22, fg) : Color.FromArgb(1, 0, 0, 0)))
            using (var p = Rounded(new RectangleF(1, 4 * scale, W - 2, H - 8 * scale), 4 * scale))
                g.FillPath(bg, p);

            int rows = Rows(H);
            float x = EdgePad * scale;
            var tf = StringFormat.GenericTypographic;
            for (int i = 0; i < items.Count; i += rows)
            {
                float col = 0;
                for (int j = i; j < Math.Min(i + rows, items.Count); j++) col = Math.Max(col, ItemWidth(g, items[j]));
                for (int j = i; j < Math.Min(i + rows, items.Count); j++)
                {
                    var it = items[j];
                    float cy = rows == 1 ? H / 2f : (j - i == 0 ? H * 0.31f : H * 0.69f);
                    int alpha = it.Stale ? 120 : 255;
                    bool isLow = it.Percent >= 0 && it.Percent <= 15 && !it.Charging;
                    Color txt = Color.FromArgb(alpha, isLow ? low : fg);
                    DrawIcon(g, it.Headset, x, cy - IconH * scale / 2, Color.FromArgb(alpha, fg));

                    float tx = x + (IconW + Gap) * scale;
                    string n = it.Percent < 0 ? "-" : it.Percent.ToString();
                    var sn = g.MeasureString(n, numFont, 200, tf);
                    float top = cy - sn.Height / 2 + 0.5f * scale;
                    using (var b = new SolidBrush(txt)) g.DrawString(n, numFont, b, tx, top, tf);
                    var sp = g.MeasureString("%", pctFont, 200, tf);
                    float px = tx + sn.Width + 1.5f * scale;
                    // smaller "%" sitting on the number's baseline
                    using (var b = new SolidBrush(Color.FromArgb(alpha * 170 / 255, txt))) g.DrawString("%", pctFont, b, px, top + sn.Height - sp.Height - 0.5f * scale, tf);
                    if (it.Charging) Bolt(g, px + sp.Width + 2.5f * scale, cy, Color.FromArgb(alpha, bolt));
                }
                x += col + ColGap * scale;
            }
            Apply(bmp);
        }
        var sb = new System.Text.StringBuilder();
        foreach (var it in items) sb.AppendLine(Describe(it));
        string t = sb.ToString().TrimEnd();
        if (tip.GetToolTip(this) != t) tip.SetToolTip(this, t);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // E962 = Mouse, E7F6 = Headphone.
    void DrawIcon(Graphics g, bool headset, float x, float y, Color c)
    {
        string glyph = ((char)(headset ? 0xE7F6 : 0xE962)).ToString();
        float w = g.MeasureString(glyph, iconFont, 200, StringFormat.GenericTypographic).Width;
        using (var b = new SolidBrush(c))
            g.DrawString(glyph, iconFont, b, x + (IconW * scale - w) / 2, y, StringFormat.GenericTypographic);
    }

    void Bolt(Graphics g, float x, float cy, Color c)
    {
        float s = scale;
        PointF[] p = {
            new PointF(x + 4.2f * s, cy - 5.5f * s), new PointF(x + 0.5f * s, cy + 0.8f * s), new PointF(x + 3f * s, cy + 0.8f * s),
            new PointF(x + 2f * s, cy + 5.5f * s), new PointF(x + 6f * s, cy - 1f * s), new PointF(x + 3.4f * s, cy - 1f * s) };
        using (var b = new SolidBrush(c)) g.FillPolygon(b, p);
    }

    void Apply(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var sz = new SZ { W = bmp.Width, H = bmp.Height }; var src = new PT();
        var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref sz, mem, ref src, 0, ref bl, 2); // ULW_ALPHA
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { dockTimer.Dispose(); tip.Dispose(); if (numFont != null) { numFont.Dispose(); pctFont.Dispose(); iconFont.Dispose(); } }
        base.Dispose(disposing);
    }
}
