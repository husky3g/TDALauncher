using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TDALauncher
{
    // ------------------------------------------------------------------ server + required content
    static class Config
    {
        public const string ServerName = "The Day After";
        public const string Host = "62.226.2.116";
        public const int Port = 2302;
        public const int QueryPort = Port + 1;          // Arma 3 Steam query port = game port + 1
        public const string Discord = "https://discord.gg/fSqqJg5W3";

        // Official opening: 20 Nov 2026 19:00 CET (= 18:00 UTC). Until then mods can be preloaded but Play is locked.
        public static readonly DateTime LaunchUtc = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);
        // Shown in the player's own time zone and clock format, e.g. "Fri 20 Nov, 19:00" or "Fri 20 Nov, 1:00 PM".
        public static string LaunchText
        {
            get { DateTime t = LaunchUtc.ToLocalTime(); return t.ToString("ddd d MMM") + ", " + t.ToShortTimeString(); }
        }
        public static bool PreLaunch { get { return DateTime.UtcNow < LaunchUtc; } }
        public const int ArmaAppId = 107410;

        public const string Repo = "husky3g/TDALauncher";
        public const string DiscordGuild = "1570790498208059534", AnnouncementsChannel = "1570791863747747890";
        public const string AnnouncementsUrl = "https://discord.com/channels/" + DiscordGuild + "/" + AnnouncementsChannel;
        // Written by .github/workflows/discord-news.yml (a bot reads the channel; the launcher never holds a token).
        public const string NewsUrl = "https://raw.githubusercontent.com/" + Repo + "/news/news.json";

        // Load order matters: CUP core first, then Breaking Point, then the map.
        public static readonly Mod[] Mods =
        {
            new Mod(583496184UL,  "CUP Terrains - Core"),
            new Mod(426629791UL,  "Breaking Point"),
            new Mod(1252091296UL, "Tembelan Island"),
        };
    }

    class Mod
    {
        public readonly ulong Id; public readonly string Name;
        public uint State; public ulong Done, Total; public string Path; public bool Known;
        public Mod(ulong id, string name) { Id = id; Name = name; }

        // EItemState flags
        public const uint Subscribed = 1, Installed = 4, NeedsUpdate = 8, Downloading = 16, DownloadPending = 32;
        public static bool IsReady(uint s) { return (s & Subscribed) != 0 && (s & Installed) != 0 && (s & (NeedsUpdate | Downloading | DownloadPending)) == 0; }
        public bool Ready { get { return Known ? IsReady(State) && Path != null : Path != null; } }
    }

    // ------------------------------------------------------------------ settings
    static class Settings
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDALauncher");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public static string Get(string key)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i).Trim() == key) return line.Substring(i + 1).Trim();
                }
            }
            catch (Exception) { }
            return null;
        }

        public static void Set(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ Steam / Arma discovery
    static class Steam
    {
        public static bool Running()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object pid = k == null ? null : k.GetValue("pid");
                    if (pid is int && (int)pid != 0)
                        try { Process.GetProcessById((int)pid).Dispose(); return true; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return Process.GetProcessesByName("steam").Length > 0;
        }

        public static List<string> Libraries()
        {
            var list = new List<string>();
            string steam = null;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) { if (k != null) steam = k.GetValue("SteamPath") as string; } } catch (Exception) { }
            if (steam == null) return list;
            steam = steam.Replace('/', '\\');
            list.Add(steam);
            try
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (string line in File.ReadAllLines(vdf))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("\"path\"")) { string[] p = t.Split('"'); if (p.Length >= 4) list.Add(p[3].Replace(@"\\", @"\")); }
                    }
            }
            catch (Exception) { }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }
    }

    static class Arma
    {
        public static string Find()
        {
            string saved = Settings.Get("arma");
            if (IsArma(saved)) return saved;
            foreach (string lib in Steam.Libraries())
            {
                string p = Path.Combine(lib, @"steamapps\common\Arma 3");
                if (IsArma(p)) return p;
            }
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Bohemia Interactive\arma 3", @"SOFTWARE\Bohemia Interactive\arma 3" })
                try { using (var k = Registry.LocalMachine.OpenSubKey(key)) { string p = k == null ? null : k.GetValue("main") as string; if (IsArma(p)) return p; } } catch (Exception) { }
            return null;
        }

        public static bool IsArma(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "arma3_x64.exe")) && File.Exists(Path.Combine(dir, "steam_api64.dll"));
        }

        public static bool GameRunning() { return Process.GetProcessesByName("arma3_x64").Length > 0 || Process.GetProcessesByName("arma3").Length > 0; }

        // Workshop folder of a mod found on disk (null when it isn't downloaded)
        public static string WorkshopFolder(ulong id)
        {
            foreach (string lib in Steam.Libraries())
            {
                string p = Path.Combine(lib, @"steamapps\workshop\content\107410\" + id);
                if (Directory.Exists(Path.Combine(p, "addons")) || Directory.Exists(Path.Combine(p, "Addons"))) return p;
            }
            return null;
        }

        // Starts Arma 3 with the server's mods and joins The Day After.
        public static void Launch(string arma, IEnumerable<string> modPaths)
        {
            string game = "\"-mod=" + string.Join(";", modPaths) + "\" -skipIntro -noSplash -world=empty -connect=" + Config.Host + " -port=" + Config.Port;
            string be = Path.Combine(arma, "arma3battleye.exe");
            ProcessStartInfo psi = File.Exists(be)
                ? new ProcessStartInfo(be, "2 1 1 -exe arma3_x64.exe " + game)
                : new ProcessStartInfo(Path.Combine(arma, "arma3_x64.exe"), game);
            psi.WorkingDirectory = arma; psi.UseShellExecute = false;
            Process.Start(psi);
        }
    }

    // ------------------------------------------------------------------ Steam Workshop (runs in a child process)
    // The launcher re-runs itself as "TDALauncher.exe --workshop <check|install> <arma dir> <parent pid>".
    // That child loads Arma 3's own steam_api64.dll as app 107410 and uses ISteamUGC to read item state,
    // subscribe and download. Running it out-of-process keeps Steam's "in game" state off the launcher
    // and lets Arma start cleanly afterwards. Output lines start with "TDA|" (Steam prints its own noise).
    static class Workshop
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool BoolFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFlatFn([Out] byte[] errMsg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint StateFn(IntPtr ugc, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong SubscribeFn(IntPtr ugc, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool DownloadFn(IntPtr ugc, ulong id, [MarshalAs(UnmanagedType.I1)] bool highPriority);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool DownloadInfoFn(IntPtr ugc, ulong id, out ulong done, out ulong total);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool InstallInfoFn(IntPtr ugc, ulong id, out ulong size, [Out] byte[] folder, uint folderSize, out uint timestamp);

        static T Fn<T>(IntPtr lib, string name) where T : class
        {
            IntPtr p = GetProcAddress(lib, name);
            if (p == IntPtr.Zero) throw new Exception("steam_api64.dll has no " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static int Run(string[] args)
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Action<string> say = s => output.WriteLine("TDA|" + s);
            try
            {
                string mode = args[1], arma = args[2];
                int parent = args.Length > 3 ? int.Parse(args[3]) : 0;

                Environment.SetEnvironmentVariable("SteamAppId", Config.ArmaAppId.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", Config.ArmaAppId.ToString());
                SetDllDirectoryW(arma);
                IntPtr lib = LoadLibraryW(Path.Combine(arma, "steam_api64.dll"));
                if (lib == IntPtr.Zero) { say("ERR Could not load Arma 3's steam_api64.dll"); return 2; }

                var shutdown = Fn<VoidFn>(lib, "SteamAPI_Shutdown");
                var callbacks = Fn<VoidFn>(lib, "SteamAPI_RunCallbacks");
                var getState = Fn<StateFn>(lib, "SteamAPI_ISteamUGC_GetItemState");
                var subscribe = Fn<SubscribeFn>(lib, "SteamAPI_ISteamUGC_SubscribeItem");
                var download = Fn<DownloadFn>(lib, "SteamAPI_ISteamUGC_DownloadItem");
                var dlInfo = Fn<DownloadInfoFn>(lib, "SteamAPI_ISteamUGC_GetItemDownloadInfo");
                var instInfo = Fn<InstallInfoFn>(lib, "SteamAPI_ISteamUGC_GetItemInstallInfo");

                // Newer SDKs export SteamAPI_InitFlat (returns ESteamAPIInitResult, 0 = OK); older ones SteamAPI_InitSafe/SteamAPI_Init.
                bool ok; string why = "";
                IntPtr flat = GetProcAddress(lib, "SteamAPI_InitFlat");
                if (flat != IntPtr.Zero)
                {
                    var msg = new byte[1024];
                    ok = ((InitFlatFn)Marshal.GetDelegateForFunctionPointer(flat, typeof(InitFlatFn)))(msg) == 0;
                    int n = Array.IndexOf(msg, (byte)0); why = Encoding.UTF8.GetString(msg, 0, n < 0 ? 0 : n);
                }
                else
                {
                    IntPtr old = GetProcAddress(lib, "SteamAPI_InitSafe"); if (old == IntPtr.Zero) old = GetProcAddress(lib, "SteamAPI_Init");
                    if (old == IntPtr.Zero) { say("ERR steam_api64.dll has no init function"); return 2; }
                    ok = ((BoolFn)Marshal.GetDelegateForFunctionPointer(old, typeof(BoolFn)))();
                }
                if (!ok) { say("ERR Steam is not ready - make sure Steam is running, you are logged in, and you own Arma 3" + (why.Length > 0 ? " (" + why + ")" : "")); return 3; }

                IntPtr ugc = IntPtr.Zero;
                for (int v = 30; v >= 10 && ugc == IntPtr.Zero; v--)
                {
                    IntPtr p = GetProcAddress(lib, "SteamAPI_SteamUGC_v" + v.ToString("000"));
                    if (p != IntPtr.Zero) ugc = ((PtrFn)Marshal.GetDelegateForFunctionPointer(p, typeof(PtrFn)))();
                }
                if (ugc == IntPtr.Zero) { shutdown(); say("ERR Steam Workshop interface unavailable"); return 4; }

                Func<ulong, string> folderOf = id =>
                {
                    var buf = new byte[1024]; ulong size; uint ts;
                    if (!instInfo(ugc, id, out size, buf, (uint)buf.Length, out ts)) return null;
                    int n = Array.IndexOf(buf, (byte)0); if (n < 0) n = buf.Length;
                    string f = Encoding.UTF8.GetString(buf, 0, n);
                    return Directory.Exists(f) ? f : null;
                };
                Action<ulong, uint> report = (id, s) =>
                {
                    ulong done, total;
                    if (!dlInfo(ugc, id, out done, out total)) { done = 0; total = 0; }
                    say("STATE " + id + " " + s + " " + done + " " + total);
                };

                callbacks();
                if (mode == "install")
                {
                    var subscribed = new HashSet<ulong>();
                    DateTime lastKick = DateTime.MinValue;
                    while (true)
                    {
                        callbacks();
                        bool all = true, kick = (DateTime.UtcNow - lastKick).TotalSeconds > 10;
                        foreach (Mod m in Config.Mods)
                        {
                            uint s = getState(ugc, m.Id);
                            if ((s & Mod.Subscribed) == 0 && subscribed.Add(m.Id)) { subscribe(ugc, m.Id); kick = true; }
                            if (!Mod.IsReady(s)) { all = false; if (kick) download(ugc, m.Id, true); }
                            report(m.Id, s);
                        }
                        if (kick) lastKick = DateTime.UtcNow;
                        if (all) break;
                        if (parent != 0) try { Process.GetProcessById(parent).Dispose(); } catch (Exception) { break; }
                        Thread.Sleep(250);
                    }
                }

                callbacks();
                foreach (Mod m in Config.Mods)
                {
                    report(m.Id, getState(ugc, m.Id));
                    string f = folderOf(m.Id);
                    if (f != null) say("PATH " + m.Id + " " + f);
                }
                say("DONE");
                shutdown();
                return 0;
            }
            catch (Exception e) { say("ERR " + e.Message); return 1; }
        }

        // Launcher side: start the child and stream its lines (null line = finished).
        public static Process Start(string mode, string arma, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,
                "--workshop " + mode + " \"" + arma + "\" " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = arma
            };
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) onLine(null);
                else if (e.Data.StartsWith("TDA|")) onLine(e.Data.Substring(4));
            };
            p.ErrorDataReceived += (s, e) => { };
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }
    }

    // ------------------------------------------------------------------ server status (Steam A2S_INFO)
    class ServerInfo
    {
        public bool Online; public string Name, Map; public int Players, MaxPlayers;

        public static ServerInfo Query()
        {
            ServerInfo info = null;
            for (int i = 0; i < 3 && (info == null || !info.Online); i++) info = QueryOnce();
            return info;
        }

        static ServerInfo QueryOnce()
        {
            var info = new ServerInfo();
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = 2500;
                    var ep = new IPEndPoint(IPAddress.Parse(Config.Host), Config.QueryPort);
                    byte[] baseReq = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 }.Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();
                    byte[] req = baseReq;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        udp.Send(req, req.Length, ep);
                        IPEndPoint from = null;
                        byte[] r = udp.Receive(ref from);
                        if (r.Length >= 9 && r[4] == 0x41) { req = baseReq.Concat(r.Skip(5).Take(4)).ToArray(); continue; } // challenge
                        if (r.Length > 6 && r[4] == 0x49)
                        {
                            int i = 6;
                            info.Name = ReadString(r, ref i); info.Map = ReadString(r, ref i);
                            ReadString(r, ref i); ReadString(r, ref i); i += 2;
                            info.Players = r[i]; info.MaxPlayers = r[i + 1]; info.Online = true;
                        }
                        break;
                    }
                }
            }
            catch (Exception) { }
            return info;
        }

        static string ReadString(byte[] b, ref int i)
        {
            int s = i; while (i < b.Length && b[i] != 0) i++;
            string v = Encoding.UTF8.GetString(b, s, i - s); i++; return v;
        }
    }
}
