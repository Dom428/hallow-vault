// Hallow Vault - where each game keeps its saves and settings, and the backups themselves (zip files you can open anywhere).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace HallowVault
{
    public class SaveRoot { public string Path, Kind; public long Bytes; public int Files; public string RegText; public string[] Include, Exclude; }   // Include/Exclude: v1.9 patterns   // RegText: v1.8, a registry root's .reg export
    public class FileStat { public long Bytes; public DateTime When; }
    // v1.2: one line of "what changed between two versions"
    public class FileChange { public string Path, Kind; public long Old = -1, New = -1; }   // Kind: added, removed, bigger, smaller, changed

    public static class Saves
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string LocalLow { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"); } }
        static string Docs { get { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } }

        // games that keep their settings somewhere no rule can guess
        static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]> {
            { "overwatch", new[] { @"{docs}\Overwatch\Settings" } },
            { "diabloiv", new[] { @"{docs}\Diablo IV" } },
            { "minecraft", new[] { @"{roaming}\.minecraft\saves", @"{roaming}\.minecraft\options.txt" } },
            { "valorant", new[] { @"{local}\VALORANT\Saved\Config" } },
            // v1.1: Delta Force keeps its settings and saves inside the game folder (Unreal, but not under LocalAppData)
            { "deltaforce", new[] { @"{install}\Game\DeltaForce\Saved\Config", @"{install}\Game\DeltaForce\Saved\SaveGames" } },
        };
        // never copied: caches, logs, crash dumps, and anything secret
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Logs", "Crashes", "webcache", "webcache_4430", "ShaderCache", "Cache", "cache", "D3DCache", "GPUCache", "Telemetry", "Demos", "CrashReportClient", "Screenshots", "PipelineLibrary" };
        static readonly HashSet<string> SkipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.json" };   // Hallow Mixer's local API key
        const long MaxFile = 512L << 20;
        // %APPDATA%\Hallow<App>\status.json (the Hallow family link) - not settings, and rewritten every few seconds
        static bool IsFamilyStatus(string f)
        {
            if (!System.IO.Path.GetFileName(f).Equals("status.json", StringComparison.OrdinalIgnoreCase)) return false;
            var dir = System.IO.Path.GetDirectoryName(f);
            return System.IO.Path.GetFileName(dir).StartsWith("Hallow", StringComparison.OrdinalIgnoreCase) && string.Equals(System.IO.Path.GetDirectoryName(dir), Roaming, StringComparison.OrdinalIgnoreCase);
        }

        static string Expand(string p) { return p.Replace("{docs}", Docs).Replace("{roaming}", Roaming).Replace("{local}", Local).Replace("{locallow}", LocalLow); }

        // the Steam account signed in now (or the one used last): userdata\<id>
        public static string SteamUserDir()
        {
            var steam = Library.SteamPath(); if (steam == null) return null;
            var ud = System.IO.Path.Combine(steam, "userdata"); if (!Directory.Exists(ud)) return null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                var a = k == null ? null : k.GetValue("ActiveUser");
                if (a is int && (int)a != 0) { var d = System.IO.Path.Combine(ud, ((uint)(int)a).ToString()); if (Directory.Exists(d)) return d; }
            }
            return Directory.GetDirectories(ud).Where(d => File.Exists(System.IO.Path.Combine(d, @"config\localconfig.vdf")))
                .OrderByDescending(d => File.GetLastWriteTime(System.IO.Path.Combine(d, @"config\localconfig.vdf"))).FirstOrDefault();
        }

        public static List<SaveRoot> Find(Game g, string steamUser, IEnumerable<string> extra)
        {
            var list = new List<SaveRoot>();
            Action<string, string> add = (p, kind) =>
            {
                if (string.IsNullOrEmpty(p)) return;
                p = p.TrimEnd('\\');
                if ((Reg.Is(p) ? Reg.Exists(p) : Directory.Exists(p) || File.Exists(p)) && !list.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x.Path + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    list.RemoveAll(x => x.Path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));   // a folder that holds one we already have replaces it
                    list.Add(new SaveRoot { Path = p, Kind = kind });
                }
            };
            if (g.Source == "hallow")
            {
                add(System.IO.Path.Combine(Roaming, "HallowMixer"), "Hallow Mixer");
                add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HallowMixer"), "Hallow Mixer EQ");
                add(System.IO.Path.Combine(Roaming, "HallowHub"), "Hallow Hub");
                add(System.IO.Path.Combine(Roaming, "HallowVault"), "Hallow Vault");
            }
            // v1.11: the online save database (Ludusavi's manifest) goes first for games it knows
            var dirs0 = new[] { g }.Concat(g.Alts).Select(x => x.InstallDir).Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToList();
            var mg = g.Source == "hallow" || ManifestFor == null ? null : ManifestFor(g);
            if (mg != null)
            {
                var steamRoot = Library.SteamPath(); var accts = new List<string>();
                try { if (steamRoot != null && Directory.Exists(System.IO.Path.Combine(steamRoot, "userdata"))) accts = Directory.GetDirectories(System.IO.Path.Combine(steamRoot, "userdata")).Select(System.IO.Path.GetFileName).Where(n => n.All(char.IsDigit)).ToList(); } catch { }
                var bases = dirs0.Count > 0 ? dirs0 : new List<string> { null };
                foreach (var b in bases)
                {
                    var sg = new[] { g }.Concat(g.Alts).FirstOrDefault(x => x.Source == "steam");
                    var w = new Manifest.Where { Base = b, GameDir = b == null ? null : System.IO.Path.GetFileName(b.TrimEnd('\\')), SteamId = sg == null ? null : sg.Id.Substring(6), SteamAccounts = accts };
                    if (steamRoot != null) w.Roots.Add(steamRoot);
                    if (b != null) { var parent = System.IO.Path.GetDirectoryName(b.TrimEnd('\\')); var lib = parent != null && System.IO.Path.GetFileName(parent).Equals("common", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(parent)) : parent; if (lib != null) w.Roots.Add(lib); }
                    foreach (var f in mg.Files.Where(x => ManifestConfig || !x.Item1))
                        foreach (var hit in Manifest.Expand(f.Item2, w)) if (!TooBroad(hit, dirs0, steamRoot)) add(hit, "Save database");
                }
                foreach (var r in mg.Registry.Where(x => ManifestConfig || !x.Item1)) add(r.Item2, "Save database");
            }
            // Vault's own finders still run after it: on this PC the database lists fewer files than they find for several games
            // (Overwatch's per-account settings, Delta Force's SaveGames), so nothing that was backed up before is dropped.
            // Steam keeps cloud saves and per-game settings under userdata\<account>\<appid>
            // (every Steam account on this PC that has a folder for the game - the signed-in one first)
            var accounts = new List<string>();
            if (steamUser != null) { accounts.Add(steamUser); try { accounts.AddRange(Directory.GetDirectories(System.IO.Path.GetDirectoryName(steamUser)).Where(d => !string.Equals(d, steamUser, StringComparison.OrdinalIgnoreCase))); } catch { } }
            foreach (var sg in new[] { g }.Concat(g.Alts).Where(x => x.Source == "steam"))
                foreach (var acc in accounts) add(System.IO.Path.Combine(acc, sg.Id.Substring(6)), acc == steamUser ? "Steam" : "Steam (other account)");
            var dirs = new[] { g }.Concat(g.Alts).Select(x => x.InstallDir).Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToList();
            var devs = new List<Tuple<string, string>>();   // v1.8: (developer, product) from the game's own files
            foreach (var dir in dirs)
            {
                // Unity: <game>_Data\app.info holds "company" and "product" -> LocalLow\company\product
                try
                {
                    foreach (var data in Directory.GetDirectories(dir, "*_Data"))
                    {
                        var info = System.IO.Path.Combine(data, "app.info"); if (!File.Exists(info)) continue;
                        var l = File.ReadAllLines(info); if (l.Length >= 2) { add(System.IO.Path.Combine(LocalLow, l[0].Trim(), l[1].Trim()), "Unity"); devs.Add(Tuple.Create(l[0].Trim(), l[1].Trim())); }
                    }
                }
                catch { }
                // Unreal: a project folder inside the game whose name matches LocalAppData\<name>\Saved
                try
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d1 in Directory.GetDirectories(dir)) { names.Add(System.IO.Path.GetFileName(d1)); try { foreach (var d2 in Directory.GetDirectories(d1)) names.Add(System.IO.Path.GetFileName(d2)); } catch { } }
                    foreach (var n in names)
                    {
                        if (n.Equals("Engine", StringComparison.OrdinalIgnoreCase) || SkipDirs.Contains(n)) continue;
                        var s = System.IO.Path.Combine(Local, n, "Saved"); if (Directory.Exists(s)) add(s, "Unreal");
                    }
                }
                catch { }
                devs.AddRange(ExeDevs(dir));
            }
            // a few well-known places named after the game
            var norm = Library.Norm(g.Name);
            string[] known;
            if (Known.TryGetValue(norm, out known))
                foreach (var k in known)
                {
                    if (k.Contains("{install}")) { foreach (var dir in dirs) add(Expand(k.Replace("{install}", dir.TrimEnd('\\'))), "Known"); }
                    else add(Expand(k), "Known");
                }
            // v1.8: folders and registry keys named after the developer: AppData / LocalAppData / LocalLow\<developer>\<game> (only a guess, so only for
            // games nothing else found), and HKCU\Software\<developer>\<game> (a specific key, always)
            var guessFolders = !list.Any(x => !x.Kind.StartsWith("Steam"));
            var gameNames = new HashSet<string>(new[] { g.Name }.Concat(dirs.Select(d => System.IO.Path.GetFileName(d.TrimEnd('\\')))).Select(Library.Norm).Where(n => n.Length >= 3));
            foreach (var dv in devs.Where(d => !NotDevs.Contains(Library.Norm(d.Item1))).GroupBy(d => Library.Norm(d.Item1) + "|" + Library.Norm(d.Item2)).Select(x => x.First()))
            {
                var want = new HashSet<string>(gameNames); var pn = Library.Norm(dv.Item2); if (pn.Length >= 3 && !NotDevs.Contains(pn)) want.Add(pn);
                foreach (var baseDir in new[] { Roaming, Local, LocalLow })
                    foreach (var dev in Children(baseDir, Library.Norm(dv.Item1)))
                        foreach (var gd in Children(dev, want)) if (guessFolders) add(gd, "Developer folder");
                foreach (var key in RegChildren(@"Software", new HashSet<string> { Library.Norm(dv.Item1) }))
                    foreach (var gk in RegChildren(key, want)) add(Reg.Prefix + gk, "Registry");
            }
            foreach (var root in new[] { System.IO.Path.Combine(Docs, "My Games"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), Docs })   // v1.8: Documents\<game> too
            {
                if (!Directory.Exists(root)) continue;
                if (root == Docs && !guessFolders) continue;   // plain Documents\<game> is a guess too
                try { foreach (var d in Directory.GetDirectories(root)) if (norm.Length >= 3 && Library.Norm(System.IO.Path.GetFileName(d)) == norm) add(d, "Documents"); } catch { }
            }
            if (extra != null) foreach (var e in extra) add(e, g.Id != null && g.Id.StartsWith("vaultgame:") ? "Save folder" : "Added by you");   // v1.12: a game you added: its own save folders
            Tuple<string[], string[]> flt = Filters == null ? null : Filters(g.Id);   // v1.9: your include / leave-out patterns for this game
            if (flt != null) foreach (var r in list) { r.Include = flt.Item1; r.Exclude = flt.Item2; }
            foreach (var r in list) Measure(r);
            list.RemoveAll(r => (r.Kind == "Developer folder" || r.Kind == "Documents") && r.Bytes > (200L << 20));   // a big guessed folder is a cache, not saves
            return list;
        }

        // ---- v1.11: the save database
        public static Func<Game, ManGame> ManifestFor;
        public static bool ManifestConfig = true;   // also settings files (tagged "config"), not only saves
        // a database path that would back up far too much: a whole user / system / Steam folder, or the game's own install folder (or above it)
        static bool TooBroad(string p, List<string> installDirs, string steamRoot)
        {
            var norm = p.TrimEnd('\\') + "\\";
            Func<string, bool> same = x => !string.IsNullOrEmpty(x) && string.Equals(x.TrimEnd('\\') + "\\", norm, StringComparison.OrdinalIgnoreCase);
            Func<string, bool> above = x => !string.IsNullOrEmpty(x) && (x.TrimEnd('\\') + "\\").StartsWith(norm, StringComparison.OrdinalIgnoreCase);
            if (System.IO.Path.GetPathRoot(p).TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            if (installDirs.Any(above)) return true;
            Func<Environment.SpecialFolder, string> sf = Environment.GetFolderPath;
            var wide = new[] { sf(Environment.SpecialFolder.UserProfile), Docs, Roaming, Local, LocalLow, sf(Environment.SpecialFolder.CommonApplicationData), sf(Environment.SpecialFolder.Windows),
                Environment.GetEnvironmentVariable("PUBLIC"), System.IO.Path.GetDirectoryName(sf(Environment.SpecialFolder.UserProfile)), steamRoot,
                steamRoot == null ? null : System.IO.Path.Combine(steamRoot, "userdata"), System.IO.Path.Combine(Docs, "My Games"), System.IO.Path.Combine(sf(Environment.SpecialFolder.UserProfile), "Saved Games") };
            if (wide.Any(x => same(x) || above(x))) return true;
            if (steamRoot != null && System.Text.RegularExpressions.Regex.IsMatch(norm, "^" + System.Text.RegularExpressions.Regex.Escape(steamRoot.TrimEnd('\\') + @"\userdata\") + @"\d+\\$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;   // a whole Steam account
            return false;
        }

        // ---- v1.9: include / leave-out patterns per game ("*.sav", "Screenshots\*", "slot?.dat"; a pattern with no "\" matches the file name anywhere)
        public static Func<string, Tuple<string[], string[]>> Filters;
        public static bool Wanted(string rel, SaveRoot r)
        {
            if (r.Exclude != null && r.Exclude.Any(p => Match(p, rel))) return false;
            return r.Include == null || r.Include.Length == 0 || r.Include.Any(p => Match(p, rel));
        }
        static readonly Dictionary<string, System.Text.RegularExpressions.Regex> patCache = new Dictionary<string, System.Text.RegularExpressions.Regex>();
        public static bool Match(string pattern, string rel)
        {
            pattern = (pattern ?? "").Trim().Replace('/', '\\').TrimStart('\\'); if (pattern.Length == 0) return false;
            System.Text.RegularExpressions.Regex re;
            lock (patCache)
                if (!patCache.TryGetValue(pattern, out re))
                    patCache[pattern] = re = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return re.IsMatch(pattern.Contains("\\") ? rel : System.IO.Path.GetFileName(rel));
        }

        // ---- v1.8 helpers for developer-named folders and keys
        static readonly HashSet<string> NotDevs = new HashSet<string> { "", "unitytechnologies", "unity", "epicgames", "epicgamesinc", "unrealengine", "microsoft", "microsoftcorporation", "nvidia", "nvidiacorporation",
            "valve", "valvecorporation", "easyanticheat", "battleye", "sentrynativesdk", "theqtcompanyltd", "qt6", "gamesdk", "crashreportclient", "steam", "google", "googlellc", "mozilla", "intel", "intelcorporation", "amd", "advancedmicrodevicesinc" };
        static IEnumerable<Tuple<string, string>> ExeDevs(string dir)   // CompanyName / ProductName of the game's own .exe files (top two levels)
        {
            var res = new List<Tuple<string, string>>();
            try
            {
                var exes = Directory.GetFiles(dir, "*.exe").Concat(Directory.GetDirectories(dir).SelectMany(d => { try { return Directory.GetFiles(d, "*.exe"); } catch { return new string[0]; } })).Take(24);
                foreach (var e in exes)
                {
                    var n = System.IO.Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
                    if (n.Contains("crash") || n.Contains("unins") || n.Contains("setup") || n.Contains("redist") || n.Contains("anticheat") || n.Contains("launcher_helper")) continue;
                    try { var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(e); if (!string.IsNullOrWhiteSpace(v.CompanyName)) res.Add(Tuple.Create(v.CompanyName.Trim(), (v.ProductName ?? "").Trim())); } catch { }
                }
            }
            catch { }
            return res;
        }
        static IEnumerable<string> Children(string dir, string norm) { return Children(dir, new HashSet<string> { norm }); }
        static IEnumerable<string> Children(string dir, HashSet<string> norms)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => norms.Contains(Library.Norm(System.IO.Path.GetFileName(d)))).ToList() : new List<string>(); } catch { return new List<string>(); }
        }
        static IEnumerable<string> RegChildren(string key, HashSet<string> norms)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(key)) return k == null ? new List<string>() : k.GetSubKeyNames().Where(n => norms.Contains(Library.Norm(n))).Select(n => key + "\\" + n).ToList(); }
            catch { return new List<string>(); }
        }

        // every file a backup would include (skips caches, logs, secrets and huge files)
        public static IEnumerable<Tuple<string, string>> FilesOf(SaveRoot r)   // (full path, path inside the root)
        {
            if (Reg.Is(r.Path)) yield break;   // registry roots are exported, not copied
            if (File.Exists(r.Path)) { yield return Tuple.Create(r.Path, System.IO.Path.GetFileName(r.Path)); yield break; }
            var stack = new Stack<string>(); stack.Push(r.Path);
            while (stack.Count > 0)
            {
                var d = stack.Pop(); string[] files = new string[0], subs = new string[0];
                try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); } catch { }
                foreach (var f in files)
                {
                    if (IsFamilyStatus(f)) continue;   // v1.3: the Hallow apps' status.json files change all the time
                    if (SkipFiles.Contains(System.IO.Path.GetFileName(f)) || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) continue;
                    long len; try { len = new FileInfo(f).Length; } catch { continue; }
                    if (len > MaxFile) continue;
                    var rel = f.Substring(r.Path.Length).TrimStart('\\');
                    if (!Wanted(rel, r)) continue;   // v1.9
                    yield return Tuple.Create(f, rel);
                }
                foreach (var s in subs)
                {
                    try { if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    if (!SkipDirs.Contains(System.IO.Path.GetFileName(s))) stack.Push(s);
                }
            }
        }
        static void Measure(SaveRoot r)
        {
            long b = 0; int n = 0;
            if (Reg.Is(r.Path)) { r.RegText = Reg.Export(r.Path); r.Bytes = r.RegText == null ? 0 : r.RegText.Length * 2; r.Files = r.RegText == null ? 0 : 1; return; }
            foreach (var f in FilesOf(r)) { try { b += new FileInfo(f.Item1).Length; n++; } catch { } }
            r.Bytes = b; r.Files = n;
        }
        // v1.2: when a game last wrote any of its save files (UTC) - backups wait until this is ~10 s in the past
        public static DateTime NewestWrite(List<SaveRoot> roots)
        {
            var t = DateTime.MinValue;
            foreach (var r in roots) foreach (var f in FilesOf(r)) { try { var w = File.GetLastWriteTimeUtc(f.Item1); if (w > t) t = w; } catch { } }
            return t;
        }
        // v1.2: every file in the save folders right now: full path -> (size, last write)
        public static Dictionary<string, FileStat> Contents(List<SaveRoot> roots)
        {
            var d = new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots) foreach (var f in FilesOf(r)) { try { var fi = new FileInfo(f.Item1); d[f.Item1] = new FileStat { Bytes = fi.Length, When = fi.LastWriteTime }; } catch { } }
            foreach (var r in roots.Where(x => Reg.Is(x.Path) && x.RegText != null)) d[System.IO.Path.Combine(r.Path, RegFile)] = new FileStat { Bytes = RegBytes(r.RegText).Length, When = RegTime };
            return d;
        }
        // changes when any file is added, removed, resized or saved again
        public static string Fingerprint(List<SaveRoot> roots)
        {
            long ticks = 0, bytes = 0; int n = 0;
            foreach (var r in roots) foreach (var f in FilesOf(r)) { try { var fi = new FileInfo(f.Item1); ticks = Math.Max(ticks, fi.LastWriteTimeUtc.Ticks); bytes += fi.Length; n++; } catch { } }
            var sig = n + "|" + bytes + "|" + ticks;
            var reg = roots.Where(r => Reg.Is(r.Path)).Select(r => r.RegText ?? "").ToList();   // v1.8: registry values (only games that have them get the extra part)
            if (reg.Count > 0) using (var sha = System.Security.Cryptography.SHA1.Create()) sig += "|reg:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", reg)))).Replace("-", "").Substring(0, 16);
            return sig;
        }
        public const string RegFile = "registry.reg";
        public static readonly DateTime RegTime = new DateTime(2000, 1, 1);   // registry exports carry no time: compared by size
        public static byte[] RegBytes(string text) { return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(); }   // .reg files are UTF-16 with a BOM
    }

    [DataContract] public class BackupInfo
    {
        [DataMember] public string Game, GameId, Created, Reason;
        [DataMember] public List<string> Roots = new List<string>();   // index in the list = folder number inside the zip
        [DataMember] public List<bool> IsFile = new List<bool>();      // that root is a single file, not a folder
        [DataMember(EmitDefaultValue = false)] public List<string> Files;   // v1.12: "<entry>\t<bytes>" for every file in the zip (null in older backups)
        public string File; public long Bytes; public bool Pinned;   // v1.7: pinned versions are never pruned
        public DateTime When { get { DateTime d; return DateTime.TryParse(Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d.ToLocalTime() : System.IO.File.GetCreationTime(File); } }
    }

    public static class Backups
    {
        public static string SafeName(string name) { var s = new string(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' '); return s.Length == 0 ? "Game" : s; }   // Windows drops trailing dots
        public static string DefaultRoot()
        {
            if (Link.TestDir != null) return System.IO.Path.Combine(Link.TestDir, "Hallow Vault");   // v1.12: a test copy never writes into the real vault
            // a second drive keeps backups safe if the system drive dies
            try
            {
                var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                var other = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase) && d.AvailableFreeSpace > (20L << 30))
                    .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
                if (other != null) return System.IO.Path.Combine(other.RootDirectory.FullName, "Hallow Vault");
            }
            catch { }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Hallow Vault");
        }
        public static CompressionLevel Level = CompressionLevel.Optimal;   // v1.9: Settings > Compression
        public static string GameDir(string root, Game g) { return System.IO.Path.Combine(root, string.IsNullOrEmpty(g.Folder) ? SafeName(g.Name) : g.Folder); }   // v1.12: a game you added keeps its folder when renamed

        // Makes one zip: <vault>\<game>\<date time>.zip with every save folder inside (folder 0, 1, ...) and a vault.json describing it.
        public static BackupInfo Make(string root, Game g, List<SaveRoot> roots, string reason, out int skipped)
        {
            skipped = 0;
            var dir = GameDir(root, g); Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".zip");
            var tmp = file + ".part";
            var info = new BackupInfo { Game = g.Name, GameId = g.Id, Created = DateTime.Now.ToString("o"), Reason = reason, Roots = roots.Select(r => r.Path).ToList(), IsFile = roots.Select(r => File.Exists(r.Path)).ToList(), File = file, Files = new List<string>() };
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    if (Reg.Is(roots[i].Path))   // v1.8: the key as a .reg file
                    {
                        var text = roots[i].RegText ?? Reg.Export(roots[i].Path); if (text == null) { skipped++; continue; }
                        var re = zip.CreateEntry(i + "/" + Saves.RegFile, Level); re.LastWriteTime = Saves.RegTime;
                        var bytes = Saves.RegBytes(text); using (var dst = re.Open()) dst.Write(bytes, 0, bytes.Length);
                        info.Files.Add(i + "/" + Saves.RegFile + "\t" + bytes.Length);
                        continue;
                    }
                    foreach (var f in Saves.FilesOf(roots[i]))
                    {
                        try
                        {
                            using (var src = new FileStream(f.Item1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                var name = i + "/" + f.Item2.Replace('\\', '/');
                                var e = zip.CreateEntry(name, Level);
                                e.LastWriteTime = File.GetLastWriteTime(f.Item1);
                                long n = 0; var buf = new byte[81920]; int k;
                                using (var dst = e.Open()) while ((k = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, k); n += k; }
                                info.Files.Add(name + "\t" + n);   // v1.12: the file list, for Check backups
                            }
                        }
                        catch { skipped++; }
                    }
                }
                var m = zip.CreateEntry("vault.json");
                using (var w = m.Open()) new DataContractJsonSerializer(typeof(BackupInfo)).WriteObject(w, info);
            }
            File.Move(tmp, file);
            info.Bytes = new FileInfo(file).Length;
            return info;
        }

        public static List<BackupInfo> List(string root, Game g)
        {
            var dir = GameDir(root, g); var res = new List<BackupInfo>();
            if (!Directory.Exists(dir)) return res;
            var pins = Pins(dir);
            foreach (var f in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f))
            {
                BackupInfo b = null;
                try
                {
                    using (var z = ZipFile.OpenRead(f)) { var m = z.GetEntry("vault.json"); if (m != null) using (var s = m.Open()) b = (BackupInfo)new DataContractJsonSerializer(typeof(BackupInfo)).ReadObject(s); }
                }
                catch { }
                if (b == null) b = new BackupInfo { Game = g.Name, Reason = "?" };
                b.File = f; b.Bytes = new FileInfo(f).Length; b.Pinned = pins.Contains(System.IO.Path.GetFileName(f)); res.Add(b);
            }
            return res;
        }

        // keeps the newest `keep` backups of a game (pinned ones are kept on top and don't count); only ever deletes Vault's own zips in that game's folder
        public static void Prune(string root, Game g, int keep)
        {
            var dir = GameDir(root, g); if (!Directory.Exists(dir)) return;
            var pins = Pins(dir);
            foreach (var f in ZipsIn(dir).Where(f => !pins.Contains(f.Name)).Skip(Math.Max(1, keep)))
                try { f.Delete(); } catch { }
        }

        // ---- v1.7: pins (a pins.txt next to the zips, one file name per line - it travels with the vault and the second copy)
        public const string PinsFile = "pins.txt";
        public static HashSet<string> Pins(string dir)
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { var f = System.IO.Path.Combine(dir, PinsFile); if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) if (l.Trim().Length > 0) h.Add(l.Trim()); } catch { }
            return h;
        }
        public static void SetPin(string zip, bool on)
        {
            var dir = System.IO.Path.GetDirectoryName(zip); var pins = Pins(dir); var name = System.IO.Path.GetFileName(zip);
            if (on) pins.Add(name); else pins.Remove(name);
            pins.RemoveWhere(n => !File.Exists(System.IO.Path.Combine(dir, n)));   // forget pins of deleted zips
            var f = System.IO.Path.Combine(dir, PinsFile);
            if (pins.Count == 0) { if (File.Exists(f)) File.Delete(f); } else File.WriteAllLines(f, pins.OrderBy(x => x, StringComparer.Ordinal));
        }
        public static BackupInfo Read(string zip)
        {
            try
            {
                using (var z = ZipFile.OpenRead(zip)) { var m = z.GetEntry("vault.json"); if (m == null) return null; using (var s = m.Open()) { var b = (BackupInfo)new DataContractJsonSerializer(typeof(BackupInfo)).ReadObject(s); b.File = zip; b.Bytes = new FileInfo(zip).Length; return b; } }
            }
            catch { return null; }
        }

        // ---- v1.7: save alarm - saves that suddenly vanish, empty or shrink under half of the last backup aren't backed up over
        public static string Alarm(List<SaveRoot> now, BackupInfo last)
        {
            if (last == null) return null;
            long before; try { before = Contents(last).Values.Sum(x => x.Bytes); } catch { return null; }
            if (before == 0) return null;
            if (now.Count == 0) return "the save folder is gone";
            long bytes = now.Sum(r => r.Bytes); int files = now.Sum(r => r.Files);
            if (files == 0) return "the save folder is empty";
            if (before >= 16 * 1024 && bytes * 2 < before) return "the saves shrank from " + VaultSettings.Size(before) + " to " + VaultSettings.Size(bytes);
            return null;
        }

        // ---- v1.3: size limits and a second copy. Only ever touches Vault's own zips (named by date and time) in game folders.
        const string ZipPattern = "????-??-?? ??.??.??.zip";
        public static List<FileInfo> ZipsIn(string dir) { return Zips(dir); }
        static List<FileInfo> Zips(string dir)   // newest first (the names sort by time)
        {
            try { return new DirectoryInfo(dir).GetFiles(ZipPattern).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList(); } catch { return new List<FileInfo>(); }
        }
        // keeps each game under maxGame bytes and the whole vault under maxTotal (0 = no limit): oldest versions go first,
        // and a game's newest backup is never removed. Returns how many backups were removed.
        public static int PruneSize(string root, long maxGame, long maxTotal)
        {
            if ((maxGame <= 0 && maxTotal <= 0) || !Directory.Exists(root)) return 0;
            int n = 0; long total = 0; var spare = new List<FileInfo>();
            foreach (var dir in Directory.GetDirectories(root))
            {
                var fs = Zips(dir); long sum = fs.Sum(f => f.Length); var pins = Pins(dir);   // pinned ones count, but are never removed
                for (int i = fs.Count - 1; i >= 1 && maxGame > 0 && sum > maxGame; i--)
                {
                    if (pins.Contains(fs[i].Name)) continue;
                    try { var len = fs[i].Length; fs[i].Delete(); sum -= len; fs.RemoveAt(i); n++; } catch { }
                }
                total += sum; spare.AddRange(fs.Skip(1).Where(f => !pins.Contains(f.Name)));
            }
            if (maxTotal > 0)
                foreach (var f in spare.OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (total <= maxTotal) break;
                    try { var len = f.Length; f.Delete(); total -= len; n++; } catch { }
                }
            return n;
        }
        // the second copy: copies every backup the copy folder doesn't have yet, and removes copies older than the
        // oldest backup still in the vault (pruned there). A game folder with no backups in the vault is left alone.
        public static int Mirror(string root, string copyRoot)
        {
            if (string.IsNullOrEmpty(copyRoot) || !Directory.Exists(root)) return 0;
            int copied = 0;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var have = Zips(dir); if (have.Count == 0) continue;
                var dst = System.IO.Path.Combine(copyRoot, System.IO.Path.GetFileName(dir)); Directory.CreateDirectory(dst);
                foreach (var f in have)
                {
                    var to = System.IO.Path.Combine(dst, f.Name); if (File.Exists(to)) continue;
                    var part = to + ".part"; File.Copy(f.FullName, part, true); File.Move(part, to); copied++;
                }
                var pf = System.IO.Path.Combine(dir, PinsFile); var pt = System.IO.Path.Combine(dst, PinsFile);   // v1.7: pins go along
                if (File.Exists(pf)) File.Copy(pf, pt, true); else if (File.Exists(pt)) File.Delete(pt);
                var oldest = have[have.Count - 1].Name;
                foreach (var c in Zips(dst)) if (string.CompareOrdinal(c.Name, oldest) < 0) try { c.Delete(); } catch { }
            }
            return copied;
        }
        // ---- v1.10: is the second copy keeping up? Behind = vault backups the copy doesn't have; conflicts = copies a cloud app made
        // when two versions of a file met ("<backup> (1).zip", "<backup>-DESKTOP-PC.zip", "<backup> (conflicted copy ...).zip", "pins (1).txt").
        public class CopyInfo { public bool Reachable; public int Behind; public List<string> Conflicts = new List<string>(); public DateTime? Last; public string Error; }
        static readonly System.Text.RegularExpressions.Regex ConflictName = new System.Text.RegularExpressions.Regex(@"^(\d{4}-\d\d-\d\d \d\d\.\d\d\.\d\d.+\.zip|pins.+\.txt)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public static CopyInfo CopyState(string root, string copyRoot)
        {
            var c = new CopyInfo();
            try
            {
                var drive = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(copyRoot));
                if (!Directory.Exists(drive)) { c.Error = drive.TrimEnd('\\') + " isn't there (unplugged?)"; return c; }
                c.Reachable = true;
                foreach (var dir in Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0])
                {
                    var have = Zips(dir); if (have.Count == 0) continue;
                    var dst = System.IO.Path.Combine(copyRoot, System.IO.Path.GetFileName(dir));
                    c.Behind += have.Count(f => !File.Exists(System.IO.Path.Combine(dst, f.Name)));
                }
                if (Directory.Exists(copyRoot))
                    foreach (var dst in Directory.GetDirectories(copyRoot))
                    {
                        foreach (var f in Directory.GetFiles(dst)) if (ConflictName.IsMatch(System.IO.Path.GetFileName(f)) && !System.IO.Path.GetFileName(f).Equals(PinsFile, StringComparison.OrdinalIgnoreCase)) c.Conflicts.Add(f);
                        var z = Zips(dst).FirstOrDefault(); if (z != null && (c.Last == null || z.LastWriteTime > c.Last)) c.Last = z.LastWriteTime;
                    }
            }
            catch (Exception ex) { c.Error = ex.Message; c.Reachable = false; }
            return c;
        }
        // a copy folder must be somewhere else than the vault (not the same folder, not inside it, not around it)
        public static bool Overlaps(string a, string b)
        {
            try
            {
                a = System.IO.Path.GetFullPath(a).TrimEnd('\\') + "\\"; b = System.IO.Path.GetFullPath(b).TrimEnd('\\') + "\\";
                return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        // v1.2: the files inside a backup, by the full path they came from: path -> (size, last write)
        public static Dictionary<string, FileStat> Contents(BackupInfo b)
        {
            var d = new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase);
            using (var z = ZipFile.OpenRead(b.File))
                foreach (var e in z.Entries)
                {
                    string dest = Where(b, e); if (dest == null) continue;
                    d[dest] = new FileStat { Bytes = e.Length, When = e.LastWriteTime.LocalDateTime };
                }
            return d;
        }
        // where a zip entry goes back to (null for vault.json, folders and anything that would land outside its save folder)
        static string Where(BackupInfo b, ZipArchiveEntry e)
        {
            if (e.FullName == "vault.json" || e.FullName.EndsWith("/")) return null;
            var slash = e.FullName.IndexOf('/'); int idx;
            if (slash < 0 || !int.TryParse(e.FullName.Substring(0, slash), out idx) || idx >= b.Roots.Count) return null;
            var rootPath = b.Roots[idx]; var rel = e.FullName.Substring(slash + 1).Replace('/', '\\');
            if (rel.Contains("..")) return null;   // never write outside the save folder
            var isFile = b.IsFile != null && idx < b.IsFile.Count && b.IsFile[idx];
            return isFile ? rootPath : System.IO.Path.Combine(rootPath, rel);
        }

        // v1.2: what changed from `older` to `newer` (sizes in bytes; "changed" = same size, saved again)
        public static List<FileChange> Diff(Dictionary<string, FileStat> older, Dictionary<string, FileStat> newer, out int same)
        {
            var res = new List<FileChange>(); same = 0;
            foreach (var kv in newer)
            {
                FileStat o;
                if (!older.TryGetValue(kv.Key, out o)) res.Add(new FileChange { Path = kv.Key, Kind = "added", New = kv.Value.Bytes });
                else if (o.Bytes != kv.Value.Bytes) res.Add(new FileChange { Path = kv.Key, Kind = kv.Value.Bytes > o.Bytes ? "bigger" : "smaller", Old = o.Bytes, New = kv.Value.Bytes });
                else if (Math.Abs((kv.Value.When - o.When).TotalSeconds) > 2.5) res.Add(new FileChange { Path = kv.Key, Kind = "changed", Old = o.Bytes, New = kv.Value.Bytes });   // zip times are 2-second steps
                else same++;
            }
            foreach (var kv in older) if (!newer.ContainsKey(kv.Key)) res.Add(new FileChange { Path = kv.Key, Kind = "removed", Old = kv.Value.Bytes });
            var order = new[] { "added", "removed", "bigger", "smaller", "changed" };
            return res.OrderBy(c => Array.IndexOf(order, c.Kind)).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Puts a backup's files back where they came from (overwriting those files; nothing else in the folders is deleted).
        public static int Restore(BackupInfo b) { return Restore(b, null); }
        // v1.7: `map` moves each file's path (a backup from another PC); null skips that file
        public static int Restore(BackupInfo b, Func<string, string> map)
        {
            int n = 0;
            using (var z = ZipFile.OpenRead(b.File))
                foreach (var e in z.Entries)
                {
                    var dest = Where(b, e); if (dest != null && map != null) dest = map(dest);
                    if (dest == null) continue;
                    var rk = System.IO.Path.GetDirectoryName(dest);
                    if (Reg.Is(rk) && System.IO.Path.GetFileName(dest).Equals(Saves.RegFile, StringComparison.OrdinalIgnoreCase))   // v1.8: registry values back into that key only
                    {
                        string text; using (var s = e.Open()) using (var r = new StreamReader(s, Encoding.Unicode, true)) text = r.ReadToEnd();
                        Reg.Import(rk, text); n++; continue;
                    }
                    if (Reg.Is(dest)) continue;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true); n++;
                }
            return n;
        }
    }
}
