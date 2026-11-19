// Hallow Vault - notices when a game is running (any process started from the game's folder), however it was launched,
// and counts its playtime. Reading a process's path needs no admin (PROCESS_QUERY_LIMITED_INFORMATION).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HallowVault
{
    public class Tracker
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        // helpers that live in a game's folder but aren't the game
        static readonly HashSet<string> Ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "crashpad_handler", "UnityCrashHandler64", "UnityCrashHandler32", "CrashReportClient", "CrashSender", "BsSndRpt64", "BsSndRpt", "EasyAntiCheat_EOS_Setup" };

        public static string PathOf(int pid)
        {
            var h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null; }
            finally { CloseHandle(h); }
        }

        public class Running { public Game Game; public DateTime Since; public DateTime LastCount; public int Missed; public string Process; }
        public readonly Dictionary<string, Running> Now = new Dictionary<string, Running>();
        public Action<Game> Started; public Action<Game, DateTime> Stopped;   // Stopped gets when the session began
        public Action<Game, double> Counted;   // minutes to add to a game's playtime

        // Call every few seconds with the current library.
        public void Poll(List<Game> games)
        {
            var dirs = games.Where(g => !string.IsNullOrEmpty(g.InstallDir)).Select(g => Tuple.Create(g, g.InstallDir.TrimEnd('\\') + "\\")).ToList();
            var seen = new Dictionary<string, string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4 || Ignore.Contains(p.ProcessName)) continue;
                    var path = PathOf(p.Id); if (path == null) continue;
                    foreach (var d in dirs)
                        if (path.StartsWith(d.Item2, StringComparison.OrdinalIgnoreCase) && !seen.ContainsKey(d.Item1.Id)) { seen[d.Item1.Id] = p.ProcessName; break; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            var now = DateTime.Now;
            foreach (var id in seen.Keys)
            {
                Running r;
                if (!Now.TryGetValue(id, out r))
                {
                    var g = games.First(x => x.Id == id);
                    r = new Running { Game = g, Since = now, LastCount = now, Process = seen[id] }; Now[id] = r;
                    if (Started != null) Started(g);
                }
                r.Missed = 0;
                var mins = (now - r.LastCount).TotalMinutes;
                if (mins >= 1) { r.LastCount = now; if (Counted != null) Counted(r.Game, mins); }
            }
            foreach (var id in Now.Keys.Where(k => !seen.ContainsKey(k)).ToList())
            {
                var r = Now[id];
                if (++r.Missed < 2) continue;   // gone twice in a row (games restart themselves when patching)
                Now.Remove(id);
                var mins = (now - r.LastCount).TotalMinutes; if (mins > 0 && Counted != null) Counted(r.Game, Math.Min(mins, 1.0));
                if (Stopped != null) Stopped(r.Game, r.Since);
            }
        }
    }
}
