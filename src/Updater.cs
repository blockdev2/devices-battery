// Self-update from GitHub Releases.
// Checks the latest release of the repository, downloads its DevicesBattery.exe over HTTPS, verifies the
// SHA-256 digest GitHub publishes for the asset, swaps the running executable and restarts.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Updater
{
    public const string Repo = "blockdev2/devices-battery";
    const string AssetName = "DevicesBattery.exe";

    public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
    public static string CurrentText { get { var v = Current; return v.Major + "." + v.Minor + "." + v.Build; } }

    public class Release { public Version Version; public string Tag, Url, Sha256; }

    static string Get(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "DevicesBattery/" + CurrentText;
        req.Accept = "application/vnd.github+json";
        req.Timeout = 15000;
        using (var resp = req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
    }

    // Latest release, or null when there is none / it has no executable / the network is down.
    public static Release Latest()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string json = Get("https://api.github.com/repos/" + Repo + "/releases/latest");
        var js = new System.Web.Script.Serialization.JavaScriptSerializer();
        var root = js.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
        string tag = root["tag_name"] as string;
        Version v;
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success || !Version.TryParse(m.Value, out v)) return null;
        foreach (System.Collections.Generic.Dictionary<string, object> a in (System.Collections.ArrayList)root["assets"])
        {
            if (!string.Equals(a["name"] as string, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string url = a["browser_download_url"] as string;
            object dg; a.TryGetValue("digest", out dg); // "sha256:<hex>"
            string sha = dg is string && ((string)dg).StartsWith("sha256:") ? ((string)dg).Substring(7) : null;
            return new Release { Version = v, Tag = tag, Url = url, Sha256 = sha };
        }
        return null;
    }

    public static bool IsNewer(Release r)
    {
        if (r == null) return false;
        var v = r.Version; // compare major.minor.build only (4-part 1.0.0.0 would beat 3-part 1.0.0)
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build)) > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
    }

    // Downloads, verifies and swaps the executable, then starts the new version. Throws on any failure,
    // leaving the current executable untouched.
    public static void Install(Release r)
    {
        var uri = new Uri(r.Url);
        if (uri.Scheme != "https" || !uri.Host.EndsWith("github.com")) throw new Exception("Unexpected download location: " + uri.Host);
        string exe = Application.ExecutablePath, tmp = exe + ".new", old = exe + ".old";
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "DevicesBattery/" + CurrentText);
            wc.DownloadFile(r.Url, tmp);
        }
        byte[] data = File.ReadAllBytes(tmp);
        if (data.Length < 10000 || data[0] != 'M' || data[1] != 'Z') { File.Delete(tmp); throw new Exception("The download is not a valid executable."); }
        if (r.Sha256 != null)
        {
            string got;
            using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            if (!got.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); throw new Exception("Checksum mismatch: the download was corrupted or altered."); }
        }
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);       // a running exe can be renamed, not overwritten
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; }
        Process.Start(exe, "--updated");
    }

    // Leftover from the previous update; the old process may still be exiting.
    public static void Cleanup()
    {
        string old = Application.ExecutablePath + ".old";
        for (int i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); } catch (Exception) { System.Threading.Thread.Sleep(250); }
        }
    }
}
