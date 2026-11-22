// Logitech (VID 046D): HID++ on Unifying / Bolt / LIGHTSPEED receivers, USB cable, and G headsets.
// Message: [report][device index][feature index][function << 4 | sw id][params]
//   report 0x10 = short (7 bytes), 0x11 = long (20 bytes).
// Battery features: 0x1004 unified battery, 0x1000 battery status, 0x1001 voltage, 0x1F20 ADC (headsets).
// HID++ 1.0 (older devices): registers 0x0D (%) and 0x07 (level 1..7).
// PRO X 2 LIGHTSPEED (046D:0AF7) uses its own channel on usage page FFA0 (verified on hardware).
using System;
using System.Collections.Generic;
using System.Text;

class HidPP : IDisposable
{
    const byte SW = 0x0A; // our software id; replies meant for G HUB carry its own
    readonly HidDevice longDev, shortDev;
    public HidPP(HidDevice longDev, HidDevice shortDev) { this.longDev = longDev; this.shortDev = shortDev; }
    public void Dispose() { if (longDev != null) longDev.Dispose(); if (shortDev != null) shortDev.Dispose(); }

    // HID++ 2.0 call. Returns the 16 parameter bytes, or null. `timedOut` tells a silent device
    // (asleep, busy radio) apart from an error reply (feature/function not supported).
    public byte[] Call(byte dev, byte feat, byte func, out bool timedOut, params byte[] args)
    {
        timedOut = false;
        if (longDev == null) return null;
        var tx = new byte[20]; tx[0] = 0x11; tx[1] = dev; tx[2] = feat; tx[3] = (byte)((func << 4) | SW);
        Array.Copy(args, 0, tx, 4, args.Length);
        for (int attempt = 0; attempt < 3; attempt++) // the 2.4 GHz link drops packets now and then: retry
        {
            bool error = false;
            var r = longDev.Request(tx, 400, delegate (byte[] x)
            {
                if (x.Length < 20 || x[0] != 0x11 || x[1] != dev) return false;
                if (x[2] == 0xFF && x[3] == feat && x[4] == tx[3]) { error = true; return true; }
                return x[2] == feat && x[3] == tx[3];
            });
            if (error) return null;
            if (r != null) { var p = new byte[16]; Array.Copy(r, 4, p, 0, 16); return p; }
        }
        timedOut = true;
        return null;
    }

    public byte[] Call(byte dev, byte feat, byte func, params byte[] args) { bool t; return Call(dev, feat, func, out t, args); }

    // Feature index for a feature id: 0 = not supported, -1 = no answer.
    public int Feature(byte dev, ushort id)
    {
        bool t;
        var r = Call(dev, 0, 0, out t, (byte)(id >> 8), (byte)id);
        return r != null ? r[0] : t ? -1 : 0;
    }

    // HID++ 1.0 register read (sub id 0x81) on the short report.
    public byte[] Register(byte dev, byte reg)
    {
        if (shortDev == null) return null;
        bool error = false;
        var r = shortDev.Request(new byte[] { 0x10, dev, 0x81, reg, 0, 0, 0 }, 300, delegate (byte[] x)
        {
            if (x.Length < 7 || x[0] != 0x10 || x[1] != dev) return false;
            if (x[2] == 0x8F && x[3] == 0x81 && x[4] == reg) { error = true; return true; }
            return x[2] == 0x81 && x[3] == reg;
        });
        if (r == null || error) return null;
        return new[] { r[4], r[5], r[6] };
    }
}

static class Logitech
{
    static readonly int[] LiIonV = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 };
    static readonly int[] LiIonP = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 };

    // Voltage -> % for G headsets (calibration from HeadsetControl).
    static int HeadsetPercent(ushort pid, int mv)
    {
        switch (pid)
        {
            case 0x0A66: return Hid.Interpolate(mv, new[] { 4200, 3850, 3790, 3750, 3680, 3330 }, new[] { 100, 50, 30, 20, 5, 0 });
            case 0x0AC4: return Hid.Interpolate(mv, new[] { 4175, 3817, 3766, 3730, 3664, 3310 }, new[] { 100, 50, 30, 20, 5, 0 });
            case 0x0A5C: case 0x0A89: case 0x0A5B: case 0x0A87: case 0x0AB5: case 0x0AFE:
                return Hid.Interpolate(mv, new[] { 4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150 }, new[] { 100, 80, 60, 40, 20, 10, 5, 0 });
            case 0x0AA7: case 0x0AAA: case 0x0ABA: case 0x0AFB:
                return Hid.Interpolate(mv, new[] { 4150, 3830, 3780, 3740, 3670, 3320 }, new[] { 100, 50, 30, 20, 5, 0 });
            default: return Hid.Interpolate(mv, new[] { 4100, 3850, 3780, 3730, 3650, 3300 }, new[] { 100, 50, 30, 20, 5, 0 });
        }
    }

    // Feature indexes never change for a device; caching them avoids re-asking (and a wrong fallback
    // when one lookup times out).
    static readonly Dictionary<string, int> featureCache = new Dictionary<string, int>();
    static int CachedFeature(HidPP h, string devKey, byte dev, ushort id)
    {
        string k = devKey + ":" + id.ToString("X4");
        int f;
        if (featureCache.TryGetValue(k, out f)) return f;
        f = h.Feature(dev, id);
        if (f >= 0) featureCache[k] = f; // a timeout is not cached
        return f;
    }

    // Returns true with a value, false when the device has no battery feature or did not answer.
    // Only the device's best battery feature is used: if it does not answer we report nothing (the UI
    // keeps the last value) instead of falling back to a voltage estimate that disagrees with it.
    static bool Battery20(HidPP h, string devKey, byte dev, ushort pid, Reading s)
    {
        foreach (ushort id in new ushort[] { 0x1004, 0x1000, 0x1001, 0x1F20 })
        {
            int f = CachedFeature(h, devKey, dev, id);
            if (f < 0) return false;  // no answer: device asleep or out of range
            if (f == 0) continue;     // not supported: try the next feature
            bool timedOut;
            var p = h.Call(dev, (byte)f, (byte)(id == 0x1004 ? 1 : 0), out timedOut);
            if (p == null) return false;
            switch (id)
            {
                case 0x1004: // UNIFIED_BATTERY get_status: [state of charge %][level flags][charging status]
                    if (p[0] > 0 && p[0] <= 100) s.Percent = p[0];
                    else if (p[1] != 0) s.Percent = (p[1] & 8) != 0 ? 100 : (p[1] & 4) != 0 ? 60 : (p[1] & 2) != 0 ? 20 : 5;
                    s.Charging = p[2] >= 1 && p[2] <= 3;
                    break;
                case 0x1000: // BATTERY_STATUS: [%][next level][status]
                    s.Charging = p[2] >= 1 && p[2] <= 4;
                    s.Percent = p[0] <= 100 ? p[0] : -1;
                    if (s.Percent == 0 && p[2] == 3) s.Percent = 100;
                    break;
                case 0x1001: // BATTERY_VOLTAGE: [mV hi][mV lo][flags]
                    int mv = (p[0] << 8) | p[1];
                    s.Charging = (p[2] & 0x80) != 0;
                    if (mv > 0) s.Percent = Hid.Interpolate(mv, LiIonV, LiIonP);
                    break;
                default: // ADC_MEASUREMENT (headsets)
                    int hv = (p[0] << 8) | p[1];
                    s.Charging = p[2] == 0x03; s.Headset = true;
                    if (hv > 0) s.Percent = HeadsetPercent(pid, hv);
                    break;
            }
            return s.Percent >= 0 || s.Charging;
        }
        return false;
    }

    static string Name20(HidPP h, string devKey, byte dev, Reading s)
    {
        int f = CachedFeature(h, devKey, dev, 0x0005);
        if (f <= 0) return null;
        var r = h.Call(dev, (byte)f, 0); if (r == null) return null;
        var sb = new StringBuilder();
        while (sb.Length < r[0])
        {
            var c = h.Call(dev, (byte)f, 1, (byte)sb.Length); if (c == null) break;
            int before = sb.Length;
            foreach (var ch in c) { if (sb.Length >= r[0] || ch == 0) break; sb.Append((char)ch); }
            if (sb.Length == before) break;
        }
        var type = h.Call(dev, (byte)f, 2); // getDeviceType: 0x08 = headset
        if (type != null && type[0] == 0x08) s.Headset = true;
        string n = sb.ToString().Trim();
        if (n == "") return null;
        return n.StartsWith("Logitech") || n.StartsWith("G ") || n.StartsWith("MX") || n.StartsWith("PRO") ? n : "Logitech " + n;
    }

    static bool Battery10(HidPP h, byte dev, Reading s)
    {
        var p = h.Register(dev, 0x0D);
        if (p != null && p[0] > 0 && p[0] <= 100) { s.Percent = p[0]; s.Charging = p[2] >= 0x21 && p[2] <= 0x26; return true; }
        p = h.Register(dev, 0x07);
        if (p != null && p[0] > 0)
        {
            int[] pct = { 0, 5, 10, 20, 35, 50, 75, 100 };
            s.Percent = pct[Math.Min(7, (int)p[0])];
            s.Charging = p[1] >= 0x21 && p[1] <= 0x26;
            return true;
        }
        return false;
    }

    // Groups HID++ collections by physical interface (paths differ only in "&colNN" and the last "&NNNN"):
    //   \\?\hid#vid_046d&pid_c547&mi_02&col01#8&36a92fbd&0&0000#{guid}
    static string Group(string path)
    {
        string p = System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), "&col[0-9a-f]{2}", "");
        return System.Text.RegularExpressions.Regex.Replace(p, "&[0-9a-f]{4}#\\{", "#{");
    }

    // Device names seen per receiver slot, so a sleeping device keeps its key/name.
    static readonly Dictionary<string, string> slotNames = new Dictionary<string, string>();

    public static void Poll(PollContext ctx)
    {
        ProX2(ctx);
        foreach (ushort pid in ctx.Products(0x046D))
        {
            var groups = new Dictionary<string, HidColl[]>(); // [0] = long, [1] = short
            foreach (var c in ctx.Find(0x046D, pid, delegate (HidColl x) { return x.Page == 0xFF00; }))
            {
                string g = Group(c.Path);
                if (!groups.ContainsKey(g)) groups[g] = new HidColl[2];
                if (c.OutLen == 20 && c.InLen == 20) groups[g][0] = c;
                else if (c.OutLen == 7 && c.InLen == 7) groups[g][1] = c;
            }
            bool any = false;
            foreach (var kv in groups)
            {
                var pair = kv.Value;
                if (pair[0] == null && pair[1] == null) continue;
                using (var h = new HidPP(pair[0] == null ? null : HidDevice.Open(pair[0]), pair[1] == null ? null : HidDevice.Open(pair[1])))
                {
                    // 0xFF = device on its own (cable / own dongle); 1..6 = receiver slots.
                    foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                    {
                        var s = new Reading();
                        string devKey = kv.Key + "#" + idx;
                        bool ok;
                        try { ok = Battery20(h, devKey, idx, pid, s) || (idx != 0xFF && Battery10(h, idx, s)); }
                        catch (Exception) { ok = false; }
                        if (!ok) continue;
                        string name = Name20(h, devKey, idx, s);
                        if (name != null) slotNames[devKey] = name;
                        else if (!slotNames.TryGetValue(devKey, out name)) name = "Logitech " + (idx == 0xFF ? Hid.Hex4(pid) : "device " + idx);
                        s.Name = name;
                        s.Key = "logi:" + name;
                        if (ctx.HasName(name)) continue; // same mouse on cable and receiver
                        ctx.Output.Add(s);
                        any = true;
                        if (idx == 0xFF) break; // a direct device has no slots
                    }
                }
            }
            if (any) ctx.Claim(0x046D, pid);
        }
    }

    // PRO X 2 LIGHTSPEED: 51 08 00 03 1A 00 03 00 04 0A -> reply 51 0B 00 03 10 00 06 00 04 0A <pct> <pct> <charging>.
    static void ProX2(PollContext ctx)
    {
        foreach (var c in ctx.Find(0x046D, 0x0AF7, delegate (HidColl x) { return x.Page == 0xFFA0 && x.OutLen == 64; }))
            using (var d = HidDevice.Open(c))
            {
                if (d == null) continue;
                byte[] r = null;
                for (int attempt = 0; attempt < 2 && r == null; attempt++)
                    r = d.Request(new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }, 1000,
                        delegate (byte[] x) { return x.Length > 10 && x[0] == 0x51 && x[1] == 0x0B && x[8] == 0x04 && x[9] == 0x0A; });
                if (r == null || r[10] > 100) continue; // headset off: the dongle stays silent
                // r[12]: 02 on the charging cable, 00 on battery (verified on hardware by plugging/unplugging)
                ctx.Add(new Reading { Key = "logi:0AF7", Name = "PRO X 2 LIGHTSPEED", Percent = r[10], Charging = r[12] != 0, Headset = true }, 0x046D, 0x0AF7);
                return;
            }
    }
}
