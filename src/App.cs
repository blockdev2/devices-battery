// Devices Battery - shows the battery of wireless mice and headsets in the Windows taskbar.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Devices Battery")]
[assembly: AssemblyProduct("Devices Battery")]
[assembly: AssemblyDescription("Battery level of wireless mice and headsets in the Windows taskbar")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Scanner
{
    public static List<Reading> ReadAll()
    {
        var ctx = new PollContext { Colls = Hid.Enumerate() };
        try { Logitech.Poll(ctx); } catch (Exception) { }
        Vendors.PollAll(ctx);
        try { Generic.StandardHid(ctx); } catch (Exception) { }
        try { Generic.Bluetooth(ctx); } catch (Exception) { }
        return ctx.Output;
    }
}

class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "DevicesBattery";

    // Every device seen since the app started. A device that goes to sleep (or stops answering) stays
    // listed with its last reading, dimmed, and picks up a fresh value on a later scan once it wakes.
    readonly Dictionary<string, Item> known = new Dictionary<string, Item>();
    readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
    readonly HashSet<string> warned = new HashSet<string>();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60000 };
    readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 30000 };
    readonly SynchronizationContext ui;
    readonly Widget widget;
    readonly List<ToolStripMenuItem> installItems = new List<ToolStripMenuItem>();
    NotifyIcon empty, notice;
    Updater.Release pending;
    int scanning;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        widget = new Widget(BuildMenu());
        widget.RefreshRequested += Refresh;
        widget.Show();
        timer.Tick += delegate { Refresh(); };
        timer.Start();
        // first update check 30 s after start, then once a day
        updateTimer.Tick += delegate { updateTimer.Interval = 24 * 3600 * 1000; CheckForUpdates(false); };
        updateTimer.Start();
        SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) Refresh(); };
        Refresh();
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Refresh now", null, delegate { Refresh(); });
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
        auto.Click += delegate { ToggleStartup(); auto.Checked = StartsWithWindows(); };
        m.Items.Add(auto);
        m.Items.Add("Check for updates", null, delegate { CheckForUpdates(true); });
        var install = new ToolStripMenuItem { Visible = false };
        install.Click += delegate { InstallUpdate(); };
        installItems.Add(install);
        m.Items.Add(install);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Devices Battery " + Updater.CurrentText) { Enabled = false });
        m.Items.Add("Exit", null, delegate { Exit(); });
        return m;
    }

    static bool StartsWithWindows()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
    }
    static void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
    }

    // ---- scanning --------------------------------------------------------------------------------
    void Refresh()
    {
        if (Interlocked.Exchange(ref scanning, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<Reading> l;
            try { l = Scanner.ReadAll(); } catch (Exception) { l = new List<Reading>(); }
            ui.Post(delegate { Show(l); scanning = 0; }, null);
        });
    }

    void Show(List<Reading> readings)
    {
        var fresh = new HashSet<string>();
        foreach (var r in readings)
        {
            Item it;
            known.TryGetValue(r.Key, out it);
            bool hasValue = r.Connected && r.Percent >= 0;
            if (it == null)
            {
                if (!hasValue && !(r.Connected && r.Charging)) continue; // nothing to show yet
                it = known[r.Key] = new Item { Key = r.Key, Percent = -1 };
            }
            it.Name = r.Name; it.Headset = r.Headset;
            if (hasValue) { it.Percent = r.Percent; it.Updated = DateTime.Now; }
            it.Charging = r.Connected && r.Charging;
            it.Stale = !hasValue;
            fresh.Add(r.Key);
        }
        foreach (var it in known.Values)
            if (!fresh.Contains(it.Key)) { it.Stale = true; it.Charging = false; } // asleep / out of range: keep the last value

        var items = new List<Item>(known.Values);
        foreach (var it in items)
        {
            if (!it.Stale && it.Percent >= 0 && it.Percent <= 15 && !it.Charging) { if (warned.Add(it.Key)) Notify("Low battery", it.Name + " is at " + it.Percent + "%.", null); }
            else if (it.Percent > 20) warned.Remove(it.Key);
        }
        // mice first, then headsets: a stable order in the taskbar
        items.Sort((a, b) => a.Headset != b.Headset ? a.Headset.CompareTo(b.Headset) : string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        widget.ShowItems(items);
        ShowTrayIcons(widget.Docked ? new List<Item>() : items); // tray icons only when the widget cannot dock
    }

    // Windows notification through a temporary tray icon.
    void Notify(string title, string text, Action onClick)
    {
        if (notice == null)
        {
            notice = new NotifyIcon { Icon = SystemIcons.Information, Text = "Devices Battery" };
            notice.BalloonTipClicked += delegate { var a = notice.Tag as Action; if (a != null) a(); };
        }
        notice.Tag = onClick;
        notice.Visible = true;
        notice.ShowBalloonTip(8000, title, text, onClick != null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        var t = new System.Windows.Forms.Timer { Interval = 15000 };
        t.Tick += delegate { t.Dispose(); if (notice != null) notice.Visible = false; };
        t.Start();
    }

    // ---- updates ---------------------------------------------------------------------------------
    void CheckForUpdates(bool manual)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            Updater.Release r = null; Exception err = null;
            try { r = Updater.Latest(); } catch (Exception e) { err = e; }
            ui.Post(delegate
            {
                if (Updater.IsNewer(r))
                {
                    pending = r;
                    foreach (var mi in installItems) { mi.Text = "Install update " + r.Tag; mi.Visible = true; }
                    if (manual) InstallUpdate();
                    else Notify("Update available", "Devices Battery " + r.Tag + " is available. Click here to install it.", InstallUpdate);
                }
                else if (manual)
                    MessageBox.Show(err != null ? "Could not check for updates:\n" + err.Message : "You have the latest version (" + Updater.CurrentText + ").",
                                    "Devices Battery", MessageBoxButtons.OK, err != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }, null);
        });
    }

    void InstallUpdate()
    {
        var r = pending;
        if (r == null) return;
        if (MessageBox.Show("Install Devices Battery " + r.Tag + " now? The app will restart.", "Devices Battery",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Exception err = null;
            try { Updater.Install(r); } catch (Exception e) { err = e; }
            ui.Post(delegate
            {
                if (err == null) Exit();
                else MessageBox.Show("The update failed:\n" + err.Message, "Devices Battery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }, null);
        });
    }

    // ---- tray fallback ---------------------------------------------------------------------------
    void ShowTrayIcons(List<Item> items)
    {
        var current = new HashSet<string>();
        foreach (var it in items)
        {
            current.Add(it.Key);
            NotifyIcon ni;
            if (!icons.TryGetValue(it.Key, out ni))
                icons[it.Key] = ni = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = true };
            SwapIcon(ni, DrawIcon(it.Percent, it.Charging, it.Stale));
            string t = Widget.Describe(it);
            ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }
        foreach (var k in new List<string>(icons.Keys))
            if (!current.Contains(k)) { icons[k].Visible = false; icons[k].Dispose(); icons.Remove(k); }

        if (widget.Docked || icons.Count > 0) { if (empty != null) { empty.Visible = false; empty.Dispose(); empty = null; } }
        else if (empty == null)
        {
            empty = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = true, Text = "No wireless mouse or headset found" };
            SwapIcon(empty, DrawIcon(-1, false, true));
        }
    }

    static void SwapIcon(NotifyIcon ni, Icon icon)
    {
        var old = ni.Icon; ni.Icon = icon;
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    // Fallback tray icon: just the number. Green > 40, amber 16-40, red <= 15, blue charging, grey = last reading.
    static Icon DrawIcon(int pct, bool charging, bool stale)
    {
        Color c = charging ? Color.FromArgb(70, 160, 255) : pct < 0 || stale ? Color.Gray
                : pct <= 15 ? Color.FromArgb(240, 70, 60) : pct <= 40 ? Color.FromArgb(245, 180, 40) : Color.FromArgb(80, 210, 110);
        string txt = pct < 0 ? "-" : pct.ToString();
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        using (var f = new Font("Segoe UI", txt.Length >= 3 ? 15f : 21f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(c))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(txt, f, b, new RectangleF(-2, 0, 36, 32), fmt);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void Exit()
    {
        foreach (var ni in icons.Values) { ni.Visible = false; ni.Dispose(); }
        if (empty != null) { empty.Visible = false; empty.Dispose(); }
        if (notice != null) { notice.Visible = false; notice.Dispose(); }
        widget.Close();
        ExitThread();
    }
}

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            AttachConsole(-1); // GUI app: write to the caller's console
            SelfTest.Run();
            foreach (var r in Scanner.ReadAll())
                Console.WriteLine(r.Name + " = " + (r.Percent < 0 ? "?" : r.Percent + "%") + (r.Charging ? " charging" : "")
                                  + (r.Asleep ? " asleep" : "") + (r.Connected ? "" : " off") + "  [" + r.Key + "]");
            return;
        }
        if (args.Length > 0 && args[0] == "--update")
        {
            AttachConsole(-1);
            var r = Updater.Latest();
            Console.WriteLine("current " + Updater.CurrentText + ", latest " + (r == null ? "none" : r.Tag + " sha256=" + r.Sha256));
            if (Updater.IsNewer(r)) { Updater.Install(r); Console.WriteLine("installed " + r.Tag); }
            return;
        }
        Updater.Cleanup();
        bool updated = Array.IndexOf(args, "--updated") >= 0;
        using (var mutex = new Mutex(false, "Local\\DevicesBattery"))
        {
            // after an update the old process may still be exiting: wait for it
            bool owned;
            try { owned = mutex.WaitOne(updated ? 15000 : 0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return; // already running
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
            mutex.ReleaseMutex();
        }
    }
}

// Self-test of the pure parts (no hardware needed); runs with --test.
static class SelfTest
{
    static void Ok(bool c, string what) { if (!c) throw new Exception("self-test failed: " + what); }
    public static void Run()
    {
        int[] v = { 4200, 4000, 3500 }, p = { 100, 50, 0 };
        Ok(Hid.Interpolate(4300, v, p) == 100, "interpolate above");
        Ok(Hid.Interpolate(4100, v, p) == 75, "interpolate middle");
        Ok(Hid.Interpolate(3000, v, p) == 0, "interpolate below");
        Ok(Hid.Map(2, 0, 4) == 50, "map");
        Ok(Hid.Clamp100(150) == 100 && Hid.Clamp100(-3) == 0, "clamp");
        Ok(Updater.IsNewer(new Updater.Release { Version = new Version(99, 0, 0) }), "newer version");
        Ok(!Updater.IsNewer(new Updater.Release { Version = Updater.Current }), "same version");
    }
}
