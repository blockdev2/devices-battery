// Win32 HID layer shared by every driver.
// Buffers follow hidapi conventions so protocols documented by other projects map 1:1:
//  * Write/SetFeature: data[0] is the report ID (0 when the device does not number its reports);
//    the buffer is zero-padded to the report length.
//  * Read: a leading 0 report ID is stripped, so data[0] is the first payload byte for
//    unnumbered reports and the report ID for numbered ones.
//  * GetFeature/GetInputReport: out[0] is the report ID.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

class Reading
{
    public string Key, Name;
    public int Percent = -1;       // -1 = no value
    public bool Charging;
    public bool Connected = true;  // false = receiver present but device off / out of range
    public bool Asleep;            // answered without a value: the UI keeps the last one
    public bool Headset;           // drawn as headphones (otherwise as a mouse)
}

class HidColl
{
    public string Path;
    public ushort Vid, Pid, Page, Usage, InLen, OutLen, FeatLen, InVals, FeatVals;
    public int Iface = -1;
    string product;
    public string Product()
    {
        if (product != null) return product;
        product = "";
        using (var h = Native.Open(Path, 0))
        {
            var b = new byte[256];
            if (!h.IsInvalid && Native.HidD_GetProductString(h, b, b.Length))
                product = Encoding.Unicode.GetString(b).Split('\0')[0].Trim();
        }
        return product;
    }
    public bool Has(string tag) { return Path.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0; }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] r;
        public ushort LinkNodes, InBtn, InVal, InIdx, OutBtn, OutVal, OutIdx, FeatBtn, FeatVal, FeatIdx;
    }
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint val, IntPtr pp, byte[] rep, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr buf, int size, out int req, IntPtr di);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, IntPtr b, int n, IntPtr read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, IntPtr b, int n, IntPtr wr, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int n, bool wait);
    [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll")] public static extern IntPtr CreateEvent(IntPtr a, bool manual, bool init, string n);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    public const uint READ = 0x80000000, WRITE = 0x40000000;
    public static SafeFileHandle Open(string path, uint access, bool overlapped = false)
    {
        return CreateFile(path, access, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero);
    }
}

static class Hid
{
    public static List<HidColl> Enumerate()
    {
        var res = new List<HidColl>();
        Guid g; Native.HidD_GetHidGuid(out g);
        IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        try
        {
            var d = new Native.SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
            {
                int req; Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                string path = null;
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                }
                finally { Marshal.FreeHGlobal(buf); }
                var c = path == null ? null : Describe(path);
                if (c != null) res.Add(c);
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return res;
    }

    static HidColl Describe(string path)
    {
        var m = Regex.Match(path, @"vid_([0-9a-f]{4}).*?pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        if (!m.Success) return null; // e.g. Bluetooth LE paths have another shape
        var c = new HidColl { Path = path, Vid = Convert.ToUInt16(m.Groups[1].Value, 16), Pid = Convert.ToUInt16(m.Groups[2].Value, 16) };
        var mi = Regex.Match(path, @"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
        if (mi.Success) c.Iface = Convert.ToInt32(mi.Groups[1].Value, 16);
        using (var h = Native.Open(path, 0)) // zero access is enough to read capabilities, even of mice/keyboards
        {
            IntPtr pp;
            if (h.IsInvalid || !Native.HidD_GetPreparsedData(h, out pp)) return null;
            Native.HIDP_CAPS caps;
            Native.HidP_GetCaps(pp, out caps);
            Native.HidD_FreePreparsedData(pp);
            c.Page = caps.UsagePage; c.Usage = caps.Usage;
            c.InLen = caps.InputLen; c.OutLen = caps.OutputLen; c.FeatLen = caps.FeatureLen;
            c.InVals = caps.InVal; c.FeatVals = caps.FeatVal;
        }
        return c;
    }

    public static int Clamp100(int v) { return v < 0 ? 0 : v > 100 ? 100 : v; }
    public static int Map(int v, int lo, int hi) { return hi <= lo ? 0 : Clamp100((v - lo) * 100 / (hi - lo)); }
    // Piecewise-linear interpolation through (mv[i], pct[i]); tables sorted by descending voltage.
    public static int Interpolate(int mv, int[] v, int[] p)
    {
        if (mv >= v[0]) return p[0];
        for (int i = 1; i < v.Length; i++)
            if (mv >= v[i]) return p[i] + (p[i - 1] - p[i]) * (mv - v[i]) / (v[i - 1] - v[i]);
        return p[p.Length - 1];
    }
    public static string Hex4(int v) { return v.ToString("X4"); }
}

// An open HID handle. Reads and writes are overlapped with a timeout, so a silent device never hangs us.
class HidDevice : IDisposable
{
    readonly SafeFileHandle h;
    readonly bool overlapped;
    public readonly HidColl Coll;

    HidDevice(HidColl c, uint access, bool ov) { Coll = c; overlapped = ov; h = Native.Open(c.Path, access, ov); }
    public bool Ok { get { return !h.IsInvalid; } }
    public void Dispose() { h.Dispose(); }

    static HidDevice OrNull(HidDevice d) { if (d.Ok) return d; d.Dispose(); return null; }
    public static HidDevice Open(HidColl c) { return OrNull(new HidDevice(c, Native.READ | Native.WRITE, true)); }
    public static HidDevice OpenRead(HidColl c) { return OrNull(new HidDevice(c, Native.READ, true)); }
    public static HidDevice OpenFeature(HidColl c) { return OrNull(new HidDevice(c, 0, false)); } // works on mice/keyboards

    // Runs an overlapped ReadFile/WriteFile; returns bytes transferred, or -1 on timeout/error.
    int Io(bool write, byte[] data, int ms)
    {
        IntPtr buf = Marshal.AllocHGlobal(data.Length), ov = Marshal.AllocHGlobal(32);
        IntPtr ev = Native.CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            if (write) Marshal.Copy(data, 0, buf, data.Length);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(ov, i, 0);
            Marshal.WriteIntPtr(ov, IntPtr.Size == 8 ? 24 : 16, ev); // OVERLAPPED.hEvent
            bool ok = write ? Native.WriteFile(h, buf, data.Length, IntPtr.Zero, ov) : Native.ReadFile(h, buf, data.Length, IntPtr.Zero, ov);
            if (!ok && Marshal.GetLastWin32Error() != 997) return -1; // 997 = ERROR_IO_PENDING
            int n;
            if (Native.WaitForSingleObject(ev, (uint)Math.Max(1, ms)) != 0)
            {
                Native.CancelIoEx(h, ov);
                Native.GetOverlappedResult(h, ov, out n, true); // wait for the cancel before freeing the buffers
                return -1;
            }
            if (!Native.GetOverlappedResult(h, ov, out n, false)) return -1;
            if (!write) Marshal.Copy(buf, data, 0, n);
            return n;
        }
        finally { Native.CloseHandle(ev); Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buf); }
    }

    static byte[] Pad(byte[] d, int len) { var b = new byte[Math.Max(len, d.Length)]; Array.Copy(d, b, d.Length); return b; }

    public bool Write(params byte[] d)
    {
        if (!overlapped || Coll.OutLen == 0 || d.Length == 0) return false;
        var b = Pad(d, Coll.OutLen);
        if (b.Length > Coll.OutLen) Array.Resize(ref b, Coll.OutLen);
        return Io(true, b, 500) == b.Length;
    }

    static byte[] StripZeroId(byte[] b, int n)
    {
        int o = n > 0 && b[0] == 0 ? 1 : 0;
        var r = new byte[n - o]; Array.Copy(b, o, r, 0, r.Length); return r;
    }

    // Reads one input report, or null on timeout/error.
    public byte[] Read(int ms)
    {
        if (!overlapped || Coll.InLen == 0) return null;
        var b = new byte[Coll.InLen];
        int n = Io(false, b, ms);
        return n <= 0 ? null : StripZeroId(b, n);
    }

    // Reads reports until `accept` returns true or time runs out.
    public byte[] ReadUntil(int ms, Func<byte[], bool> accept)
    {
        int end = Environment.TickCount + ms;
        for (;;)
        {
            int left = end - Environment.TickCount;
            if (left <= 0) return null;
            var r = Read(left);
            if (r == null) return null;
            if (accept(r)) return r;
        }
    }

    // Writes a request and waits for the accepted reply.
    public byte[] Request(byte[] req, int ms, Func<byte[], bool> accept)
    {
        Flush();
        return Write(req) ? ReadUntil(ms, accept) : null;
    }

    // For collections without an interrupt OUT pipe: arms a read, sends via HidD_SetOutputReport, returns the first reply.
    public byte[] SetOutputAndRead(byte[] d, int ms)
    {
        if (!overlapped || Coll.InLen == 0 || Coll.OutLen == 0) return null;
        byte[] resp = null;
        var t = new Thread(delegate () { resp = Read(ms); });
        t.Start();
        Thread.Sleep(5);
        var b = Pad(d, Coll.OutLen);
        if (!Native.HidD_SetOutputReport(h, b, Coll.OutLen)) { t.Join(); return null; }
        t.Join();
        return resp;
    }

    public void Flush() { if (Ok) Native.HidD_FlushQueue(h); }

    public bool SetFeature(params byte[] d)
    {
        if (Coll.FeatLen == 0) return false;
        var b = Pad(d, Coll.FeatLen);
        return Native.HidD_SetFeature(h, b, Coll.FeatLen);
    }

    public byte[] GetFeature(byte id)
    {
        if (Coll.FeatLen == 0) return null;
        var b = new byte[Coll.FeatLen]; b[0] = id;
        return Native.HidD_GetFeature(h, b, b.Length) ? b : null;
    }

    public byte[] GetInputReport(byte id)
    {
        if (Coll.InLen == 0) return null;
        var b = new byte[Coll.InLen]; b[0] = id;
        return Native.HidD_GetInputReport(h, b, b.Length) ? b : null;
    }
}

// State of one scan: present collections, devices a driver already handled, and the output.
class PollContext
{
    public List<HidColl> Colls;
    public readonly List<Reading> Output = new List<Reading>();
    readonly HashSet<uint> claimed = new HashSet<uint>();

    static uint K(ushort vid, ushort pid) { return ((uint)vid << 16) | pid; }
    public bool IsClaimed(ushort vid, ushort pid) { return claimed.Contains(K(vid, pid)); }
    public void Claim(ushort vid, ushort pid) { claimed.Add(K(vid, pid)); }
    public void Add(Reading r, ushort vid, ushort pid) { Output.Add(r); Claim(vid, pid); }
    public bool HasName(string n) { foreach (var o in Output) if (o.Name == n) return true; return false; }

    // Unclaimed products of a vendor, ordered by PID.
    public List<ushort> Products(ushort vid)
    {
        var s = new SortedSet<ushort>();
        foreach (var c in Colls) if (c.Vid == vid && !IsClaimed(c.Vid, c.Pid)) s.Add(c.Pid);
        return new List<ushort>(s);
    }

    public List<HidColl> Find(ushort vid, ushort pid, Func<HidColl, bool> f)
    {
        var r = new List<HidColl>();
        foreach (var c in Colls) if (c.Vid == vid && c.Pid == pid && f(c)) r.Add(c);
        return r;
    }
}
