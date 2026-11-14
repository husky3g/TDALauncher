using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TDALauncher
{
    static class Net
    {
        static Net() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        public static WebClient Client()
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "TDALauncher/" + AppVersion.Text;
            return wc;
        }

        public static Dictionary<string, object> Json(string url, bool githubApi)
        {
            using (var wc = Client())
            {
                if (githubApi) wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(wc.DownloadString(url)) as Dictionary<string, object>;
            }
        }

        public static string Str(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
        public static IEnumerable<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || !(v is IEnumerable)) yield break;
            foreach (object o in (IEnumerable)v) { var x = o as Dictionary<string, object>; if (x != null) yield return x; }
        }
    }

    // ------------------------------------------------------------------ self-update from GitHub Releases
    class UpdateInfo { public Version Version; public string Tag, Url, Sha256; public long Size; }

    static class Updater
    {
        public static Version Current { get { return Normalize(new Version(AppVersion.Text)); } }
        static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        // Latest published release, when it is newer than this build and carries TDALauncher.exe.
        public static UpdateInfo Check()
        {
            var rel = Net.Json("https://api.github.com/repos/" + Config.Repo + "/releases/latest", true);
            string tag = Net.Str(rel, "tag_name");
            Version v;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            v = Normalize(v);
            if (v <= Current) return null;
            foreach (var a in Net.List(rel, "assets"))
                if (string.Equals(Net.Str(a, "name"), "TDALauncher.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string digest = Net.Str(a, "digest");
                    long size; long.TryParse(Net.Str(a, "size"), out size);
                    return new UpdateInfo
                    {
                        Version = v, Tag = tag, Url = Net.Str(a, "browser_download_url"), Size = size,
                        Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null
                    };
                }
            return null;
        }

        // Downloads the new exe next to this one, verifies it, and swaps it in (the running exe is renamed to .old).
        public static void Apply(UpdateInfo u)
        {
            string exe = Application.ExecutablePath, tmp = exe + ".new", old = exe + ".old";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var wc = Net.Client()) wc.DownloadFile(u.Url, tmp);
            byte[] bytes = File.ReadAllBytes(tmp);
            try
            {
                if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new Exception("the download isn't a Windows program");
                if (u.Size > 0 && bytes.Length != u.Size) throw new Exception("the download is incomplete");
                if (u.Sha256 != null)
                    using (var sha = SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != u.Sha256)
                            throw new Exception("the download failed its checksum");
            }
            catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch (Exception) { File.Move(old, exe); throw; }
        }

        public static void Restart() { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--updated") { UseShellExecute = false }); }

        // Removes the previous exe left behind by an update (it may still be closing for a moment).
        public static void CleanUp()
        {
            string old = Application.ExecutablePath + ".old";
            if (!File.Exists(old)) return;
            new Thread(() =>
            {
                for (int i = 0; i < 10 && File.Exists(old); i++) { try { File.Delete(old); } catch (Exception) { Thread.Sleep(1000); } }
            }) { IsBackground = true }.Start();
        }
    }

    // ------------------------------------------------------------------ Discord #server-announcements (mirrored to the repo's news branch)
    class NewsPost { public string Author, Text, Url; public DateTime Time; }

    static class News
    {
        public static List<NewsPost> Fetch()
        {
            string local = Environment.GetEnvironmentVariable("TDA_NEWS_FILE");   // for testing the panel offline
            Dictionary<string, object> doc = local != null
                ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(local, Encoding.UTF8)) as Dictionary<string, object>
                : Net.Json(Config.NewsUrl + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute, false);
            var list = new List<NewsPost>();
            foreach (var p in Net.List(doc, "posts"))
            {
                DateTime t;
                DateTime.TryParse(Net.Str(p, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);
                list.Add(new NewsPost { Author = Net.Str(p, "author") ?? "", Text = Net.Str(p, "text") ?? "", Url = Net.Str(p, "url") ?? Config.AnnouncementsUrl, Time = t });
            }
            return list.OrderByDescending(x => x.Time).ToList();
        }

        public static string Ago(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "";
            TimeSpan d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "m ago";
            if (d.TotalDays < 1) return (int)d.TotalHours + "h ago";
            if (d.TotalDays < 7) return (int)d.TotalDays + "d ago";
            return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }
    }
}
