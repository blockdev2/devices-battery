// Per-vendor battery protocols. READ commands only: no lighting, DPI or configuration writes.
// Formats documented by community projects (OpenRazer, HeadsetControl, rivalcfg, OpenRGB and others),
// rewritten here. Except Logitech, none of these were verified on real hardware by this project.
using System;
using System.Collections.Generic;
using System.Threading;

static class Vendors
{
    static bool In(ushort v, params ushort[] l) { return Array.IndexOf(l, v) >= 0; }
    static Reading R(string key, string name)
    {
        bool headset = false;
        foreach (var p in new[] { "blackshark:", "barracuda:", "inzone:", "hyperx:", "rog:1B84" }) headset |= key.StartsWith(p);
        return new Reading { Key = key, Name = name, Headset = headset };
    }
    static void Sleep(int ms) { Thread.Sleep(ms); }
    static string Clean(string n, string fallback, params string[] suffixes)
    {
        foreach (var s in suffixes) if (n.Length > s.Length && n.EndsWith(s)) n = n.Substring(0, n.Length - s.Length);
        return n == "" ? fallback : n;
    }

    public static void PollAll(PollContext ctx)
    {
        Action<PollContext>[] drivers = {
            Razer, RazerHeadsets, SteelSeries, Corsair, HyperX, Glorious, Pulsar, AtkNano, Kysona, Lamzu,
            Endgame, Vaxee, Wlmouse, Ninjutso, Rog, Zowie, CoolerMaster, Areson, Misc, SonyInzone,
        };
        foreach (var d in drivers)
            try { d(ctx); } catch (Exception) { } // one failing driver must not stop the others
    }

    // ================================================================ Razer (1532), 90-byte feature report
    // [0] status [1] transaction id [5] size [6] class [7] command [8..87] args [88] xor(2..87)
    // Battery: class 07 cmd 80 -> args[1] 0..255. Charging: 07 84 -> args[1] == 1.
    // Status: 01 busy, 02 ok, 03 failed, 04 timeout (offline), 05 not supported.
    enum Rz { Ok, Offline, Unsupported, NoReply }

    static byte KeyboardTid(ushort pid)
    {
        if (In(pid, 0x025C, 0x0271, 0x0290, 0x0296, 0x02BA, 0x02D2, 0x02D5)) return 0x9F;
        if (In(pid, 0x0258, 0x0292, 0x0298, 0x02B9, 0x02CD, 0x02D4, 0x02D7)) return 0x1F;
        if (pid == 0x025A) return 0x3F;
        return 0;
    }

    static Rz RazerCmd(HidDevice d, byte tid, byte cls, byte id, out byte[] args)
    {
        args = null;
        var rep = new byte[91]; // [0] = report id 0
        rep[2] = tid; rep[6] = 2; rep[7] = cls; rep[8] = id;
        byte crc = 0; for (int i = 3; i < 89; i++) crc ^= rep[i];
        rep[89] = crc;
        bool send = true; Rz res = Rz.NoReply;
        for (int t = 0; t < 8; t++)
        {
            if (send && !d.SetFeature(rep)) return Rz.NoReply;
            Sleep(KeyboardTid(d.Coll.Pid) != 0 ? 5 : (t == 0 ? 15 : 40));
            send = true; res = Rz.NoReply;
            var r = d.GetFeature(0);
            if (r == null || r.Length < 91) continue;
            byte rc = 0; for (int i = 3; i < 89; i++) rc ^= r[i];
            // Synapse shares the same mailbox: a reply to another request means sending ours again
            if (r[2] != tid || r[3] != 0 || r[4] != 0 || r[5] != 0 || r[6] != 2 || r[7] != cls || r[8] != id || r[89] != rc) continue;
            if (r[1] == 0x01) { send = false; continue; }
            if (r[1] == 0x02) { args = new byte[80]; Array.Copy(r, 9, args, 0, 80); return Rz.Ok; }
            if (r[1] == 0x04) { res = Rz.Offline; continue; }
            if (r[1] == 0x03 || r[1] == 0x05) return Rz.Unsupported;
        }
        return res;
    }

    static void Razer(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x1532))
            foreach (var c in ctx.Find(0x1532, pid, x => x.FeatLen == 91))
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    byte kt = KeyboardTid(pid), tid = 0;
                    byte[] tids = kt != 0 ? new[] { kt } : new byte[] { 0x1F, 0x3F, 0xFF, 0x08, 0x9F };
                    Rz res = Rz.NoReply; bool sawOffline = false; byte[] a = null;
                    foreach (var t in tids)
                    {
                        var r = RazerCmd(d, t, 0x07, 0x80, out a);
                        if (r == Rz.Ok) { res = r; tid = t; break; }
                        sawOffline |= r == Rz.Offline;
                    }
                    if (res != Rz.Ok && sawOffline) res = Rz.Offline;
                    if (res != Rz.Ok && res != Rz.Offline) continue;
                    var s = R("razer:" + Hid.Hex4(pid), Clean(c.Product(), "Razer", " Dongle", " (Wired)", " (Wireless)", " Wireless Receiver", " Receiver", " HyperSpeed Dongle"));
                    if (res == Rz.Offline) s.Connected = false;
                    else
                    {
                        s.Percent = Hid.Clamp100((a[1] * 100 + 127) / 255);
                        byte[] ch;
                        if (RazerCmd(d, tid, 0x07, 0x84, out ch) == Rz.Ok) s.Charging = ch[1] == 1;
                    }
                    if (ctx.HasName(s.Name)) ctx.Claim(0x1532, pid); // mouse on cable + dongle
                    else ctx.Add(s, 0x1532, pid);
                    break;
                }
    }

    // ================================================================ Razer headsets
    static readonly Dictionary<ushort, Reading> v3Cache = new Dictionary<ushort, Reading>();
    static readonly Dictionary<ushort, int> v3CacheAt = new Dictionary<ushort, int>();
    static byte hsSeq, v3xTid = 0x40;

    static bool UseV3Cache(PollContext ctx, ushort pid)
    {
        // these headsets drop their link when queried often: at most once every 5 minutes
        int at;
        if (!v3Cache.ContainsKey(pid) || !v3CacheAt.TryGetValue(pid, out at) || Environment.TickCount - at > 300000) return false;
        ctx.Add(v3Cache[pid], 0x1532, pid);
        return true;
    }
    static void StoreV3(PollContext ctx, ushort pid, Reading s) { v3Cache[pid] = s; v3CacheAt[pid] = Environment.TickCount; ctx.Add(s, 0x1532, pid); }

    static void RazerHeadsets(PollContext ctx)
    {
        // BlackShark V2 HyperSpeed (0565): [02][00][seq][..][6]=04 [9]=80 [10]=cmd, [62]=xor(0..61)
        if (!ctx.IsClaimed(0x1532, 0x0565))
        {
            var cs = ctx.Find(0x1532, 0x0565, x => (x.Page == 0xFF14 || x.Page == 0xFF00) && x.Usage == 1 && x.OutLen >= 64 && x.InLen >= 64);
            cs.Sort((a, b) => (b.Page == 0xFF14).CompareTo(a.Page == 0xFF14));
            foreach (var c in cs)
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    Func<byte, int> q = cmd =>
                    {
                        byte seq = (byte)(0x60 | (hsSeq++ & 0x1F));
                        var b = new byte[64]; b[0] = 2; b[2] = seq; b[6] = 4; b[9] = 0x80; b[10] = cmd;
                        byte x = 0; for (int i = 0; i < 62; i++) x ^= b[i]; b[62] = x;
                        var r = d.Request(b, 650, y =>
                        {
                            if (y.Length < 64 || y[0] != 2 || y[2] != seq || y[10] != cmd || y[12] < 1) return false;
                            byte k = 0; for (int i = 1; i < 62; i++) k ^= y[i];
                            return k == y[62];
                        });
                        return r == null ? -1 : r[13];
                    };
                    int lvl = q(0x21);
                    if (lvl < 0 || lvl > 100) continue;
                    var s = R("blackshark:0565", "Razer BlackShark V2 HyperSpeed"); s.Percent = lvl; s.Charging = q(0x2A) > 0;
                    ctx.Add(s, 0x1532, 0x0565);
                    break;
                }
        }
        // BlackShark V2 Pro (2023) (0555): "PA" envelope; remote mode is switched on around each query
        if (!ctx.IsClaimed(0x1532, 0x0555))
            foreach (var c in ctx.Find(0x1532, 0x0555, x => x.Page == 0xFF00 && x.OutLen >= 64 && x.InLen >= 16))
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    Func<byte, byte, byte, byte, byte, byte[]> pa = (tot, inner, type, cmd, flag) =>
                    { var b = new byte[64]; b[0] = 2; b[1] = 0x80; b[2] = tot; b[5] = (byte)'P'; b[6] = (byte)'A'; b[7] = inner; b[9] = type; b[10] = cmd; b[11] = flag; return b; };
                    Action<bool> remote = on => { d.Write(pa(0x07, 0x0E, 0x02, 0xE1, (byte)(on ? 1 : 0))); Sleep(35); };
                    Func<byte, int> q = cmd =>
                    {
                        d.Flush(); remote(true); int v = -1; // first frame only wakes a dozing link
                        for (int t = 0; t < 3 && v < 0; t++)
                        {
                            remote(true);
                            if (!d.Write(pa(0x08, 0x08, 0x03, cmd, 0))) break;
                            var r = d.ReadUntil(150, y => y.Length >= 16 && y[0] == 2 && y[12] == cmd && y[13] == 1 && y[14] >= 1);
                            if (r != null) v = r[15];
                        }
                        remote(false);
                        return v;
                    };
                    int lvl = q(0x21);
                    if (lvl < 0 || lvl > 100) continue;
                    var s = R("blackshark:0555", "Razer BlackShark V2 Pro (2023)"); s.Percent = lvl; s.Charging = q(0x2A) > 0;
                    ctx.Add(s, 0x1532, 0x0555);
                    break;
                }
        BarracudaX(ctx);
        // BlackShark V3 X (057D dongle, 057C cable): feature report 07, [62] = xor(3..60)
        foreach (ushort pid in new ushort[] { 0x057D, 0x057C })
        {
            if (ctx.IsClaimed(0x1532, pid)) continue;
            var cs = ctx.Find(0x1532, pid, x => x.Iface == 0 && x.FeatLen >= 64);
            if (cs.Count == 0 || UseV3Cache(ctx, pid)) continue;
            using (var d = HidDevice.OpenFeature(cs[0]))
            {
                if (d == null) continue;
                Func<byte, int> q = cmd =>
                {
                    for (int t = 0; t < 4; t++)
                    {
                        var b = new byte[64]; b[0] = 7; b[2] = ++v3xTid; b[6] = 2; b[7] = 7; b[8] = cmd;
                        byte x = 0; for (int i = 3; i <= 60; i++) x ^= b[i]; b[62] = x;
                        if (!d.SetFeature(b)) return -1;
                        Sleep(60);
                        var r = d.GetFeature(7);
                        if (r == null || r.Length < 63 || r[2] != v3xTid || r[7] != 7 || r[8] != cmd || r[6] < 2) continue;
                        if (r[1] != 2 && r[1] != 1) return -1;
                        return r[10];
                    }
                    return -1;
                };
                int lvl = q(0x80);
                if (lvl < 0 || lvl > 100) continue;
                var s = R("blackshark:" + Hid.Hex4(pid), "Razer BlackShark V3 X"); s.Percent = lvl; s.Charging = q(0x84) == 1;
                StoreV3(ctx, pid, s);
            }
        }
        // BlackShark V3 / V3 Pro: interface 5, report 02, [62] = xor(0..61), class 21 = battery
        foreach (ushort pid in new ushort[] { 0x0576, 0x0577, 0x0579, 0x057A })
        {
            if (ctx.IsClaimed(0x1532, pid)) continue;
            var cs = ctx.Find(0x1532, pid, x => x.Iface == 5 && x.OutLen >= 64 && x.InLen >= 15);
            if (cs.Count == 0 || UseV3Cache(ctx, pid)) continue;
            using (var d = HidDevice.Open(cs[0]))
            {
                if (d == null) continue;
                Func<byte, byte, byte[]> pk = (dir, cls) =>
                { var b = new byte[64]; b[0] = 2; b[2] = 0x60; b[6] = 4; b[9] = dir; b[10] = cls; byte x = 0; for (int i = 0; i < 62; i++) x ^= b[i]; b[62] = x; return b; };
                d.Write(pk(0x00, 0x02)); d.Write(pk(0x00, 0x2A)); d.Write(pk(0x80, 0x2A)); // handshake the headset expects
                Sleep(250); d.Flush();
                var r = d.Write(pk(0x80, 0x21)) ? d.ReadUntil(600, y => y.Length >= 15 && y[0] == 2 && y[2] == 0x60 && y[10] == 0x21) : null;
                if (r == null || r[13] > 100) continue;
                var s = R("blackshark:" + Hid.Hex4(pid), (pid == 0x0576 || pid == 0x0577) ? "Razer BlackShark V3 Pro" : "Razer BlackShark V3");
                s.Percent = r[13]; s.Charging = r[14] != 0;
                StoreV3(ctx, pid, s);
            }
        }
    }

    // Barracuda X (2022), YS-Tech receiver 1532:0550: bridge to the Airoha chip, CRC-16/XMODEM over the report.
    static void BarracudaX(PollContext ctx)
    {
        if (ctx.IsClaimed(0x1532, 0x0550)) return;
        foreach (var c in ctx.Find(0x1532, 0x0550, x => x.Page == 0xFF00 && x.Usage == 1 && x.OutLen >= 64 && x.InLen >= 64))
            using (var d = HidDevice.Open(c))
            {
                if (d == null) continue;
                byte seq = 0;
                var stream = new List<byte>();
                Func<byte, byte[], bool> cmd = (group, body) =>
                {
                    seq = (byte)(seq % 127 + 1);
                    var f = new byte[64]; f[0] = 2; f[1] = 0x80; f[2] = (byte)(4 + body.Length);
                    f[5] = 0x50; f[6] = 0x41; f[7] = group; f[8] = seq; Array.Copy(body, 0, f, 9, body.Length);
                    ushort crc = 0;
                    foreach (byte b in f) { crc ^= (ushort)(b << 8); for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1); }
                    f[3] = (byte)crc; f[4] = (byte)(crc >> 8);
                    for (int t = 0; t < 5; t++)
                    {
                        if (t > 0) Sleep(50);
                        if (!d.Write(f)) return false;
                        var st = d.GetInputReport(2);
                        if (st == null || st.Length < 2) return false;
                        if (st[1] == 'O') return true;  // taken
                        if (st[1] != 'F') return false; // anything but "busy" is a refusal
                    }
                    return false;
                };
                Func<Func<byte[], bool>, int, byte[]> waitFrame = (ok, ms) =>
                {
                    int end = Environment.TickCount + ms;
                    for (;;)
                    {
                        while (stream.Count >= 2)
                        {
                            int start = 0;
                            while (start + 1 < stream.Count && !(stream[start] == 0x50 && stream[start + 1] == 0x49)) start++;
                            stream.RemoveRange(0, start);
                            if (stream.Count < 10) break;
                            int len = 10 + (stream[8] | (stream[9] << 8));
                            if (stream.Count < len) break;
                            var fr = stream.GetRange(0, len).ToArray(); stream.RemoveRange(0, len);
                            if (ok(fr)) return fr;
                        }
                        int left = end - Environment.TickCount;
                        var r = left > 0 ? d.Read(left) : null;
                        if (r == null || r.Length < 2) return null;
                        if (r[0] == 2 && r[1] + 2 <= r.Length) for (int i = 0; i < r[1]; i++) stream.Add(r[2 + i]);
                    }
                };
                Func<bool, bool> remote = on =>
                {
                    if (!cmd(0x0E, new byte[] { 0x02, 0xE1, (byte)(on ? 1 : 0) })) return false;
                    byte s0 = seq;
                    var f = waitFrame(y => y.Length >= 13 && y[10] == 0x0E && y[11] == (byte)(0x80 | s0), 800);
                    return f != null && f[12] == 0;
                };
                Func<byte, int> get = c2 =>
                {
                    if (!cmd(0x08, new byte[] { 0x03, c2, 0, 0 })) return -1;
                    var f = waitFrame(y => y.Length >= 13 && y[2] == 0x08 && y[10] == c2 && y[11] == 1, 1000);
                    return f != null && f[12] >= 1 && f.Length >= 14 ? f[13] : -1;
                };
                if (!remote(true)) continue; // headset off, out of range or in Bluetooth mode
                int lvl = get(0x21), cable = lvl >= 0 ? get(0x2A) : -1;
                remote(false);
                if (lvl < 0 || lvl > 100) continue;
                var s = R("barracuda:0550", "Razer Barracuda X (2022)"); s.Percent = lvl; s.Charging = cable == 1;
                ctx.Add(s, 0x1532, 0x0550);
                return;
            }
    }

    // ================================================================ SteelSeries (1038)
    class SsModel { public ushort[] Pids; public Func<HidColl, bool> Accept; public Func<HidDevice, ushort, Reading> Read; }

    static byte[] Xfer(HidDevice d, int ms, params byte[] req) { d.Flush(); return d.Write(req) ? d.Read(ms) : null; }
    static Reading SsHeadset(ushort pid, string name) { var s = R("steelseries:" + Hid.Hex4(pid), name); s.Headset = true; return s; }

    static readonly SsModel[] ssModels = {
        // Arctis 1 / 1 Xbox / 7X: 06 12 -> [2]==01 offline, [3] %
        new SsModel { Pids = new ushort[] { 0x12B3, 0x12B6, 0x12D7 }, Accept = c => c.Page == 0xFF43 && c.Usage == 0x0202, Read = (d, pid) => {
            var r = Xfer(d, 1000, 0x06, 0x12); if (r == null || r.Length < 4) return null;
            var s = SsHeadset(pid, "SteelSeries Arctis 1 Wireless"); s.Connected = r[2] != 1; if (s.Connected) s.Percent = Hid.Clamp100(r[3]); return s; } },
        // Arctis 7 / Pro 2019: 06 18 -> [2] %
        new SsModel { Pids = new ushort[] { 0x1260, 0x12AD, 0x1252 }, Accept = c => c.Page == 0xFF43 && c.Usage == 0x0202, Read = (d, pid) => {
            var r = Xfer(d, 1000, 0x06, 0x18); if (r == null || r.Length < 3) return null;
            var s = SsHeadset(pid, pid == 0x1252 ? "SteelSeries Arctis Pro" : "SteelSeries Arctis 7"); s.Percent = Hid.Clamp100(r[2]); s.Connected = r[2] != 0; return s; } },
        // Arctis 9: 00 20 -> [3] raw 0x64..0x9A, [4]==01 charging
        new SsModel { Pids = new ushort[] { 0x12C2 }, Accept = c => c.Iface == 0 && c.OutLen > 0, Read = (d, pid) => {
            var r = Xfer(d, 1000, 0x00, 0x20); if (r == null || r.Length < 5) return null;
            var s = SsHeadset(pid, "SteelSeries Arctis 9"); s.Percent = Hid.Map(r[3], 0x64, 0x9A); s.Charging = r[4] == 1; return s; } },
        // Arctis Pro Wireless: 41 AA -> [0]==02 offline; 40 AA -> [0] level 0..4
        new SsModel { Pids = new ushort[] { 0x1290 }, Accept = c => c.Iface == 0 && c.OutLen > 0, Read = (d, pid) => {
            Func<byte, byte[]> q = cmd => { var r0 = Xfer(d, 1000, 0x00, cmd, 0xAA); return r0 != null && r0.Length > 0 ? r0 : Xfer(d, 1000, cmd, 0xAA); };
            var r = q(0x41); if (r == null || r.Length == 0) return null;
            var s = SsHeadset(pid, "SteelSeries Arctis Pro Wireless"); s.Connected = r[0] != 2;
            if (s.Connected) { r = q(0x40); if (r != null && r.Length > 0 && r[0] <= 4) s.Percent = Hid.Map(r[0], 0, 4); }
            return s; } },
        // Arctis Nova Pro Wireless: 06 B0 -> [15] 01 offline / 02 charging, [6] level 0..8
        new SsModel { Pids = new ushort[] { 0x12E0, 0x225D }, Accept = c => c.Iface == 4 && c.OutLen > 0, Read = (d, pid) => {
            var r = Xfer(d, 1000, 0x06, 0xB0); if (r == null || r.Length < 16) return null;
            var s = SsHeadset(pid, "SteelSeries Arctis Nova Pro Wireless"); s.Connected = r[15] != 1; s.Charging = r[15] == 2;
            if (s.Connected) s.Percent = Hid.Map(r[6], 0, 8); return s; } },
        // Nova family: 00 B0 -> status block, layout depends on the model
        new SsModel { Pids = new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236, 0x2269, 0x226D, 0x2232, 0x2253, 0x230A, 0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E,
                                            0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7 },
            Accept = c => c.Page == 0xFFC0 && c.Usage == 1, Read = NovaFamily },
        // Aerox 3/5/9, Prime Wireless: 92 (cable) / D2 (wireless) -> [1] = (level/5 + 1) | charging<<7
        new SsModel { Pids = new ushort[] { 0x1838, 0x183A, 0x1852, 0x1854, 0x1858, 0x185A, 0x1840, 0x1842 }, Accept = c => c.Iface == 3 && c.OutLen > 0, Read = (d, pid) => {
            bool cable = In(pid, 0x183A, 0x1854, 0x185A, 0x1842);
            var r = Xfer(d, 300, 0x00, (byte)(cable ? 0x92 : 0xD2)); if (r == null || r.Length < 2) return null;
            string n = In(pid, 0x1838, 0x183A) ? "SteelSeries Aerox 3 Wireless" : In(pid, 0x1852, 0x1854) ? "SteelSeries Aerox 5 Wireless"
                     : In(pid, 0x1858, 0x185A) ? "SteelSeries Aerox 9 Wireless" : "SteelSeries Prime Wireless";
            var s = R("steelseries:" + Hid.Hex4(pid), n); int raw = r[1] & 0x7F;
            if (raw == 0) s.Connected = false; else if (raw <= 21) { s.Percent = (raw - 1) * 5; s.Charging = (r[1] & 0x80) != 0; }
            return s; } },
        // Rival 3 Wireless (+ Gen 2), Rival 650: AA 01 -> [0] %, [2] charging
        new SsModel { Pids = new ushort[] { 0x1830, 0x1872 }, Accept = c => c.Iface == 3 && c.OutLen > 0, Read = Rival },
        new SsModel { Pids = new ushort[] { 0x172B, 0x1726 }, Accept = c => c.Iface == 0 && c.OutLen > 0, Read = Rival },
        // Rival Pro: 92 -> 92 00 <mouse %>
        new SsModel { Pids = new ushort[] { 0x1880 }, Accept = c => c.Iface == 2 && c.OutLen > 0, Read = (d, pid) => {
            var r = d.Request(new byte[] { 0x00, 0x92 }, 500, y => y.Length >= 3 && y[0] == 0x92 && y[1] == 0);
            if (r == null || r[2] > 100) return null;
            var s = R("steelseries:1880", "SteelSeries Rival Pro"); s.Percent = r[2]; return s; } },
    };

    static Reading Rival(HidDevice d, ushort pid)
    {
        var r = Xfer(d, 300, 0x00, 0xAA, 0x01); if (r == null || r.Length < 3) return null;
        bool r650 = pid == 0x172B || pid == 0x1726;
        var s = R(r650 ? "steelseries:172B" : "steelseries:" + Hid.Hex4(pid),
                  r650 ? "SteelSeries Rival 650 Wireless" : pid == 0x1872 ? "SteelSeries Rival 3 Wireless Gen 2" : "SteelSeries Rival 3 Wireless");
        s.Percent = Hid.Clamp100(r[0]); s.Charging = r[2] != 0; return s;
    }

    static Reading NovaFamily(HidDevice d, ushort pid)
    {
        var r = Xfer(d, 1000, 0x00, 0xB0); if (r == null || r.Length < 7) return null;
        Reading s;
        if (In(pid, 0x220E, 0x2212, 0x2216, 0x2236))
        { s = SsHeadset(pid, "SteelSeries Arctis 7+"); s.Connected = r[1] != 1; s.Percent = Hid.Map(r[2], 0, 4); s.Charging = r[3] == 1; }
        else if (In(pid, 0x2269, 0x226D, 0x2232, 0x2253))
        { s = SsHeadset(pid, In(pid, 0x2232, 0x2253) ? "SteelSeries Arctis Nova 5" : "SteelSeries Arctis Nova 3P Wireless"); s.Connected = r[1] != 2; s.Percent = Hid.Clamp100(r[3]); s.Charging = r[4] == 1; }
        else if (pid == 0x230A)
        {   // GameBuds: the lowest bud that is out of the case
            s = SsHeadset(pid, "SteelSeries Arctis GameBuds"); int lvl = -1;
            if (r[3] == 3) lvl = r[5];
            if (r[4] == 3) lvl = lvl < 0 ? r[6] : Math.Min(lvl, (int)r[6]);
            s.Connected = lvl >= 0; s.Percent = lvl >= 0 ? Hid.Clamp100(lvl) : -1;
        }
        else
        {
            bool discrete = In(pid, 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4);
            s = SsHeadset(pid, "SteelSeries Arctis Nova 7"); s.Connected = r[3] != 0; s.Charging = r[3] == 1 || r[3] == 2;
            s.Percent = discrete ? Hid.Map(r[2], 0, 4) : Hid.Clamp100(r[2]);
        }
        if (!s.Connected) s.Percent = -1;
        return s;
    }

    static void SteelSeries(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x1038))
            foreach (var m in ssModels)
            {
                if (Array.IndexOf(m.Pids, pid) < 0) continue;
                foreach (var c in ctx.Find(0x1038, pid, m.Accept))
                    using (var d = HidDevice.Open(c))
                    {
                        var s = d == null ? null : m.Read(d, pid);
                        if (s == null) continue;
                        var dup = ctx.Output.Find(o => o.Key == s.Key);
                        if (dup == null) ctx.Add(s, 0x1038, pid); else ctx.Claim(0x1038, pid);
                        break;
                    }
                break;
            }
    }

    // ================================================================ Corsair (1B1C)
    // Void / HS70 / HS80: output C9 64 -> [2] % (bit 7 = mic), [4] status (0 off, 1-2 ok, 4-5 charging).
    // "Bragi" (Virtuoso, Void V2, HS80 MAX, Slipstream): 02 <target> 02 <property> = GET;
    //   property 0F = battery 0..1000, 10 = charge state (1 charging, 3 full), 12 = paired device PID.
    //   Target 08 = the device/receiver itself, 09+ = devices paired to a receiver.
    static readonly ushort[] voidPids = { 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 };
    static readonly ushort[] bragiPids = { 0x2A08, 0x2A02, 0x0A97, 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x2B00 };

    class Bragi
    {
        readonly HidDevice d; readonly bool numbered; int mode; // 0 = unknown, 1 = prefixed, 2 = plain
        public Bragi(HidDevice d) { this.d = d; numbered = d.Coll.OutLen == 64; }
        public bool Get(byte target, byte prop, out uint v)
        {
            if (mode != 2 && Get(false, target, prop, out v)) { mode = 1; return true; }
            if (mode == 1) { v = 0; return false; }
            if (Get(true, target, prop, out v)) { mode = 2; return true; }
            return false;
        }
        bool Get(bool plain, byte target, byte prop, out uint v)
        {
            v = 0;
            byte[] req = plain ? new byte[] { 0, target, 2, prop } : numbered ? new byte[] { 2, target, 2, prop } : new byte[] { 0, 2, target, 2, prop };
            byte source = (byte)(target == 8 ? 0 : 1); int o = plain ? 0 : 1;
            var r = d.Request(req, 300, y => y.Length >= o + 7 && y[o + 1] == 2 && y[o] == source);
            if (r == null || r[o + 2] != 0) return false;
            v = (uint)(r[o + 3] | (r[o + 4] << 8) | (r[o + 5] << 16) | (r[o + 6] << 24));
            return true;
        }
    }

    static void Corsair(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x1B1C))
        {
            if (Array.IndexOf(voidPids, pid) >= 0)
            {
                foreach (var c in ctx.Find(0x1B1C, pid, x => (x.Page == 0xFFC5 || x.Page == 0xFF00) && x.OutLen > 0))
                    using (var d = HidDevice.Open(c))
                    {
                        var r = d == null ? null : Xfer(d, 1000, 0xC9, 0x64);
                        if (r == null || r.Length < 5) continue;
                        byte st = r[4]; string n = c.Product();
                        var s = R("corsair:" + Hid.Hex4(pid), n == "" ? "Corsair wireless headset" : n);
                        s.Headset = true; s.Connected = st != 0; s.Charging = st == 4 || st == 5;
                        if (st == 1 || st == 2 || s.Charging) s.Percent = Hid.Clamp100(r[2] & 0x7F);
                        ctx.Add(s, 0x1B1C, pid);
                        break;
                    }
                continue;
            }
            bool known = Array.IndexOf(bragiPids, pid) >= 0;
            foreach (var c in ctx.Find(0x1B1C, pid, x => (x.OutLen == 64 || x.OutLen == 65) && (x.InLen == 64 || x.InLen == 65)
                                                        && (x.Page == 0xFF42 || (known && x.Page >= 0xFF00) || (known && x.Iface == 4))))
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    var b = new Bragi(d); bool any = false; string prod = c.Product();
                    foreach (byte target in new byte[] { 0x09, 0x0A, 0x0B, 0x08 })
                    {
                        if (target == 8 && any) break;
                        uint lvl; bool ok = b.Get(target, 0x0F, out lvl);
                        for (int t = 0; ok && (lvl == 0 || lvl > 1000) && t < 2; t++) ok = b.Get(target, 0x0F, out lvl);
                        if (!ok || lvl > 1000) continue;
                        var s = R("corsair:" + Hid.Hex4(pid) + ":" + target, prod == "" ? "Corsair wireless device" : prod);
                        uint dp;
                        if (target != 8 && b.Get(target, 0x12, out dp))
                        {
                            if (dp == 0x1B7E) s.Name = "Corsair Dark Core RGB Pro SE";
                            else if ((dp >> 8) == 0x0A) { s.Headset = true; if (prod.Contains("Receiver")) s.Name = "Corsair wireless headset"; }
                        }
                        if ((pid >> 8) == 0x0A || s.Name.Contains("Headset") || s.Name.Contains("Virtuoso")) s.Headset = true; // Corsair headsets use 0Axx PIDs
                        s.Percent = Hid.Clamp100(((int)lvl + 5) / 10);
                        uint charge; if (b.Get(target, 0x10, out charge)) s.Charging = charge == 1 || charge == 3;
                        ctx.Output.Add(s); any = true;
                    }
                    if (any) { ctx.Claim(0x1B1C, pid); break; }
                }
        }
    }

    // ================================================================ HyperX (03F0 HP, 0951 Kingston)
    class HxModel { public ushort Vid; public ushort[] Pids; public Func<HidColl, bool> Accept; public Func<HidColl, Reading> Read; }
    static Reading Hx(ushort pid, string n) { return R("hyperx:" + Hid.Hex4(pid), n); }

    static readonly HxModel[] hxModels = {
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x05B7, 0x0C9D }, Accept = c => c.OutLen == 62 && c.InLen == 62, Read = Cloud3 },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x069F, 0x0995 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 62 && c.InLen >= 62, Read = Cloud3 },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x06BE, 0x02CC }, Accept = c => c.OutLen == 64 && c.InLen == 64, Read = Cloud3S },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x0696, 0x018B, 0x0D93 }, Accept = c => c.Page == 0xFF90 && c.Usage == 0x0303, Read = Cloud2 },
        new HxModel { Vid = 0x0951, Pids = new ushort[] { 0x1718 }, Accept = c => c.OutLen == 62, Read = Cloud2Kingston },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x098D }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Read = CloudAlpha },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x08BE }, Accept = c => c.Page == 0xFF13 && c.Usage == 0xFF00, Read = CloudAlpha2 },
        new HxModel { Vid = 0x0951, Pids = new ushort[] { 0x16C4, 0x1723, 0x1749 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Read = CloudFlight },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x0E90 }, Accept = c => c.Page >= 0xFF00 && c.OutLen >= 3, Read = CloudFlight },
        new HxModel { Vid = 0x03F0, Pids = new ushort[] { 0x0A93 }, Accept = c => c.Page == 0xFF00 && c.Usage == 1 && c.FeatLen >= 32, Read = StingerCore },
        new HxModel { Vid = 0x0951, Pids = new ushort[] { 0x170B }, Accept = c => c.Page == 0xFF00 && c.Usage == 1 && c.FeatLen >= 32, Read = StingerCore },
    };

    // Cloud III Wireless: 66 <cmd>; 82 -> [2] connected, 8A -> [2] charging, 89 -> [4] %
    static Reading Cloud3(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte[]> q = cmd => d.Request(new byte[] { 0x66, cmd }, 800, y => y.Length >= 5 && y[0] == 0x66 && y[1] == cmd);
            var r = q(0x82); if (r == null) return null;
            var s = Hx(c.Pid, (c.Pid == 0x069F || c.Pid == 0x0995) ? "HyperX Cloud II Core Wireless" : "HyperX Cloud III Wireless");
            s.Connected = r[2] != 0;
            if (s.Connected)
            {
                r = q(0x8A); if (r != null) s.Charging = r[2] == 1;
                r = q(0x89); if (r != null && r[4] <= 100) s.Percent = r[4];
            }
            return s;
        }
    }
    // Cloud II Wireless (HP): 06 FF BB <cmd>; 02 -> [7] %, 03 -> [4] charging
    static Reading Cloud2(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte[]> q = cmd => d.Request(new byte[] { 0x06, 0xFF, 0xBB, cmd }, 1000, y => y.Length >= 8 && y[0] == 6 && y[1] == 0xFF && y[2] == 0xBB && y[3] == cmd);
            var r = q(0x02); if (r == null || r[7] > 100) return null;
            var s = Hx(c.Pid, "HyperX Cloud II Wireless"); s.Percent = r[7];
            r = q(0x03); if (r != null) s.Charging = r[4] == 1;
            return s;
        }
    }
    // Cloud II Wireless (Kingston 0951:1718): fixed frame, reply 0B 00 BB <cmd>
    static Reading Cloud2Kingston(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte[]> q = cmd => d.Request(new byte[] { 0x06, 0, 0x02, 0, 0x9A, 0, 0, 0x68, 0x4A, 0x8E, 0x0A, 0, 0, 0, 0xBB, cmd, 0 }, 1000,
                                                    y => y.Length >= 8 && y[0] == 0x0B && y[2] == 0xBB && y[3] == cmd);
            var r = q(0x02); if (r == null || r[7] > 100) return null;
            var s = Hx(c.Pid, "HyperX Cloud II Wireless"); s.Percent = r[7];
            r = q(0x03); if (r != null) s.Charging = r[4] == 1;
            return s;
        }
    }
    // Cloud Alpha Wireless: 21 BB <cmd>; 03 -> [3]==1 offline, 0C -> charging, 0B -> [3] %
    static Reading CloudAlpha(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte[]> q = cmd => d.Request(new byte[] { 0x21, 0xBB, cmd }, 1000, y => y.Length >= 4 && y[0] == 0x21 && y[2] == cmd);
            var r = q(0x03); if (r == null) return null;
            var s = Hx(c.Pid, "HyperX Cloud Alpha Wireless"); s.Connected = r[3] != 1;
            if (!s.Connected) return s;
            r = q(0x0C); if (r != null) s.Charging = r[3] == 1;
            r = q(0x0B); if (r != null && r[3] <= 100) s.Percent = r[3];
            return s;
        }
    }
    // Cloud Alpha 2 Wireless: [cmd][sub] -> [cmd+1][sub]; 52 01 -> [2] connected, 50 02 -> [2] %, [3] charging
    static Reading CloudAlpha2(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte, byte[]> q = (cmd, sub) => d.Request(new byte[] { cmd, sub }, 1000, y => y.Length >= 8 && y[0] == cmd + 1 && y[1] == sub);
            var r = q(0x52, 0x01); if (r == null) return null;
            var s = Hx(c.Pid, "HyperX Cloud Alpha 2 Wireless"); s.Connected = r[2] != 0;
            if (s.Connected && (r = q(0x50, 0x02)) != null && r[2] <= 100) { s.Percent = r[2]; s.Charging = r[3] == 1; }
            return s;
        }
    }
    // Cloud Flight: 21 FF 05 -> [3..4] mV (above 0x100B = charging)
    static Reading CloudFlight(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            var r = d.Request(new byte[] { 0x21, 0xFF, 0x05 }, 1000, y => y.Length >= 5);
            if (r == null) return null;
            int mv = (r[3] << 8) | r[4];
            var s = Hx(c.Pid, "HyperX Cloud Flight");
            if (mv > 0x100B) s.Charging = true;
            else if (mv <= 3648) s.Percent = Hid.Clamp100((int)(0.00125 * mv));
            else if (mv > 3975) s.Percent = 100;
            else { double v = mv; s.Percent = Hid.Clamp100((int)Math.Round(0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3) + 2.238321 * v * v - 5706.256 * v + 5452299.0)); }
            return s;
        }
    }
    // Cloud III S: 0C 02 03 01 00 <cmd> -> [5] = cmd, [6] = value; 02 -> [6]==2 connected, 06 -> %, 48 -> charge state
    static Reading Cloud3S(HidColl c)
    {
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            Func<byte, byte[]> q = cmd => d.Request(new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, cmd }, 1000, y => y.Length >= 7 && y[0] == 0x0C && y[5] == cmd && y[6] != 0xFF);
            var r = q(0x02); if (r == null) return null;
            var s = Hx(c.Pid, "HyperX Cloud III S Wireless"); s.Connected = r[6] == 2;
            if (!s.Connected) return s;
            r = q(0x06); if (r != null && r[6] <= 100) s.Percent = r[6];
            r = q(0x48); if (r != null) s.Charging = r[6] == 1 || r[6] == 2;
            return s;
        }
    }
    // Cloud Stinger Core Wireless: SetFeature(command), 20 ms, GetFeature(FF); [11] connected; battery [9] code, [11..12] mV
    static Reading StingerCore(HidColl c)
    {
        byte[] kCon = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00 };
        byte[] kBat = { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C };
        int[] pct = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };
        int[] mvChg = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
        int[] mvDis = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };
        using (var d = HidDevice.Open(c))
        {
            if (d == null) return null;
            bool ps = c.Pid == 0x0A93;
            byte[] r = null;
            Func<byte[], Func<byte[], bool>, bool> exchange = (cmd, good) =>
            {
                if (!d.SetFeature(cmd)) return false;
                for (int i = 0; i < (ps ? 10 : 1); i++)
                {
                    Sleep(20); r = d.GetFeature(0xFF);
                    if (r == null || r.Length < 13) return false;
                    if (!ps || good(r)) return true;
                }
                return false;
            };
            Func<byte[], bool> fresh = y => !(y[1] == 1 && y[2] == 0);
            if (!exchange(kCon, fresh) && !(ps && r != null && r.Length >= 13 && !fresh(r))) return null;
            var s = Hx(c.Pid, "HyperX Cloud Stinger Core Wireless"); s.Connected = r[11] == 1;
            if (!s.Connected || !exchange(kBat, y => y[0] == 0xFF && y[1] == 0x12 && y[2] == 0x05)) return s;
            byte code = r[9];
            if (code != 3 && code != 5 && code != 6) return s;
            int mv = r[11] | (r[12] << 8);
            s.Charging = code == 5;
            s.Percent = code == 6 ? 100 : Hid.Interpolate(mv, code == 5 ? mvChg : mvDis, pct);
            return s;
        }
    }

    static void HyperX(PollContext ctx)
    {
        foreach (var m in hxModels)
            foreach (ushort pid in m.Pids)
            {
                if (ctx.IsClaimed(m.Vid, pid)) continue;
                foreach (var c in ctx.Find(m.Vid, pid, m.Accept))
                {
                    var s = m.Read(c);
                    if (s != null) { ctx.Add(s, m.Vid, pid); break; }
                }
            }
    }

    // ================================================================ Glorious / Hitscan / G-Wolves / WLMouse YING
    // 64-byte feature report (id 0): [3]=02 [4]=02 [6]=83. Reply: [1] status, [6]=83, [7] charging, [8] %.
    // Status A1 = awake, A4 = asleep, A0 = waking up.
    static string GWolvesName(ushort pid)
    {
        switch (pid)
        {
            case 0x3808: case 0x3817: return "G-Wolves HTM Plus";
            case 0x6808: case 0x6817: return "G-Wolves HSK Pro 2.0";
            case 0x5608: case 0x5617: case 0x5618: case 0x5619: return "G-Wolves HTXU";
            case 0x3608: case 0x3617: case 0x3619: return "G-Wolves Fenrir Pro";
            case 0x3908: case 0x3917: return "G-Wolves VUK";
            case 0x7904: case 0x7913: return "G-Wolves HT-S2";
            case 0x3708: case 0x3717: return "G-Wolves Fenrir Max";
            case 0x5308: case 0x5317: return "G-Wolves HTS Ultra";
            case 0x7704: case 0x7713: return "G-Wolves HTR";
            case 0x3508: case 0x3517: return "G-Wolves Fenrir";
            case 0x7908: case 0x7917: return "G-Wolves HT-S2 Pro";
            case 0x5808: case 0x5817: return "G-Wolves HSK Pro";
            case 0x2708: case 0x2717: case 0x2719: return "G-Wolves HTX Mini";
            case 0x5408: case 0x5417: case 0x5418: case 0x5419: return "G-Wolves HTS Plus";
            case 0x5708: case 0x5717: return "G-Wolves HTX";
            case 0x5908: case 0x5917: return "G-Wolves HSK Plus";
            case 0x7204: case 0x7203: return "G-Wolves HSK Lite";
            case 0x7708: case 0x7717: return "G-Wolves HTR Pro";
            case 0x5804: case 0x5803: return "G-Wolves HSK Pro ACE";
            case 0x5404: case 0x5403: return "G-Wolves HTS Plus ACE";
            case 0x5704: case 0x5703: return "G-Wolves HTX ACE";
            case 0x5904: case 0x5903: return "G-Wolves HSK Plus ACE";
            case 0x4718: case 0x4719: return "G-Wolves Lycan";
            case 0x4219: return "G-Wolves WARG";
            case 0x5219: return "G-Wolves HTS Plus Pro";
            case 0x3519: return "G-Wolves Fenrir Asym";
            case 0x2619: return "G-Wolves HTX Mini Asym";
            case 0x1619: return "G-Wolves Fenrir Max Asym";
            case 0x2119: return "G-Wolves VUK Mini";
            default: return "G-Wolves wireless mouse";
        }
    }
    static string GloriousName(ushort pid)
    {
        switch (pid)
        {
            case 0x2022: case 0x2011: return "Glorious Model O Wireless";
            case 0x2023: case 0x2012: return "Glorious Model D Wireless";
            case 0x2024: case 0x2013: return "Glorious Model O- Wireless";
            case 0x2025: case 0x2014: return "Glorious Model D- Wireless";
            case 0x2033: return "Glorious Model O 2 Wireless";
            case 0x2034: case 0x2036: case 0x201A: return "Glorious Model D 2 PRO";
            default: return null; // another brand on the same Sinowealth chip (e.g. Redragon keyboards)
        }
    }

    static void Glorious(PollContext ctx)
    {
        var vendors = new[] {
            new { Vid = (ushort)0x258A, Prefix = "glorious:", Delay = 50, Name = (Func<ushort, string>)GloriousName, Filter = (Func<HidColl, bool>)null },
            new { Vid = (ushort)0x3770, Prefix = "hitscan:", Delay = 200, Name = (Func<ushort, string>)(p => p == 0x0136 ? "Hitscan Hyperlight X" : p == 0x0194 ? "Hitscan Hyperlight N" : "Hitscan Hyperlight"), Filter = (Func<HidColl, bool>)null },
            new { Vid = (ushort)0x33E4, Prefix = "gwolves:", Delay = 100, Name = (Func<ushort, string>)GWolvesName, Filter = (Func<HidColl, bool>)null },
            new { Vid = (ushort)0x36A7, Prefix = "wlmouse:", Delay = 100, Name = (Func<ushort, string>)(p => "WLMouse YING"), Filter = (Func<HidColl, bool>)(c => c.Pid == 0xA874 && c.Page == 0xFFFF && c.Usage == 0) },
        };
        foreach (var v in vendors)
            foreach (ushort pid in ctx.Products(v.Vid))
                foreach (var c in ctx.Find(v.Vid, pid, x => x.FeatLen == 65 && (v.Filter == null || v.Filter(x))))
                    using (var d = HidDevice.OpenFeature(c))
                    {
                        if (d == null) continue;
                        byte[] r = null; bool ok = false;
                        for (int t = 0; t < 2; t++)
                        {
                            var w = new byte[65]; w[3] = 2; w[4] = 2; w[6] = 0x83;
                            if (!d.SetFeature(w)) break;
                            Sleep(v.Delay);
                            r = d.GetFeature(0);
                            if (r == null || r.Length < 9 || r[6] != 0x83 || (r[1] & 0xF0) != 0xA0) break; // another protocol
                            if (r[1] == 0xA0 && t == 0) { Sleep(400); continue; } // waking up
                            ok = true; break;
                        }
                        if (!ok) continue;
                        string n = v.Name(pid);
                        if (n == null) { n = c.Product(); if (n == "") n = "Wireless device"; }
                        var s = R(v.Prefix + Hid.Hex4(pid), n);
                        s.Charging = r[7] == 1;
                        if (r[1] == 0xA1 && r[8] <= 100) s.Percent = Hid.Clamp100(r[8] == 0 ? 1 : r[8]); else s.Asleep = true;
                        ctx.Add(s, v.Vid, pid);
                        break;
                    }
    }

    // ================================================================ Pulsar X2 V3 (3710, Sonix)
    // Feature report 0, 64 bytes: [00][08][81][01][..][16-bit LE sum in 62..63]; reply [status][08][81][01][..][..][%]
    static void Pulsar(PollContext ctx)
    {
        var prods = new[] { new { Pid = (ushort)0x3402, Cable = true, Name = "Pulsar X2 V3" }, new { Pid = (ushort)0x3406, Cable = true, Name = "Pulsar X2 V3 eS" },
                            new { Pid = (ushort)0x5402, Cable = false, Name = (string)null }, new { Pid = (ushort)0x5403, Cable = false, Name = (string)null } };
        foreach (var p in prods)
        {
            if (ctx.IsClaimed(0x3710, p.Pid)) continue;
            var cs = ctx.Find(0x3710, p.Pid, x => x.FeatLen >= 65);
            cs.Sort((a, b) => b.Iface.CompareTo(a.Iface)); // interface 2 rejects SET_FEATURE
            foreach (var c in cs)
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    var req = new byte[65]; req[2] = 0x08; req[3] = 0x81; req[4] = 0x01;
                    int sum = 0; for (int i = 1; i < 63; i++) sum += req[i];
                    req[63] = (byte)sum; req[64] = (byte)(sum >> 8);
                    int pct = -1; int rep = 0; bool sawOff = false; // rep: 1 battery, 2 offline
                    for (int t = 0; t < 3 && rep == 0; t++)
                    {
                        if (!d.SetFeature(req)) break;
                        for (int l = 0; l < 6; l++)
                        {
                            Sleep(50);
                            var r = d.GetFeature(0);
                            if (r == null || r.Length < 65) { t = 3; break; }
                            if (r[2] != 0x08 || r[3] != 0x81 || r[4] != 0x01) continue;
                            if (r[1] == 0x01) { if (r[7] <= 100) { pct = r[7]; rep = 1; } else t = 3; break; }
                            sawOff = r[1] == 0x05;
                        }
                    }
                    if (rep == 0 && sawOff) rep = 2;
                    if (rep == 0) continue;
                    string n = p.Name;
                    if (n == null) { n = c.Product(); if (n == "" || n.Contains("Dongle") || n.Contains("Receiver")) n = "Pulsar wireless mouse"; }
                    var s = R("pulsar:" + Hid.Hex4(p.Pid), n);
                    if (p.Cable) s.Charging = true; else if (rep == 2 || pct == 0) s.Asleep = true;
                    if (rep == 1 && (pct > 0 || p.Cable)) s.Percent = pct;
                    ctx.Add(s, 0x3710, p.Pid);
                    break;
                }
        }
    }

    // ================================================================ ATK / VXE NK NANO dongle (373B:1216), BITMOUSE protocol
    static void AtkNano(PollContext ctx)
    {
        if (ctx.IsClaimed(0x373B, 0x1216)) return;
        foreach (var c in ctx.Find(0x373B, 0x1216, x => x.Page == 0xFF05 && x.Usage == 1 && x.OutLen >= 64 && x.InLen >= 8))
            using (var d = HidDevice.Open(c))
            {
                if (d == null) continue;
                Func<byte, int> q = cmd =>
                {
                    var req = new byte[64]; req[0] = 8; req[2] = 0x72; req[3] = 2; req[5] = 1; req[6] = cmd; req[7] = 1;
                    int sum = 0; for (int i = 2; i < 64; i++) sum += req[i]; req[1] = (byte)sum;
                    var r = d.Request(req, 700, y => y.Length >= 8 && y[0] == 8 && y[1] == 0x72 && y[5] == cmd);
                    return r == null || r[2] == 0xFF ? -1 : r[7];
                };
                int lvl = q(0x07);
                if (lvl < 1 || lvl > 100) continue;
                var s = R("atknk:1216", "ATK / VXE NK mouse"); s.Percent = lvl; s.Charging = q(0x0F) == 1;
                ctx.Add(s, 0x373B, 0x1216);
                break;
            }
    }

    // ================================================================ Kysona / Pulsar / ATK / VXE / Hitscan / G-Wolves / Redragon (Compx)
    // 17-byte report 08: [08][cmd][0 x14][checksum], checksum = 0x55 - sum(0..15).
    //   cmd 03 = online -> [6] = 1 connected; cmd 04 = battery -> [6] %, [7] charging.
    static byte Chk55(byte[] b) { int s = 0; for (int i = 0; i < 16; i++) s += b[i]; return (byte)(0x55 - s); }

    static void Kysona(PollContext ctx)
    {
        foreach (ushort vend in new ushort[] { 0x3554, 0x373B, 0x3770, 0x33E4, 0x3710, 0x35BB })
            foreach (ushort pid in ctx.Products(vend))
            {
                if (vend == 0x373B && In(pid, 0x11CA, 0x11CB, 0x1203, 0x1204)) continue; // ATK N9 headsets: serial protocol
                var cs = ctx.Find(vend, pid, x => (x.OutLen == 17 && x.InLen == 17) || (x.Page >= 0xFF00 && x.OutLen >= 17 && x.InLen >= 17));
                cs.Sort((a, b) => (b.Page == 0xFF02 && b.Usage == 2).CompareTo(a.Page == 0xFF02 && a.Usage == 2));
                foreach (var c in cs)
                    using (var d = HidDevice.Open(c))
                    {
                        if (d == null) continue;
                        Func<byte, byte[]> q = cmd =>
                        {
                            var req = new byte[17]; req[0] = 8; req[1] = cmd; req[16] = Chk55(req);
                            return d.Request(req, 500, y => y.Length >= 17 && y[0] == 8 && y[1] == cmd && y[16] == Chk55(y));
                        };
                        var s = new Reading(); bool answered = false;
                        var r = q(0x03);
                        if (r != null) { answered = true; s.Connected = r[6] != 0; }
                        if ((!answered || s.Connected) && (r = q(0x04)) != null)
                        { answered = true; s.Connected = true; if (r[6] <= 100) s.Percent = r[6]; s.Charging = r[7] != 0; }
                        else if (answered && s.Connected) s.Asleep = true;
                        if (!answered) continue;
                        bool redragon = vend == 0x3554 && In(pid, 0xF55D, 0xF5D5, 0xF55E);
                        bool vxe = vend == 0x3554 && In(pid, 0xF58A, 0xF58C, 0xF58E, 0xF58F, 0xF505, 0xF506);
                        s.Key = "kysona:" + Hid.Hex4(vend) + ":" + Hid.Hex4(pid);
                        s.Name = vend == 0x33E4 ? GWolvesName(pid) : redragon ? "Redragon wireless mouse" : vxe ? "VXE Dragonfly R1"
                               : vend == 0x3770 ? "Hitscan Hyperlight" : vend == 0x35BB ? "HECATE mouse"
                               : Clean(c.Product(), vend == 0x373B ? "ATK wireless mouse" : "Wireless mouse", " Dongle", " Receiver", " 2.4G", " Wireless Receiver");
                        if (ctx.HasName(s.Name)) ctx.Claim(vend, pid); else ctx.Add(s, vend, pid);
                        break;
                    }
            }
    }

    // ================================================================ LAMZU (373E, 37B0)
    static string LamzuName(ushort vid, ushort pid)
    {
        if (vid == 0x373E) return In(pid, 0x001C, 0x001D, 0x001E) ? "LAMZU Maya X" : pid == 0x0016 ? "LAMZU Maya X V2" : null;
        if (pid == 0x001F) return "LAMZU Maya X V2";
        if (In(pid, 0x0005, 0x000B, 0x000C)) return "LAMZU Tachi";
        if (In(pid, 0x001C, 0x001E)) return "LAMZU Tachi Lite";
        if (In(pid, 0x0009, 0x000F, 0x0010)) return "LAMZU Inca";
        if (In(pid, 0x0011, 0x0013, 0x0015, 0x003C, 0x0050)) return "LAMZU Maya";
        if (In(pid, 0x0007, 0x000D, 0x000E)) return "LAMZU Paro";
        if (In(pid, 0x0017, 0x0019, 0x001B)) return "LAMZU Thorn";
        if (In(pid, 0x0021, 0x0003, 0x0023, 0x0030, 0x0032, 0x0040)) return "LAMZU Thorn V2";
        if (In(pid, 0x0025, 0x0027)) return "LAMZU Atlantis OG Champion";
        if (In(pid, 0x0028, 0x002B, 0x002A, 0x003E, 0x003F)) return "LAMZU Atlantis Mini";
        if (In(pid, 0x002C, 0x0052)) return "LAMZU Maya M";
        if (In(pid, 0x003A, 0x003B, 0x0044)) return "LAMZU Maya X";
        if (In(pid, 0x0042, 0x0056)) return "LAMZU Orcus";
        if (In(pid, 0x0046, 0x0054)) return "LAMZU Mini";
        return null;
    }
    static bool LamzuCable(ushort vid, ushort pid)
    {
        if (vid == 0x373E) return pid == 0x001C;
        return In(pid, 0x0005, 0x0007, 0x0009, 0x0011, 0x0017, 0x001C, 0x001F, 0x0021, 0x0025, 0x0028, 0x002C, 0x0030, 0x0034, 0x0038, 0x003A, 0x003C,
                  0x003E, 0x0040, 0x0042, 0x0044, 0x0046, 0x0050, 0x0052, 0x0056);
    }
    static void Lamzu(PollContext ctx)
    {
        foreach (ushort vend in new ushort[] { 0x373E, 0x37B0 })
            foreach (ushort pid in ctx.Products(vend))
                foreach (var c in ctx.Find(vend, pid, x => x.FeatLen == 65))
                    using (var d = HidDevice.OpenFeature(c))
                    {
                        if (d == null) continue;
                        var req = new byte[65]; req[3] = 2; req[4] = 2; req[6] = 0x83;
                        if (!d.SetFeature(req)) continue;
                        Sleep(100);
                        var r = d.GetFeature(0);
                        if (r == null || r.Length < 10) continue;
                        int chg, pct;
                        if (r[1] == 0xA1 && r[4] == 2 && r[6] == 0x83) { chg = r[7]; pct = r[8]; }
                        else if (r[0] == 0xA1 && r[3] == 2 && r[5] == 0x83) { chg = r[6]; pct = r[7]; }
                        else continue;
                        if (pct > 100) continue;
                        bool cable = LamzuCable(vend, pid);
                        var s = R("lamzu:" + Hid.Hex4(vend) + ":" + Hid.Hex4(pid), LamzuName(vend, pid) ?? "LAMZU wireless mouse");
                        if (pct > 0) s.Percent = pct; else if (!cable) s.Asleep = true; // 0 from a dongle = mouse off or asleep
                        s.Charging = chg == 1;
                        ctx.Add(s, vend, pid);
                        break;
                    }
    }

    // ================================================================ Endgame Gear 4K (3367:1970)
    // Feature report A1 (64 bytes) on FF01:0002: [A1][cmd][target]; reply [1] status (01 ok, 03 busy, 08/11 asleep).
    // B4 = battery -> [16] %. 0E = info -> [18..19] mouse PID. 0F = probe (only after a failure).
    static readonly Dictionary<string, string> endgameNames = new Dictionary<string, string>();
    static void Endgame(PollContext ctx)
    {
        if (ctx.IsClaimed(0x3367, 0x1970)) return;
        foreach (var c in ctx.Find(0x3367, 0x1970, x => x.Page == 0xFF01 && x.Usage == 2 && x.FeatLen >= 64))
            using (var d = HidDevice.OpenFeature(c))
            {
                if (d == null) continue;
                byte[] r = null;
                Func<byte, byte, int> cmd = (cm, target) =>
                {
                    var req = new byte[64]; req[0] = 0xA1; req[1] = cm; req[2] = target;
                    if (!d.SetFeature(req)) return -1;
                    for (int t = 0; t < 3; t++)
                    {
                        Sleep(150 + t * 100);
                        r = d.GetFeature(0xA1);
                        if (r == null || r.Length < 20 || r[0] != 0xA1) return -1;
                        if (r[1] != 3) return r[1];
                    }
                    return 3;
                };
                int st = cmd(0xB4, 0);
                if (st != 1 && st != 8 && st != 0x11) { cmd(0x0F, 1); cmd(0x0F, 0x0F); st = cmd(0xB4, 0); }
                bool awake = st == 1 && r[16] <= 100, asleep = st == 8 || st == 0x11;
                if (!awake && !asleep) continue;
                var s = R("endgame:1970", null); s.Asleep = asleep;
                if (awake)
                {
                    s.Percent = r[16];
                    if (cmd(0x0E, 0) == 1 && r[16] == 0x67 && r[17] == 0x33)
                    {
                        int mp = r[18] | (r[19] << 8);
                        string n = mp == 0x1972 ? "Endgame Gear OP1w 4k" : mp == 0x1984 ? "Endgame Gear OP1w 4k v2" : mp == 0x1968 ? "Endgame Gear XM2w 4k" : mp == 0x1982 ? "Endgame Gear XM2w 4k v2" : null;
                        if (n != null) endgameNames[c.Path] = n;
                    }
                }
                string nm; s.Name = endgameNames.TryGetValue(c.Path, out nm) ? nm : "Endgame Gear 4k wireless mouse";
                ctx.Add(s, 0x3367, 0x1970);
                break;
            }
    }

    // ================================================================ VAXEE 4K (3057:1002) and ZYGEN NP-01S V2 (3057:1011)
    // Feature report 0E: [0E][A5][cmd][01 = READ][01]; reply [0E][A5][cmd][..][1][value]. 0B = battery 0..20, 10 = charging.
    static void Vaxee(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x3057))
        {
            if (pid != 0x1002 && pid != 0x1011) continue;
            foreach (var c in ctx.Find(0x3057, pid, x => x.Page == 0xFF05 && x.Usage == 1 && x.FeatLen == 64))
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    Func<byte, int> q = cmd =>
                    {
                        var req = new byte[64]; req[0] = 0x0E; req[1] = 0xA5; req[2] = cmd; req[3] = 1; req[4] = 1;
                        if (!d.SetFeature(req)) return -1;
                        Sleep(100);
                        var r = d.GetFeature(0x0E);
                        return r == null || r.Length < 6 || r[0] != 0x0E || r[1] != 0xA5 || r[2] != cmd || r[4] != 1 ? -1 : r[5];
                    };
                    int b = q(0x0B); if (b < 0 || b > 20) b = q(0x0B);
                    if (b < 0 || b > 20) continue;
                    var s = R("vaxee:" + Hid.Hex4(pid), pid == 0x1011 ? "ZYGEN NP-01S V2 Wireless" : "VAXEE wireless mouse");
                    s.Percent = b * 5; int ch = q(0x10); if (ch >= 0) s.Charging = ch != 0;
                    ctx.Add(s, 0x3057, pid);
                    break;
                }
        }
    }

    // ================================================================ WLMouse (36A7)
    // Feature report 0, 65 bytes: [00][00][00][02][02][00][83]; reply [1] status A1/A2 ok, A0 asleep, [7] charging, [8] %.
    static void Wlmouse(PollContext ctx)
    {
        var models = new[] {
            new { Pid = (ushort)0xA879, Receiver = false, Name = "WLMouse Sword X 8K", Pair = (ushort)0xA878 },
            new { Pid = (ushort)0xA884, Receiver = false, Name = "WLMouse Beast X 8K", Pair = (ushort)0xA883 },
            new { Pid = (ushort)0xA860, Receiver = false, Name = "WLMouse mouse", Pair = (ushort)0 },
            new { Pid = (ushort)0xA866, Receiver = false, Name = "WLMouse mouse", Pair = (ushort)0 },
            new { Pid = (ushort)0xA867, Receiver = false, Name = "WLMouse mouse", Pair = (ushort)0 },
            new { Pid = (ushort)0xA856, Receiver = true, Name = "WLMouse Beast X V2", Pair = (ushort)0 },
            new { Pid = (ushort)0xA868, Receiver = true, Name = "WLMouse Beast X Mini Pro", Pair = (ushort)0 },
            new { Pid = (ushort)0xA870, Receiver = true, Name = "WLMouse Beast X Pro", Pair = (ushort)0 },
            new { Pid = (ushort)0xA878, Receiver = true, Name = "WLMouse Sword X 8K", Pair = (ushort)0 },
            new { Pid = (ushort)0xA880, Receiver = true, Name = "WLMouse Beast MAX", Pair = (ushort)0 },
            new { Pid = (ushort)0xA883, Receiver = true, Name = "WLMouse Beast X 8K", Pair = (ushort)0 },
            new { Pid = (ushort)0xA885, Receiver = true, Name = "WLMouse Beast X Mini", Pair = (ushort)0 },
        };
        foreach (var m in models)
        {
            if (ctx.IsClaimed(0x36A7, m.Pid)) continue;
            var cs = ctx.Find(0x36A7, m.Pid, x => x.Page == 0xFFFF && x.FeatLen >= 65);
            cs.Sort((a, b) => (b.Usage == 0).CompareTo(a.Usage == 0));
            foreach (var c in cs)
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    var req = new byte[65]; req[3] = 2; req[4] = 2; req[6] = 0x83;
                    int rep = 0, pct = -1; bool ch = false; // rep: 1 battery, 2 asleep
                    for (int t = 0; t < 2 && rep == 0; t++)
                    {
                        if (!d.SetFeature(req)) break;
                        for (int l = 0; l < 6 && rep == 0; l++)
                        {
                            Sleep(60);
                            var r = d.GetFeature(0);
                            if (r == null || r.Length < 9) { t = 2; break; }
                            if (r[3] != 2 || r[4] != 2 || r[6] != 0x83) continue;
                            if (r[1] == 0xA0) rep = 2;
                            else if (r[1] == 0xA1 || r[1] == 0xA2) { if (r[8] > 100) { t = 2; break; } pct = r[8]; ch = r[7] == 1; rep = 1; }
                        }
                    }
                    if (rep == 0) continue;
                    var s = R("wlmouse:" + Hid.Hex4(m.Pid), m.Name);
                    if (rep == 2) s.Asleep = true; else { s.Percent = pct; s.Charging = ch; }
                    ctx.Add(s, 0x36A7, m.Pid);
                    if (m.Pair != 0) ctx.Claim(0x36A7, m.Pair);
                    break;
                }
        }
    }

    // ================================================================ Ninjutso Sora V2 (1915:AE1C)
    // Feature report 05, cmd 15: [9] battery (0 = 100%), [10] charging, [12] online.
    static void Ninjutso(PollContext ctx)
    {
        if (ctx.IsClaimed(0x1915, 0xAE1C)) return;
        foreach (var c in ctx.Find(0x1915, 0xAE1C, x => x.Page == 0xFFA0 && x.Usage == 1 && x.FeatLen >= 32))
            using (var d = HidDevice.OpenFeature(c))
            {
                if (d == null) continue;
                var req = new byte[32]; req[0] = 5; req[1] = 0x15; req[4] = 1;
                Reading s = null;
                for (int t = 0; t < 4; t++)
                {
                    if (t > 0) Sleep(90);
                    if (!d.SetFeature(req)) break;
                    Sleep(15);
                    var r = d.GetFeature(5);
                    if (r == null || r.Length < 13 || r[0] != 5 || r[1] != 0x15 || r[9] > 100) continue;
                    s = R("ninjutso:AE1C", "Ninjutso Sora V2");
                    bool online = r[12] != 0;
                    s.Asleep = !online;
                    s.Percent = online ? (r[9] == 0 ? 100 : r[9]) : -1;
                    s.Charging = online && r[10] != 0;
                    if (online) break;
                }
                if (s == null) continue;
                ctx.Add(s, 0x1915, 0xAE1C);
                break;
            }
    }

    // ================================================================ ASUS ROG (0B05)
    // Query [report id][12][sub]: sub 01 = keyboard, 07 = mouse. [1..2] == FF AA: asleep.
    // Keyboard: [offset] %, [9]==1 charging. Mouse: [5] level (x25 on older 0-4 gauges), [10] charging.
    class RogModel { public ushort Pid; public string Name; public bool Kb; public byte Rid; public int Iface; public string Col; public int Off, Scale;
        public RogModel(ushort p, string n, bool kb, byte rid, int i, string col, int off, int sc) { Pid = p; Name = n; Kb = kb; Rid = rid; Iface = i; Col = col; Off = off; Scale = sc; } }
    static readonly RogModel[] rog = {
        new RogModel(0x1A85, "ROG Azoth", true, 2, 2, "col02", 6, 1), new RogModel(0x1A85, "ROG Azoth", true, 0, 1, null, 6, 1), new RogModel(0x1A83, "ROG Azoth", true, 0, 1, null, 6, 1),
        new RogModel(0x1B42, "ROG Azoth Extreme", true, 2, 2, "col02", 6, 1), new RogModel(0x1B3F, "ROG Azoth Extreme", true, 0, 1, null, 6, 1),
        new RogModel(0x1CF1, "ROG Azoth Extreme SE", true, 2, 2, "col02", 6, 1), new RogModel(0x1CEF, "ROG Azoth Extreme SE", true, 0, 1, null, 6, 1),
        new RogModel(0x1C25, "ROG Azoth X", true, 0, 1, null, 6, 1), new RogModel(0x1C24, "ROG Azoth X", true, 0, 1, null, 6, 1),
        new RogModel(0x193E, "ROG Falchion", true, 0, 1, null, 11, 1), new RogModel(0x193C, "ROG Falchion", true, 0, 1, null, 11, 1),
        new RogModel(0x1B04, "ROG Falchion RX", true, 0, -1, null, 11, 1), new RogModel(0x1B06, "ROG Falchion RX Low Profile", true, 2, 2, "col02", 11, 1),
        new RogModel(0x1AB0, "ROG Strix Scope II 96", true, 2, 2, "col02", 6, 1), new RogModel(0x1AAE, "ROG Strix Scope II 96", true, 0, 1, null, 6, 1),
        new RogModel(0x1B7A, "ROG Strix Scope II 96 RX", true, 2, 2, "col02", 6, 1), new RogModel(0x1B78, "ROG Strix Scope II 96 RX", true, 0, 1, null, 6, 1),
        new RogModel(0x1A07, "ROG Strix Scope RX TKL", true, 0, -1, null, 6, 1),
        new RogModel(0x18E5, "ROG Chakram", false, 0, 0, null, 5, 25), new RogModel(0x18E3, "ROG Chakram", false, 0, 0, null, 5, 25),
        new RogModel(0x1A1A, "ROG Chakram X", false, 0, 0, null, 5, 1), new RogModel(0x1A18, "ROG Chakram X", false, 0, 0, null, 5, 1),
        new RogModel(0x18A0, "ROG Gladius II Wireless", false, 0, 2, null, 5, 25),
        new RogModel(0x197F, "ROG Gladius III Wireless", false, 0, 0, null, 5, 1), new RogModel(0x197D, "ROG Gladius III Wireless", false, 0, 0, null, 5, 1),
        new RogModel(0x1A72, "ROG Gladius III Aimpoint", false, 3, 2, "col03", 5, 1), new RogModel(0x1A72, "ROG Gladius III Aimpoint", false, 0, 0, null, 5, 1),
        new RogModel(0x1A70, "ROG Gladius III Aimpoint", false, 0, 0, null, 5, 1),
        new RogModel(0x1B0C, "ROG Gladius III Aimpoint EVA-02", false, 0, 0, null, 5, 1), new RogModel(0x1B0A, "ROG Gladius III Aimpoint EVA-02", false, 0, 0, null, 5, 1),
        new RogModel(0x1A94, "ROG Harpe Ace Aim Lab Edition", false, 3, 2, "col03", 5, 1), new RogModel(0x1A94, "ROG Harpe Ace Aim Lab Edition", false, 0, 0, null, 5, 1),
        new RogModel(0x1A92, "ROG Harpe Ace Aim Lab Edition", false, 0, 0, null, 5, 1),
        new RogModel(0x1B68, "ROG Harpe Ace Extreme", false, 3, 2, "col03", 5, 1), new RogModel(0x1B69, "ROG Harpe Ace Extreme", false, 3, 2, "col03", 5, 1),
        new RogModel(0x1B67, "ROG Harpe Ace Extreme", false, 0, 0, null, 5, 1),
        new RogModel(0x1B65, "ROG Harpe Ace Mini", false, 3, 2, "col03", 5, 1), new RogModel(0x1B63, "ROG Harpe Ace Mini", false, 0, 0, null, 5, 1),
        new RogModel(0x1AD0, "ROG Harpe II Ace", false, 3, 2, "col03", 5, 1), new RogModel(0x1C69, "ROG Harpe II Ace", false, 0, 0, null, 5, 1),
        new RogModel(0x1960, "ROG Keris Wireless", false, 0, 0, null, 5, 25), new RogModel(0x195E, "ROG Keris Wireless", false, 0, 0, null, 5, 25),
        new RogModel(0x1A59, "ROG Keris Wireless EVA Edition", false, 0, 0, null, 5, 25), new RogModel(0x1A57, "ROG Keris Wireless EVA Edition", false, 0, 0, null, 5, 25),
        new RogModel(0x1A68, "ROG Keris Wireless Aimpoint", false, 3, 2, "col03", 5, 1), new RogModel(0x1A6A, "ROG Keris Wireless Aimpoint", false, 3, 2, "col03", 5, 1),
        new RogModel(0x1A68, "ROG Keris Wireless Aimpoint", false, 0, 0, null, 5, 1), new RogModel(0x1A66, "ROG Keris Wireless Aimpoint", false, 0, 0, null, 5, 1),
        new RogModel(0x1B1A, "ROG Keris II Ace", false, 3, 2, "col03", 5, 1), new RogModel(0x1B18, "ROG Keris II Ace", false, 3, 2, "col03", 5, 1),
        new RogModel(0x1B16, "ROG Keris II Ace", false, 0, 0, null, 5, 1),
        new RogModel(0x1C0E, "ROG Keris II Origin", false, 3, 2, "col03", 5, 1), new RogModel(0x1C0C, "ROG Keris II Origin", false, 0, 0, null, 5, 1),
        new RogModel(0x1D4E, "ROG Keris II Origin KJP", false, 3, 2, "col03", 5, 1), new RogModel(0x1D4C, "ROG Keris II Origin KJP", false, 0, 0, null, 5, 1),
        new RogModel(0x1908, "ROG Pugio II", false, 0, 0, null, 5, 25), new RogModel(0x1906, "ROG Pugio II", false, 0, 0, null, 5, 25),
        new RogModel(0x1979, "ROG Spatha X", false, 0, 0, null, 5, 1), new RogModel(0x1977, "ROG Spatha X", false, 0, 0, null, 5, 1),
        new RogModel(0x18B4, "ROG Strix Carry", false, 0, 1, null, 7, 25),
        new RogModel(0x1949, "ROG Strix Impact II Wireless", false, 0, 0, null, 5, 25), new RogModel(0x1947, "ROG Strix Impact II Wireless", false, 0, 0, null, 5, 25),
        new RogModel(0x1AD7, "ROG Strix Impact III Wireless", false, 3, 2, "col03", 5, 1),
    };
    static readonly HashSet<ushort> rogSeenAwake = new HashSet<ushort>();

    // 0 = no answer, 1 = ok, 2 = asleep
    static int RogQuery(HidDevice d, byte rid, byte cmd, byte sub, out byte[] resp)
    {
        int st = 0; byte[] got = null;
        d.Request(new byte[] { rid, cmd, sub }, 500, y =>
        {
            if (y.Length < 11) return false;
            var r = y;
            if (rid == 0) { r = new byte[y.Length + 1]; Array.Copy(y, 0, r, 1, y.Length); } // restore the report id slot
            if (r.Length < 12) return false;
            if (r[1] == 0xFF && r[2] == 0xAA) { st = 2; return true; }
            if (r[1] == cmd && r[2] == sub) { st = 1; got = r; return true; }
            return false;
        });
        resp = got; return st;
    }

    static Reading RogRead(HidDevice d, RogModel m)
    {
        byte[] r; int st = RogQuery(d, m.Rid, 0x12, (byte)(m.Kb ? 1 : 7), out r);
        if (st == 0) return null;
        var s = R("rog:" + Hid.Hex4(m.Pid) + (m.Kb ? ":kb" : ":ms"), m.Name);
        if (st == 2) s.Asleep = true;
        else if (m.Kb) { if (r[m.Off] <= 100) s.Percent = r[m.Off]; s.Charging = r[9] == 1; }
        else { int v = r[m.Off] * m.Scale; if (v > 0 && v <= 100) s.Percent = v; else s.Asleep = true; s.Charging = r[10] != 0; }
        return s;
    }

    static void Rog(PollContext ctx)
    {
        // ROG Pelta (1B84): CC 12 07 -> [6] %; CC 12 08 -> [5] charging; FF AA = asleep. No OUT pipe.
        if (!ctx.IsClaimed(0x0B05, 0x1B84))
            foreach (var c in ctx.Find(0x0B05, 0x1B84, x => x.Iface == 3 && x.Page == 0xFF00 && x.Usage == 1 && x.InLen >= 8 && x.OutLen >= 8))
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    Func<byte, byte[]> q = cmd =>
                    {
                        d.Flush(); int end = Environment.TickCount + 1200;
                        var r = d.SetOutputAndRead(new byte[] { 0xCC, 0x12, cmd }, 1200);
                        while (r != null && r.Length >= 8)
                        {
                            if (r[0] == 0xCC && r[1] == 0xFF && r[2] == 0xAA) return new byte[0];
                            if (r[0] == 0xCC && r[1] == 0x12) { if (r[5] == 0xFF && r[6] == 0xAA) return new byte[0]; if (r[2] == cmd) return r; }
                            int left = end - Environment.TickCount; if (left <= 0) break;
                            r = d.Read(left);
                        }
                        return null;
                    };
                    var rr = q(0x07); if (rr == null) continue;
                    var s = R("rog:1B84:hs", "ROG Pelta");
                    if (rr.Length == 0 || rr[6] > 100) s.Asleep = true;
                    else { s.Percent = rr[6]; var ch = q(0x08); if (ch != null && ch.Length > 5) s.Charging = ch[5] == 1; }
                    ctx.Add(s, 0x0B05, 0x1B84);
                    break;
                }
        // ROG Omni Receiver (1ACE): [01 A0 00] on col01 lists the paired PIDs (4-byte slots from byte 5)
        if (!ctx.IsClaimed(0x0B05, 0x1ACE))
        {
            var list = ctx.Find(0x0B05, 0x1ACE, x => x.Iface == 2 && x.OutLen >= 12 && x.Has("col01"));
            if (list.Count > 0)
                using (var d = HidDevice.Open(list[0]))
                {
                    byte[] r;
                    if (d != null && RogQuery(d, 1, 0xA0, 0, out r) == 1)
                    {
                        ctx.Claim(0x0B05, 0x1ACE);
                        for (int at = 5; at + 1 < r.Length; at += 4)
                        {
                            ushort pid = (ushort)(r[at] | (r[at + 1] << 8));
                            if (pid == 0) break;
                            foreach (var m in rog)
                            {
                                if (m.Pid != pid || m.Rid == 0) continue;
                                foreach (var c in ctx.Find(0x0B05, 0x1ACE, x => x.Iface == 2 && x.OutLen >= 12 && x.Has(m.Col)))
                                    using (var dv = HidDevice.Open(c))
                                    {
                                        var s = dv == null ? null : RogRead(dv, m);
                                        if (s != null) { if (!ctx.HasName(s.Name)) ctx.Output.Add(s); break; }
                                    }
                                break;
                            }
                        }
                    }
                }
        }
        foreach (var m in rog)
        {
            if (ctx.IsClaimed(0x0B05, m.Pid)) continue;
            foreach (var c in ctx.Find(0x0B05, m.Pid, x => x.OutLen >= 12 && x.Page >= 0xFF00 && (m.Iface < 0 || x.Iface == m.Iface) && (m.Col == null || x.Has(m.Col))))
                using (var d = HidDevice.Open(c))
                {
                    var s = d == null ? null : RogRead(d, m);
                    if (s == null) continue;
                    // a receiver channel answers "asleep" even with nothing paired: only show it after a real reading
                    if (m.Rid != 0 && s.Asleep && !rogSeenAwake.Contains(m.Pid)) continue;
                    if (m.Rid != 0 && !s.Asleep) rogSeenAwake.Add(m.Pid);
                    if (!ctx.HasName(s.Name)) ctx.Output.Add(s);
                    ctx.Claim(0x0B05, m.Pid);
                    break;
                }
        }
    }

    // ================================================================ BenQ ZOWIE (04A5)
    // EC-CW (8006 receiver, 8005 cable): output [A5 03] -> [mV lo][mV hi][%].
    // DW / U2 (800A, 8008): output 08 [B4 15] on FF03, input 09 on FF04: mV = ([1]*256+[2])*10, [4]==C8.
    static int ZowiePercent(int mv) { return Hid.Interpolate(mv, new[] { 4200, 4000, 3890, 3760, 3680, 3500 }, new[] { 100, 75, 50, 25, 10, 0 }); }
    class ChargeTrend
    {   // no charging flag over the radio: inferred from the voltage going up/down
        bool has, charging; int peak, trough;
        public bool Update(int mv)
        {
            if (!has) { has = true; charging = mv >= 4250; peak = trough = mv; }
            else if (charging) { peak = Math.Max(peak, mv); if (mv <= peak - 20) { charging = false; trough = mv; } }
            else { trough = Math.Min(trough, mv); if (mv >= trough + 50) { charging = true; peak = mv; } }
            return charging;
        }
    }
    static readonly Dictionary<ushort, ChargeTrend> zowieTrend = new Dictionary<ushort, ChargeTrend>();

    static void Zowie(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x04A5))
        {
            if (pid == 0x8005 || pid == 0x8006)
            {
                foreach (var c in ctx.Find(0x04A5, pid, x => (x.Page >= 0xFF00 || x.Page == 0x00FF) && x.OutLen == 65))
                    using (var d = HidDevice.Open(c))
                    {
                        var r = d == null ? null : Xfer(d, 1000, 0x00, 0xA5, 0x03);
                        if (r == null || r.Length < 3) continue;
                        int mv = r[0] | (r[1] << 8);
                        if (mv != 0 && (mv < 3000 || mv > 4500)) continue;
                        var s = R("zowie:" + Hid.Hex4(pid), "ZOWIE EC-CW"); s.Charging = pid == 0x8005;
                        if (mv == 0) s.Asleep = true; else s.Percent = r[2] >= 1 && r[2] <= 100 ? r[2] : ZowiePercent(mv);
                        ctx.Add(s, 0x04A5, pid);
                        break;
                    }
                continue;
            }
            var cmd = ctx.Find(0x04A5, pid, x => x.Page == 0xFF03 && x.OutLen == 16);
            var rep = ctx.Find(0x04A5, pid, x => x.Page == 0xFF04 && x.InLen == 16);
            if (cmd.Count == 0 || rep.Count == 0) continue;
            using (var din = HidDevice.Open(rep[0]))
            using (var dout = din == null ? null : HidDevice.Open(cmd[0]))
            {
                if (dout == null) continue;
                din.Flush();
                if (!dout.Write(0x08, 0xB4, 0x15)) continue;
                var r = din.ReadUntil(1500, y => y.Length >= 5 && y[0] == 0x09);
                if (r == null || r[1] == 0xE6 || r[4] != 0xC8) continue;
                int mv = (r[1] * 256 + r[2]) * 10;
                bool cable = pid != 0x800A && pid != 0x8008;
                string n = cmd[0].Product();
                if (pid == 0x8008) n = "ZOWIE U2"; else if (n == "" || n.Contains("Receiver")) n = "ZOWIE DW mouse";
                var s = R("zowie:" + Hid.Hex4(pid), n);
                if (mv == 0) s.Asleep = true;
                else
                {
                    s.Percent = ZowiePercent(mv);
                    ChargeTrend t; if (!zowieTrend.TryGetValue(pid, out t)) zowieTrend[pid] = t = new ChargeTrend();
                    s.Charging = cable || t.Update(mv);
                }
                ctx.Add(s, 0x04A5, pid);
            }
        }
    }

    // ================================================================ Cooler Master MM712 / MM731 (2516)
    // Interface 3, FF0A: [46][81][00] -> [47][81][model] .. [10] %, [11] charging, [12] link (0 = cable).
    static void CoolerMaster(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x2516))
        {
            if (!In(pid, 0x01B7, 0x0169, 0x0167)) continue;
            foreach (var c in ctx.Find(0x2516, pid, x => x.Iface == 3 && x.Page == 0xFF0A))
                using (var d = HidDevice.Open(c))
                {
                    var r = d == null ? null : d.Request(new byte[] { 0x00, 0x46, 0x81, 0x00 }, 500, y => y.Length >= 13 && y[0] == 0x47 && y[1] == 0x81);
                    if (r == null) continue;
                    string n = pid == 0x0169 || (pid == 0x01B7 && r[2] == 0x50) ? "Cooler Master MM712"
                             : pid == 0x0167 || (pid == 0x01B7 && r[2] == 0x70) ? "Cooler Master MM731" : "Cooler Master wireless mouse";
                    var s = R("coolermaster:" + Hid.Hex4(pid), n);
                    if (r[10] >= 1 && r[10] <= 100) s.Percent = r[10]; else s.Asleep = true;
                    s.Charging = r[11] != 0;
                    var dup = ctx.Output.Find(o => o.Name == n);
                    if (dup != null) { dup.Charging |= s.Charging; ctx.Claim(0x2516, pid); } else ctx.Add(s, 0x2516, pid);
                    break;
                }
        }
    }

    // ================================================================ Redragon on Areson receivers (25A7:FA07/FA08)
    // SET_FEATURE on FF02:0002 (if 1): [08][04][0..][0x55 - sum]; input reply on FF01:0000 (if 1):
    // [09][04][00][..][..][..][level 0..10][charging], the whole frame sums to 0x55.
    static void Areson(PollContext ctx)
    {
        foreach (ushort pid in ctx.Products(0x25A7))
        {
            if (pid != 0xFA07 && pid != 0xFA08) continue;
            var cmds = ctx.Find(0x25A7, pid, x => x.Iface == 1 && x.Page == 0xFF02 && x.Usage == 2 && x.FeatLen >= 17);
            var reps = ctx.Find(0x25A7, pid, x => x.Iface == 1 && x.Page == 0xFF01 && x.Usage == 0 && x.InLen >= 17);
            if (cmds.Count == 0 || reps.Count == 0) continue;
            using (var cmd = HidDevice.OpenFeature(cmds[0]))
            using (var rep = cmd == null ? null : HidDevice.Open(reps[0]))
            {
                if (rep == null) continue;
                var req = new byte[17]; req[0] = 8; req[1] = 4; req[16] = Chk55(req);
                for (int t = 0; t < 2; t++)
                {
                    if (t > 0) Sleep(200);
                    rep.Flush();
                    if (!cmd.SetFeature(req)) break;
                    var r = rep.Read(1800);
                    if (r == null || r.Length < 17) continue;
                    int tot = 0; foreach (var b in r) tot += b;
                    if ((tot & 0xFF) != 0x55 || r[0] != 9 || r[1] != 4 || r[2] != 0 || r[6] > 10) continue;
                    var s = R("areson:" + Hid.Hex4(pid), "Redragon wireless mouse"); s.Percent = Hid.Clamp100(r[6] * 10); s.Charging = r[7] != 0;
                    ctx.Add(s, 0x25A7, pid);
                    break;
                }
            }
        }
    }

    // ================================================================ Sony INZONE H9 II / H5 / Buds (054C)
    // Sony command layer over HID (report 02, 64 bytes) on usage page FF04. Battery = event 04, type 01 (GET).
    static ushort inzoneTid = 1;
    static void SonyInzone(PollContext ctx)
    {
        var models = new[] { new { Pid = (ushort)0x0FA8, Name = "Sony INZONE H9 II", Buds = false, Min = 2 },
                             new { Pid = (ushort)0x0EBF, Name = "Sony INZONE H5", Buds = false, Min = 2 },
                             new { Pid = (ushort)0x0EC2, Name = "Sony INZONE Buds", Buds = true, Min = 6 } };
        foreach (var m in models)
        {
            if (ctx.IsClaimed(0x054C, m.Pid)) continue;
            bool noReply = false, added = false;
            foreach (var c in ctx.Find(0x054C, m.Pid, x => x.Page == 0xFF04 && x.OutLen >= 64))
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    if (++inzoneTid <= 1) inzoneTid = 2; // tid 1 belongs to the dongle's own notifications
                    ushort tid = inzoneTid;
                    var req = new byte[64]; req[0] = 2; req[1] = 12; req[2] = 1; req[4] = 0xFC; req[5] = 8; req[6] = 0x96; req[7] = 0xC3;
                    req[8] = 0x41; req[9] = 0x04; req[10] = 0x01; req[11] = (byte)tid; req[12] = (byte)(tid >> 8);
                    int sm = 0; for (int i = 6; i <= 12; i++) sm += req[i]; req[13] = (byte)sm;
                    byte[] payload = null;
                    var r = d.Request(req, 1500, b =>
                    {
                        if (b.Length < 15 || b[0] != 2) return false;
                        int len = b[1];
                        if (len < 12 || len > 62 || b.Length < len + 2) return false;
                        if (b[2] != 4 || b[3] != 0xFF || b[5] != 0 || b[6] != 0x96 || b[7] != 0xC3 || (b[8] >> 4) != 1) return false;
                        int s2 = 0; for (int i = 5; i <= len; i++) s2 += b[i];
                        if ((byte)s2 != b[len + 1] || b[9] != 4 || (b[11] | (b[12] << 8)) != tid || len + 1 - 13 < m.Min) return false;
                        if (b[10] != 0x10 && b[10] != 0xA0) return false;
                        payload = new byte[len + 1 - 13]; Array.Copy(b, 13, payload, 0, payload.Length);
                        return true;
                    });
                    if (r == null) { noReply = true; continue; }
                    var s = R("inzone:" + Hid.Hex4(m.Pid), m.Name);
                    if (m.Buds)
                    {   // lowest bud that is out of the case; FF = in the case
                        int lvl = -1;
                        foreach (int i in new[] { 1, 3 }) if (payload[i] <= 100) lvl = lvl < 0 ? payload[i] : Math.Min(lvl, (int)payload[i]);
                        s.Asleep = lvl < 0; s.Percent = lvl;
                    }
                    else { bool off = payload[1] == 0xFF; s.Asleep = off; s.Percent = !off && payload[1] <= 100 ? payload[1] : -1; s.Charging = payload[0] != 0; }
                    ctx.Add(s, 0x054C, m.Pid); added = true;
                    break;
                }
            if (m.Buds && noReply && !added) { var s = R("inzone:" + Hid.Hex4(m.Pid), m.Name); s.Asleep = true; ctx.Add(s, 0x054C, m.Pid); }
        }
    }

    // ================================================================ Smaller mouse brands
    static byte deluxSeq;
    static void Misc(PollContext ctx)
    {
        // Motospeed X6 (0BDA FFE0/FFF2 receiver, FFF1 cable; interface 4): output B5 [06] -> report B4, [20] = level (+128 charging)
        foreach (ushort pid in new ushort[] { 0xFFE0, 0xFFF2, 0xFFF1 })
        {
            if (ctx.IsClaimed(0x0BDA, pid)) continue;
            foreach (var c in ctx.Find(0x0BDA, pid, x => x.Iface == 4 && x.OutLen >= 21))
            {
                var s = R("motospeed:" + Hid.Hex4(pid), "Motospeed X6");
                if (pid == 0xFFF1) { s.Charging = true; s.Asleep = true; ctx.Add(s, 0x0BDA, pid); break; } // cable: no reading
                using (var d = HidDevice.Open(c))
                {
                    var r = d == null ? null : d.Request(new byte[] { 0xB5, 0x06 }, 1500, y => y.Length > 20 && y[0] == 0xB4);
                    if (r == null) continue;
                    s.Charging = r[20] >= 128; s.Percent = Hid.Clamp100(s.Charging ? r[20] - 128 : r[20]);
                    ctx.Add(s, 0x0BDA, pid);
                    break;
                }
            }
        }
        // Xenta receivers (1D57; Delux M900 Pro, Attack Shark): broadcast [03][model][40][state][level] every ~2 s.
        // shortcut: opens and listens up to 5 s per scan instead of keeping the handle open; can slow a scan down.
        foreach (ushort pid in new ushort[] { 0xFA60, 0xFA65, 0xFA55, 0x2155 })
        {
            if (ctx.IsClaimed(0x1D57, pid)) continue;
            foreach (var c in ctx.Find(0x1D57, pid, x => x.Page == 0x000A && x.InLen >= 5))
                using (var d = HidDevice.OpenRead(c))
                {
                    if (d == null) continue;
                    byte[] last = null, prev = null; int end = Environment.TickCount + 5000;
                    while (Environment.TickCount < end && (prev == null || last == null))
                    {
                        var r = d.Read(Math.Max(1, end - Environment.TickCount));
                        if (r == null) break;
                        if (r.Length < 5 || r[0] != 3 || r[2] != 0x40) continue;
                        prev = last; last = r;
                    }
                    if (last == null) continue; // mouse off: no frames
                    bool cable = pid == 0xFA55 || pid == 0x2155, tens = last[1] == 0x10;
                    string n = last[1] == 0x3D ? "Delux M900 Pro" : last[1] == 0x55 ? "Attack Shark X11" : last[1] == 0x4E ? "Attack Shark X3 Max"
                             : last[1] == 0xE5 ? "Attack Shark V3 Pro" : last[1] == 0x10 ? "Attack Shark X6 / R1" : "Wireless mouse";
                    var s = R("xenta:" + Hid.Hex4(pid), n);
                    bool agree = prev != null && prev[1] == last[1] && prev[3] == last[3] && prev[4] == last[4]; // every link-up starts with a false 100%
                    byte st = last[3], lvl = last[4];
                    if (cable || st == 2 || st == 3) { s.Charging = true; if (st == 2 && !cable) s.Percent = 100; }
                    else if (agree) { if (tens) { if (lvl >= 1 && lvl <= 10) s.Percent = lvl * 10; } else if (st == 1 && lvl >= 1 && lvl <= 100) s.Percent = lvl; }
                    if (s.Percent < 0 && !s.Charging) s.Asleep = true;
                    ctx.Add(s, 0x1D57, pid);
                    break;
                }
        }
        // Delux M800 Pro (248A 5B2F receiver, 5B2E cable): feature 0C [0C][01][20][00][seq] -> [18] %, [19] state
        foreach (ushort pid in new ushort[] { 0x5B2E, 0x5B2F })
        {
            if (ctx.IsClaimed(0x248A, pid)) continue;
            foreach (var c in ctx.Find(0x248A, pid, x => x.Page == 0xFF00 && x.Usage == 1 && x.FeatLen >= 33))
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    bool rec = pid == 0x5B2F;
                    Func<byte[]> q = () =>
                    {
                        var req = new byte[c.FeatLen]; req[0] = 0x0C; req[1] = 1; req[2] = 0x20; req[4] = ++deluxSeq;
                        if (!d.SetFeature(req)) return null;
                        Sleep(20);
                        var x = d.GetFeature(0x0C);
                        return x != null && x.Length >= 20 && x[0] == 0x0C && x[2] == 0x20 ? x : null;
                    };
                    var r = q(); if (r == null) continue;
                    if (rec && r[6] == 0) { var r2 = q(); if (r2 != null) r = r2; }
                    int pct = r[18]; if (pct > 100) continue;
                    var s = R("delux:m800", "Delux M800 Pro");
                    bool linked = r[6] != 0 || pct > 0;
                    if (r[19] == 2) { s.Percent = 100; s.Charging = true; }
                    else if (r[19] == 1) { s.Charging = true; if (linked && pct > 0) s.Percent = pct; }
                    else if (linked && pct > 0) s.Percent = pct;
                    if (!linked) s.Asleep = true;
                    if (!rec && linked) ctx.Claim(0x248A, 0x5B2F);
                    ctx.Add(s, 0x248A, pid);
                    break;
                }
        }
        // Ajazz AJ179 (249A receiver, 248A cable; interface 2, 33 bytes): cmd 10 -> [12] charging, [13] %, [14] linked, [31] = sum(4..30)
        foreach (ushort vend in new ushort[] { 0x249A, 0x248A })
            foreach (ushort pid in ctx.Products(vend))
                foreach (var c in ctx.Find(vend, pid, x => x.Iface == 2 && x.OutLen == 33 && x.InLen == 33))
                    using (var d = HidDevice.Open(c))
                    {
                        var r = d == null ? null : d.Request(new byte[] { 0x00, 0x10 }, 1500, y =>
                        {
                            if (y.Length < 32 || y[0] != 0x10 || y[1] != 0) return false;
                            int sm = 0; for (int i = 4; i <= 30; i++) sm += y[i];
                            return (byte)sm == y[31];
                        });
                        if (r == null || r[13] > 100) continue;
                        string model = "";
                        for (int i = 4; i < 8 && r[i] >= 0x20 && r[i] < 0x7F; i++) model += (char)r[i];
                        bool rec = vend == 0x249A;
                        var s = R("ajazz:" + Hid.Hex4(vend) + ":" + Hid.Hex4(pid), model.Length == 4 && model[0] == 'M' ? "Ajazz AJ" + model.Substring(1) : "Ajazz mouse");
                        bool linked = !rec || r[14] != 0;
                        s.Charging = r[12] != 0; if (linked) s.Percent = r[13]; else s.Asleep = true;
                        ctx.Add(s, vend, pid);
                        break;
                    }
        // AJAZZ AJ-series on SONiX 3151: SET_FEATURE [00][F7] brings telemetry up; GET_FEATURE 05 -> [3] %
        foreach (ushort pid in ctx.Products(0x3151))
            foreach (var c in ctx.Find(0x3151, pid, x => x.Page == 0xFFFF && x.Usage == 2 && x.FeatLen >= 65))
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null || !d.SetFeature(0x00, 0xF7)) continue;
                    Sleep(30);
                    var r = d.GetFeature(0x05);
                    if (r == null || r.Length < 4 || r[0] != 5 || r[1] != 0 || r[2] != 0 || r[3] > 100) continue;
                    string n = c.Product();
                    var s = R("ajazzaj:" + Hid.Hex4(pid), n == "" ? "AJAZZ mouse" : n);
                    if (r[3] > 0) s.Percent = r[3]; else s.Asleep = true;
                    ctx.Add(s, 0x3151, pid);
                    break;
                }
        // AJAZZ AJ139 V2 (A8A5 dongle / A8A4 cable, PID 2255): FF01:0010, [55][30][A5][chk][2E] -> [AA][30] .. [8] %, [9] USB power
        foreach (ushort vend in new ushort[] { 0xA8A5, 0xA8A4 })
        {
            if (ctx.IsClaimed(vend, 0x2255)) continue;
            foreach (var c in ctx.Find(vend, 0x2255, x => x.Page == 0xFF01 && x.Usage == 0x10 && x.OutLen >= 65 && x.InLen >= 65))
                using (var d = HidDevice.Open(c))
                {
                    if (d == null) continue;
                    Func<byte[], bool> bat = y => y.Length > 9 && y[0] == 0xAA && y[1] == 0x30;
                    var r = d.Request(new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x2E, 0x2E }, 700, bat)
                         ?? d.Request(new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01 }, 800, bat);
                    if (r == null || r[8] > 100) continue;
                    bool cable = vend == 0xA8A4;
                    var s = R("ajazz139:" + Hid.Hex4(vend), "AJAZZ wireless mouse");
                    s.Charging = r[9] != 0; if (cable || r[8] > 0) s.Percent = r[8]; else s.Asleep = true;
                    ctx.Add(s, vend, 0x2255);
                    break;
                }
        }
        // Incott G23 V2 / G24 (093A 522C dongle / 622C cable), FF05: SET_FEATURE [09][89] -> GET 09: [2] bit7 charging, low 7 bits %.
        // The Wraith W1 shares these ids with another command map: if it answers as a W1 (8F -> model 18/20), skip it.
        foreach (ushort pid in new ushort[] { 0x522C, 0x622C })
        {
            if (ctx.IsClaimed(0x093A, pid)) continue;
            var cs = ctx.Find(0x093A, pid, x => x.Page == 0xFF05 && x.FeatLen >= 9);
            cs.Reverse(); // on the cable the first FF05 collection rejects SET_FEATURE
            foreach (var c in cs)
                using (var d = HidDevice.OpenFeature(c))
                {
                    if (d == null) continue;
                    Func<byte, Func<byte[], bool>, byte[]> q = (cmd, ok) =>
                    {
                        if (!d.SetFeature(0x09, cmd)) return null;
                        for (int i = 0; i < 10; i++) { Sleep(50); var x = d.GetFeature(9); if (x == null) return null; if (ok(x)) return x; }
                        return null;
                    };
                    var w1 = q(0x8F, y => y.Length >= 4 && y[0] == 9 && y[1] == 0x8F);
                    if (w1 != null && (w1[3] == 18 || w1[3] == 20)) { ctx.Claim(0x093A, pid); break; } // shortcut: Wraith W1 not supported
                    var r = q(0x89, y => y.Length >= 4 && y[0] == 9 && y[1] == 0x89 && (y[2] | y[3]) != 0);
                    if (r == null) continue;
                    int pct = r[2] & 0x7F; if (pct > 100) continue;
                    bool cable = pid == 0x622C;
                    var s = R("incott:" + Hid.Hex4(pid), "Incott mouse");
                    s.Charging = (r[2] & 0x80) != 0; if (cable || pct > 0) s.Percent = pct; else s.Asleep = true;
                    ctx.Add(s, 0x093A, pid);
                    break;
                }
        }
    }
}
