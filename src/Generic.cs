// Vendor-neutral readers:
//  * Standard HID: the "Battery Strength" usage (page 0x06 usage 0x20, or 0x85/0x65) many devices expose.
//  * Bluetooth: the same percentage Windows shows under Settings > Bluetooth (battery DEVPKEY).
// They only run for devices no vendor driver handled.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Generic
{
    public static void StandardHid(PollContext ctx)
    {
        var digitizers = new HashSet<uint>();
        foreach (var c in ctx.Colls) if (c.Page == 0x0D) digitizers.Add(((uint)c.Vid << 16) | c.Pid);
        foreach (var c in ctx.Colls)
        {
            if (ctx.IsClaimed(c.Vid, c.Pid)) continue;
            if (c.Page == 0x84 || c.Page == 0x85) continue; // UPS / power supplies
            if (digitizers.Contains(((uint)c.Vid << 16) | c.Pid)) continue; // pens/touch: that is the pen's battery
            if (c.InVals == 0 && c.FeatVals == 0) continue;
            int pct = ReadBatteryUsage(c);
            if (pct < 0) continue;
            string name = c.Product();
            ctx.Add(new Reading { Key = "hid:" + Hid.Hex4(c.Vid) + ":" + Hid.Hex4(c.Pid), Name = name == "" ? "Wireless device" : name, Percent = pct }, c.Vid, c.Pid);
        }
    }

    static int ReadBatteryUsage(HidColl c)
    {
        using (var h = Native.Open(c.Path, 0))
        {
            IntPtr pp;
            if (h.IsInvalid || !Native.HidD_GetPreparsedData(h, out pp)) return -1;
            try
            {
                foreach (int type in new[] { 2, 0 }) // HidP_Feature, HidP_Input
                {
                    ushort n = type == 2 ? c.FeatVals : c.InVals;
                    int len = type == 2 ? c.FeatLen : c.InLen;
                    if (n == 0 || len == 0) continue;
                    var caps = new byte[72 * n]; // HIDP_VALUE_CAPS is 72 bytes
                    if (Native.HidP_GetValueCaps(type, caps, ref n, pp) != 0x00110000) continue; // HIDP_STATUS_SUCCESS
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 72;
                        ushort page = BitConverter.ToUInt16(caps, o), usage = BitConverter.ToUInt16(caps, o + 56);
                        if (!((page == 0x06 && usage == 0x20) || (page == 0x85 && usage == 0x65))) continue;
                        var buf = new byte[len]; buf[0] = caps[o + 2];
                        bool ok = type == 2 ? Native.HidD_GetFeature(h, buf, len) : Native.HidD_GetInputReport(h, buf, len);
                        uint val;
                        if (!ok || Native.HidP_GetUsageValue(type, page, BitConverter.ToUInt16(caps, o + 6), usage, out val, pp, buf, len) != 0x00110000) continue;
                        int min = BitConverter.ToInt32(caps, o + 40), max = BitConverter.ToInt32(caps, o + 44);
                        if (max > min) return Hid.Clamp100((int)(((long)val - min) * 100 / (max - min)));
                        if (val <= 100) return (int)val;
                    }
                }
            }
            finally { Native.HidD_FreePreparsedData(pp); }
        }
        return -1;
    }

    // ---- Bluetooth ----------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid cls; public uint inst; public IntPtr r; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr g, string e, IntPtr w, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr s, ref SP_DEVINFO_DATA d, char[] b, int n, IntPtr req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceProperty(IntPtr s, ref SP_DEVINFO_DATA d, ref DEVPROPKEY k, out uint t, byte[] b, int n, IntPtr req, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_Property(string path, ref DEVPROPKEY k, out uint t, byte[] b, ref int n, int f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNode(out uint inst, string id, int f);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Property(uint inst, ref DEVPROPKEY k, out uint t, byte[] b, ref int n, int f);

    static DEVPROPKEY Key(string g, uint p) { return new DEVPROPKEY { fmtid = new Guid(g), pid = p }; }
    static DEVPROPKEY kBattery = Key("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY kConnected = Key("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static DEVPROPKEY kClassOfDevice = Key("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);
    static DEVPROPKEY kContainer = Key("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
    static DEVPROPKEY kFriendlyName = Key("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    static DEVPROPKEY kDeviceDesc = Key("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
    static DEVPROPKEY kInstanceId = Key("78C34FC8-104A-4ACA-9EA4-524D52996E57", 256);

    static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k, out uint t)
    {
        var b = new byte[512];
        return SetupDiGetDeviceProperty(set, ref d, ref k, out t, b, b.Length, IntPtr.Zero, 0) ? b : null;
    }
    static string Str(byte[] b) { return b == null ? "" : System.Text.Encoding.Unicode.GetString(b).Split('\0')[0]; }
    static byte[] Head(byte[] b, int n) { var r = new byte[n]; Array.Copy(b, r, n); return r; }

    // Container id of the physical device behind a HID collection (so one device is not listed twice).
    static string ContainerOf(string path)
    {
        var k = kInstanceId; uint t; var b = new byte[1024]; int n = b.Length;
        if (CM_Get_Device_Interface_Property(path, ref k, out t, b, ref n, 0) != 0) return "";
        uint inst;
        if (CM_Locate_DevNode(out inst, Str(b), 0) != 0) return "";
        k = kContainer; n = b.Length;
        if (CM_Get_DevNode_Property(inst, ref k, out t, b, ref n, 0) != 0 || n < 16) return "";
        return new Guid(Head(b, 16)).ToString();
    }

    public static void Bluetooth(PollContext ctx)
    {
        var handled = new HashSet<string>(); // containers a vendor driver already listed
        foreach (var c in ctx.Colls) if (ctx.IsClaimed(c.Vid, c.Pid)) handled.Add(ContainerOf(c.Path));

        var names = new Dictionary<string, string>();
        var connected = new Dictionary<string, bool>();
        var battery = new Dictionary<string, int>();
        var cod = new Dictionary<string, int>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x6); // ALLCLASSES | PRESENT
        try
        {
            var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
            var idb = new char[512];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                if (!SetupDiGetDeviceInstanceId(set, ref d, idb, idb.Length, IntPtr.Zero)) continue;
                string id = new string(idb).Split('\0')[0];
                if (!id.StartsWith("BTHENUM\\") && !id.StartsWith("BTHLE\\") && !id.StartsWith("BTHLEDEVICE\\")) continue;
                uint t;
                var cb = Prop(set, ref d, kContainer, out t);
                string cid = cb == null ? id : new Guid(Head(cb, 16)).ToString();
                if (id.StartsWith("BTHENUM\\DEV_") || id.StartsWith("BTHLE\\DEV_"))
                {
                    string n = Str(Prop(set, ref d, kFriendlyName, out t));
                    names[cid] = n != "" ? n : Str(Prop(set, ref d, kDeviceDesc, out t));
                    var cn = Prop(set, ref d, kConnected, out t);
                    if (cn != null && t == 0x11) connected[cid] = cn[0] != 0; // DEVPROP_TYPE_BOOLEAN
                    var cd = Prop(set, ref d, kClassOfDevice, out t);
                    if (cd != null && t == 0x07) cod[cid] = BitConverter.ToInt32(cd, 0); // DEVPROP_TYPE_UINT32
                }
                var bt = Prop(set, ref d, kBattery, out t);
                if (bt != null && t == 0x03 && bt[0] <= 100) battery[cid] = bt[0]; // DEVPROP_TYPE_BYTE
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        foreach (var kv in battery)
        {
            bool on;
            if (handled.Contains(kv.Key) || (connected.TryGetValue(kv.Key, out on) && !on)) continue;
            string name;
            if (!names.TryGetValue(kv.Key, out name) || name == "") name = "Bluetooth device";
            if (ctx.HasName(name)) continue;
            int c; // Class of Device: major class 0x04 = audio (headphones/headsets)
            bool headset = cod.TryGetValue(kv.Key, out c) && ((c >> 8) & 0x1F) == 0x04;
            ctx.Output.Add(new Reading { Key = "bt:" + kv.Key, Name = name, Percent = kv.Value, Headset = headset });
        }
    }
}
