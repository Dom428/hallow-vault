// Hallow Vault v1.9 - the backup log (%APPDATA%\HallowVault\backup-log.txt): one line per backup, skip, refusal, failure,
// restore or import - time, game, what happened. Keeps the newest 2000 lines. Never file contents or settings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HallowVault
{
    public static class Log
    {
        public static string File { get { return Path.Combine(VaultSettings.Dir, "backup-log.txt"); } }
        static readonly object gate = new object();

        public static void Add(string game, string what)
        {
            if (Snap.On) return;
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(VaultSettings.Dir);
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + (game ?? "") + "\t" + (what ?? "").Replace("\r", " ").Replace("\n", " ") + "\r\n";
                    System.IO.File.AppendAllText(File, line, new UTF8Encoding(false));
                    if (new FileInfo(File).Length > 600 * 1024)   // trim now and then, not on every line
                    {
                        var all = System.IO.File.ReadAllLines(File);
                        if (all.Length > 2000) System.IO.File.WriteAllLines(File, all.Skip(all.Length - 2000), new UTF8Encoding(false));
                    }
                }
            }
            catch { }
        }

        // newest first: (time, game, what)
        public static List<string[]> Recent(int max)
        {
            try
            {
                lock (gate)
                {
                    if (!System.IO.File.Exists(File)) return new List<string[]>();
                    return System.IO.File.ReadAllLines(File).Reverse().Take(max).Select(l => l.Split('\t')).Where(p => p.Length >= 3).ToList();
                }
            }
            catch { return new List<string[]>(); }
        }
    }
}
