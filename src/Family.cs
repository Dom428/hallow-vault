// Hallow Vault 1.16 - Hallow family backup: one place that backs up the whole Hallow family.
//   - every Hallow app's settings folders (%APPDATA%\Hallow*, %LOCALAPPDATA%\Hallow*) - small, plain files; caches, WebView2 profiles,
//     logs, crash dumps, temp files, programs and secrets (app keys, Keys' Secret Key file, VPN credentials) are left out;
//   - Hallow Keys' encrypted backup files (*.hkbackup, from the folder Keys backs up to) and Hallow Authenticator's encrypted backups
//     (%APPDATA%\HallowAuthenticator\backups and \autobackups) as OPAQUE files: Vault copies their bytes and checks them by SHA-256,
//     and never opens, parses or decrypts them (it never reads Keys' vault file at all);
//   - optionally Hallow Clips favourites (copied as they are, not versioned).
// Versions are zip files ("Hallow family yyyy-MM-dd HHmmss.zip" + family.json listing every file with its SHA-256) in
// <destination>\Hallow family backup\<PC name>\ on one or more destinations (another drive, a USB drive, a NAS share on a private
// address). Old versions go to the Recycle Bin (or, where Windows has none - USB drives, network shares - to a "Removed versions"
// folder next to them), never deleted for good.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace HallowVault
{
    [DataContract]
    public class FamilyDest
    {
        [DataMember] public string Path = "";
        [DataMember] public string LastSuccess = "";     // when it last held the newest version (written, or checked and already there)
        [DataMember] public string LastError = "";       // the last failure, in plain words ("" = the last try worked)
        [DataMember] public string LastErrorKind = "";   // blocked (Ransomware protection) / denied / unreachable / full / error
        [DataMember] public string LastTry = "";
        [OnDeserializing] void D(StreamingContext c) { Path = LastSuccess = LastError = LastErrorKind = LastTry = ""; }
    }

    [DataContract]
    public class FamilySettings
    {
        [DataMember] public bool On;                                    // off until you add a destination
        [DataMember] public string OnSince = "";                         // for the "hasn't run in 3 days" note when it never worked
        [DataMember] public List<FamilyDest> Dests = new List<FamilyDest>();
        [DataMember] public int Keep = 14;                               // versions kept per destination (7 / 14 / 30 / 60)
        [DataMember] public bool Clips;                                  // also copy Hallow Clips favourites (off by default - videos are big)
        [DataMember] public string LastRun = "";                         // the last run that left at least one destination up to date
        [DataMember] public string LastBackup = "";                      // when the newest version was made
        [DataMember] public string LastVersion = "";                     // its file name
        [DataMember] public string LastSig = "";                         // fingerprint (names, sizes, times) of what it holds
        [DataMember] public string LastNote = "";                        // the day of the last "hasn't run" note (at most one a day)
        [DataMember] public string LastError = "";                       // the last run's problem ("" = fine)
        [DataMember] public int LastFiles, LastApps;
        [DataMember] public long LastBytes;
        [OnDeserializing] void D(StreamingContext c) { On = false; OnSince = ""; Dests = new List<FamilyDest>(); Keep = 14; Clips = false; LastRun = LastBackup = LastVersion = LastSig = LastNote = LastError = ""; }
        [OnDeserialized] void A(StreamingContext c) { if (Dests == null) Dests = new List<FamilyDest>(); if (Keep < 1) Keep = 14; }
    }

    [DataContract]
    public class FamilyEntry
    {
        [DataMember] public string Entry = "", App = "", Kind = "", Source = "", Sha256 = "", Modified = "";
        [DataMember] public long Bytes;
        [DataMember] public bool Opaque;
    }

    [DataContract]
    public class FamilyManifest
    {
        [DataMember] public string Made = "", Pc = "", Version = "";
        [DataMember] public List<FamilyEntry> Items = new List<FamilyEntry>();
        [DataMember] public List<FamilyEntry> Clips = new List<FamilyEntry>();   // copied beside the versions (not in the zip)
        [DataMember] public List<string> Notes = new List<string>();
    }

    public static class Family
    {
        public const string Folder = "Hallow family backup";
        public const string Prefix = "Hallow family ";
        public const string ManifestName = "family.json";
        public const string ClipsFolder = "Clips favourites";
        public const string RemovedFolder = "Removed versions";
        public const long MaxSettingsFile = 16L << 20;
        public const int StaleDays = 3;
        public const double MinGapHours = 3;   // after changes: at most one new version every 3 hours (daily otherwise)

        // ---- tests: a fake user profile (Roaming / Local under it), the PC name, Ransomware protection's state and the Recycle Bin
        public static string Home;
        public static string PcName = Environment.MachineName;
        public static Func<bool?> CfaState = ReadCfa;
        public static Func<string, bool> Recycler = RecycleFile;     // true = in the Recycle Bin
        public static Func<string, string> CloseApp = DefaultClose;  // null = the app is closed now, else why not
        public static Func<string, bool> HasBin = HasRecycleBin;     // does this file's drive have a Recycle Bin?
        static string Roaming { get { return Home != null ? System.IO.Path.Combine(Home, @"AppData\Roaming") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string Local { get { return Home != null ? System.IO.Path.Combine(Home, @"AppData\Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string RoamingDir { get { return Roaming; } }
        public static string LocalDir { get { return Local; } }

        public class Item { public string App, Kind, Path, Entry, Source; public long Bytes; public DateTime When; public bool Opaque; }

        // ================================================================ what gets backed up
        // "Hallow" (the family theme), HallowHub, "Hallow Shield"...; never HallowDev, old "HallowBrowser-backup-1.5" copies or "hallowed-..."
        static readonly Regex AppFolder = new Regex(@"^Hallow( ?[A-Z][A-Za-z0-9]*)?$");
        static readonly HashSet<string> NotApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HallowDev", "HallowAuthenticator" };
        // never copied, at any depth: caches, browser (WebView2) data, logs, crash dumps, temp files, downloads, recordings
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Cache", "Caches", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "Service Worker", "CacheStorage", "blob_storage", "IndexedDB", "Session Storage", "Local Storage", "EBWebView", "WebView2", "Crashpad",
            "logs", "log", "crashes", "crash", "CrashDumps", "dumps", "temp", "tmp", "buffer", "ffmpeg", "downloads", "updates", "update", "thumbs", "thumbnails" };
        // per app (top-level folders): Vault's own save database cache and family-backup work folders, Hub's store pictures / metadata cache,
        static readonly Dictionary<string, string[]> SkipPerApp = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
            { "HallowVault", new[] { "manifest", "family-*" } }, { "HallowHub", new[] { "meta" } },
            { "HallowBrowser", new[] { "Shield", "ShieldLoaded", "newtab", "Profile", "Twitch" } }, { "Hallow Shield", new[] { "chrome" } } };   // Browser: the built-in Shield extension copies, its new-tab page cache and WebView2 profiles; Shield: the built extension
        static readonly HashSet<string> SkipExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".log", ".tmp", ".temp", ".dmp", ".mdmp", ".part", ".partial", ".crdownload", ".lock", ".pid", ".flag", ".exe", ".dll", ".msi", ".ts", ".etl", ".pdb" };
        // secrets and live state: Mixer's local API key, Keys' Secret Key file and live vault (its backups are copied instead), VPN credentials
        static readonly Regex SkipName = new Regex(@"^(status\.json(\.tmp)?|api\.json|account\.dat|secret-key.*|.*\.hkvault.*|LOCK|LOG|LOG\.old|running\.flag)$", RegexOptions.IgnoreCase);
        // encrypted files: stored without compression and only ever handled as bytes
        static readonly HashSet<string> OpaqueExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".hkbackup", ".hallowauth", ".hfvault", ".bin", ".dat" };
        // Hallow Keys: only its settings from its own folder (never the vault, the Secret Key file or anything else in it)
        static readonly HashSet<string> KeysAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "settings.json", "device-id.txt" };

        public static string AppOf(string folder)   // "HallowHub" -> "Hub", "Hallow Shield" -> "Shield", "Hallow" -> "Hallow"
        {
            var a = folder.Substring(6).Trim(); return a.Length == 0 ? "Hallow" : a;
        }
        public static string Label(string kind, string app)
        {
            if (kind == "keys") return "Hallow Keys encrypted backups";
            if (kind == "authenticator") return "Hallow Authenticator encrypted backups";
            if (kind == "clips") return "Hallow Clips favourites";
            return app == "Hallow" ? "Hallow family look (theme.json)" : "Hallow " + app + " settings";
        }

        // where Hallow Keys writes its automatic encrypted backups: its settings.json says (plain settings; the vault is never read)
        public static string KeysBackupFolder()
        {
            var keysDir = System.IO.Path.Combine(Roaming, "HallowKeys"); var def = System.IO.Path.Combine(keysDir, "backups");
            try
            {
                var f = System.IO.Path.Combine(keysDir, "settings.json"); if (!File.Exists(f)) return def;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                object v; if (d != null && d.TryGetValue("AutoBackupFolder", out v) && v is string && ((string)v).Trim().Length > 0) return ((string)v).Trim();
            }
            catch { }
            return def;
        }
        public static string AuthDir { get { return System.IO.Path.Combine(Roaming, "HallowAuthenticator"); } }
        static readonly Regex KeysBackupName = new Regex(@"^Hallow Keys backup .+\.hkbackup$", RegexOptions.IgnoreCase);
        static readonly Regex AuthBackupName = new Regex(@"^(backup|auto)-.+\.hallowauth$", RegexOptions.IgnoreCase);

        public static List<Item> Collect(bool clips, List<string> notes)
        {
            var items = new List<Item>(); if (notes == null) notes = new List<string>();
            foreach (var place in new[] { Tuple.Create("roaming", Roaming, "%APPDATA%"), Tuple.Create("local", Local, "%LOCALAPPDATA%") })
            {
                string[] dirs; try { dirs = Directory.Exists(place.Item2) ? Directory.GetDirectories(place.Item2) : new string[0]; } catch { dirs = new string[0]; }
                foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = System.IO.Path.GetFileName(d);
                    if (!AppFolder.IsMatch(name) || NotApps.Contains(name) || IsLink(d)) continue;
                    var app = AppOf(name);
                    if (name.Equals("HallowKeys", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var k in KeysAllow) { var f = System.IO.Path.Combine(d, k); if (File.Exists(f)) AddFile(items, notes, f, app, "settings", place.Item1 + "/" + name + "/" + k, place.Item3 + "\\" + name + "\\" + k); }
                        continue;
                    }
                    string[] extra; SkipPerApp.TryGetValue(name, out extra);
                    Walk(items, notes, d, d, app, place.Item1 + "/" + name, place.Item3 + "\\" + name, extra ?? new string[0]);
                }
            }
            // Hallow Keys' encrypted backups (only Keys' own dated backup files in that folder)
            var kb = KeysBackupFolder();
            try
            {
                if (Directory.Exists(kb))
                    foreach (var f in Directory.GetFiles(kb).Where(x => KeysBackupName.IsMatch(System.IO.Path.GetFileName(x))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        AddFile(items, notes, f, "Keys", "keys", "keys/" + System.IO.Path.GetFileName(f), f, true);
            }
            catch (Exception ex) { notes.Add("Hallow Keys' backup folder couldn't be read: " + ex.Message); }
            // Hallow Authenticator's encrypted backups (kept by Hallow Remote)
            foreach (var sub in new[] { "backups", "autobackups" })
            {
                var ad = System.IO.Path.Combine(AuthDir, sub);
                try
                {
                    if (Directory.Exists(ad))
                        foreach (var f in Directory.GetFiles(ad).Where(x => AuthBackupName.IsMatch(System.IO.Path.GetFileName(x))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            AddFile(items, notes, f, "Authenticator", "authenticator", "authenticator/" + sub + "/" + System.IO.Path.GetFileName(f), "%APPDATA%\\HallowAuthenticator\\" + sub + "\\" + System.IO.Path.GetFileName(f), true);
                }
                catch (Exception ex) { notes.Add("Hallow Authenticator's " + sub + " folder couldn't be read: " + ex.Message); }
            }
            if (clips) items.AddRange(ClipFavourites(notes));
            return items;
        }
        static bool IsLink(string d) { try { return (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0; } catch { return true; } }
        // a WebView2 / Chromium user data folder (a browser profile): never backed up
        static bool IsBrowserData(string d)
        {
            var n = System.IO.Path.GetFileName(d);
            if (n.EndsWith(".WebView2", StringComparison.OrdinalIgnoreCase)) return true;
            try { return File.Exists(System.IO.Path.Combine(d, "Local State")) || Directory.Exists(System.IO.Path.Combine(d, "EBWebView")) || File.Exists(System.IO.Path.Combine(d, "Preferences")) && File.Exists(System.IO.Path.Combine(d, "Network Persistent State")); }
            catch { return false; }
        }
        static bool Like(string name, string pat)
        {
            return pat.EndsWith("*") ? name.StartsWith(pat.TrimEnd('*'), StringComparison.OrdinalIgnoreCase) : name.Equals(pat, StringComparison.OrdinalIgnoreCase);
        }
        public static bool SkipFile(string name)
        {
            return SkipName.IsMatch(name) || SkipExt.Contains(System.IO.Path.GetExtension(name)) || name.EndsWith("~") || name.StartsWith("~$");
        }
        static void Walk(List<Item> items, List<string> notes, string top, string dir, string app, string entry, string source, string[] extra)
        {
            string[] files, subs;
            try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); } catch (Exception ex) { notes.Add("couldn't read " + source + ": " + ex.Message); return; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var n = System.IO.Path.GetFileName(f); if (SkipFile(n)) continue;
                AddFile(items, notes, f, app, "settings", entry + "/" + n, source + "\\" + n);
            }
            foreach (var s in subs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var n = System.IO.Path.GetFileName(s);
                if (SkipDirs.Contains(n) || (dir == top && extra.Any(p => Like(n, p))) || IsLink(s) || IsBrowserData(s)) continue;
                Walk(items, notes, top, s, app, entry + "/" + n, source + "\\" + n, extra);
            }
        }
        static void AddFile(List<Item> items, List<string> notes, string f, string app, string kind, string entry, string source, bool opaque = false)
        {
            try
            {
                var fi = new FileInfo(f); if (!fi.Exists) return;
                if (kind == "settings" && fi.Length > MaxSettingsFile) { notes.Add("left out " + source + " (" + VaultSettings.Size(fi.Length) + " - too big for settings)"); return; }
                items.Add(new Item { App = app, Kind = kind, Path = f, Entry = entry, Source = source, Bytes = fi.Length, When = fi.LastWriteTime, Opaque = opaque || OpaqueExt.Contains(fi.Extension) });
            }
            catch (Exception ex) { notes.Add("couldn't read " + source + ": " + ex.Message); }
        }
        // Hallow Clips favourites: clips.json lists them (full paths); only files that are still there
        static List<Item> ClipFavourites(List<string> notes)
        {
            var o = new List<Item>();
            try
            {
                var f = System.IO.Path.Combine(Local, @"HallowClips\clips.json"); if (!File.Exists(f)) return o;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                object v; if (d == null || !d.TryGetValue("Favorites", out v) || !(v is object[])) return o;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ((object[])v).OfType<string>())
                {
                    if (!File.Exists(p)) continue;
                    var n = System.IO.Path.GetFileName(p); var key = n; int i = 2;
                    while (!seen.Add(key)) key = System.IO.Path.GetFileNameWithoutExtension(n) + " (" + i++ + ")" + System.IO.Path.GetExtension(n);
                    var fi = new FileInfo(p);
                    o.Add(new Item { App = "Clips", Kind = "clips", Path = p, Entry = "clips/" + key, Source = p, Bytes = fi.Length, When = fi.LastWriteTime, Opaque = true });
                }
            }
            catch (Exception ex) { notes.Add("Hallow Clips' favourites couldn't be read: " + ex.Message); }
            return o;
        }

        // names, sizes and times of everything - a change means a new version is due
        public static string Signature(List<Item> items)
        {
            var sb = new StringBuilder();
            foreach (var i in items.OrderBy(x => x.Entry, StringComparer.Ordinal)) sb.Append(i.Entry).Append('|').Append(i.Bytes).Append('|').Append(i.When.ToUniversalTime().Ticks).Append('\n');
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        // bytes as they are, even while the app has the file open (opaque files are only ever read this way)
        static byte[] ReadBytes(string f)
        {
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var b = new byte[fs.Length]; int got = 0, n;
                while (got < b.Length && (n = fs.Read(b, got, b.Length - got)) > 0) got += n;
                if (got != b.Length) Array.Resize(ref b, got);
                return b;
            }
        }
        public static string Sha(byte[] b) { using (var s = SHA256.Create()) return Hex(s.ComputeHash(b)); }

        // ================================================================ one version: a zip with family.json
        public static string VersionName(DateTime when) { return Prefix + when.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip"; }
        public static DateTime? WhenOf(string file)
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(file); DateTime t;
            if (!n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return DateTime.TryParseExact(n.Substring(Prefix.Length), "yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t) ? t : (DateTime?)null;
        }
        public static FamilyManifest MakeZip(List<Item> items, string zipPath, List<string> notes)
        {
            var man = new FamilyManifest { Made = DateTime.Now.ToString("o"), Pc = PcName, Version = Program.Version };
            var tmp = zipPath + ".part";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(zipPath));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var it in items.Where(x => x.Kind != "clips"))
                {
                    byte[] data;
                    try { data = ReadBytes(it.Path); }
                    catch (Exception ex) { var m = "skipped " + it.Source + " (" + ex.Message.TrimEnd('.') + ")"; man.Notes.Add(m); if (notes != null) notes.Add(m); continue; }
                    var e = z.CreateEntry(it.Entry, it.Opaque ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    try { e.LastWriteTime = it.When; } catch { }
                    using (var s = e.Open()) s.Write(data, 0, data.Length);
                    man.Items.Add(new FamilyEntry { Entry = it.Entry, App = it.App, Kind = it.Kind, Source = it.Source, Bytes = data.Length, Sha256 = Sha(data), Modified = it.When.ToString("o"), Opaque = it.Opaque });
                }
                foreach (var it in items.Where(x => x.Kind == "clips"))
                    man.Clips.Add(new FamilyEntry { Entry = it.Entry, App = "Clips", Kind = "clips", Source = it.Source, Bytes = it.Bytes, Modified = it.When.ToString("o"), Opaque = true });
                var me = z.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using (var s = me.Open()) new DataContractJsonSerializer(typeof(FamilyManifest)).WriteObject(s, man);
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return man;
        }
        public static FamilyManifest ReadManifest(string zip)
        {
            try
            {
                using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var e = z.GetEntry(ManifestName); if (e == null) return null;
                    using (var s = e.Open()) return (FamilyManifest)new DataContractJsonSerializer(typeof(FamilyManifest)).ReadObject(s);
                }
            }
            catch { return null; }
        }

        // ================================================================ destinations
        public static string DestDir(string dest) { return System.IO.Path.Combine(dest, Folder, Safe(PcName)); }
        static string Safe(string n) { foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n.Length == 0 ? "PC" : n; }
        public class Snapshot { public string File, Pc; public DateTime When; public long Bytes; public bool ThisPc; }
        // every version at a destination, newest first (this PC's and any other family PC's that backs up there)
        public static List<Snapshot> Versions(string dest)
        {
            var o = new List<Snapshot>();
            try
            {
                var root = System.IO.Path.Combine(dest, Folder); if (!Directory.Exists(root)) return o;
                foreach (var pc in Directory.GetDirectories(root))
                {
                    var pn = System.IO.Path.GetFileName(pc); if (pn.Equals(RemovedFolder, StringComparison.OrdinalIgnoreCase) || pn.Equals(ClipsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var f in Directory.GetFiles(pc, Prefix + "*.zip"))
                    {
                        var w = WhenOf(f); if (w == null) continue;
                        o.Add(new Snapshot { File = f, Pc = pn, When = w.Value, Bytes = new FileInfo(f).Length, ThisPc = pn.Equals(Safe(PcName), StringComparison.OrdinalIgnoreCase) });
                    }
                }
            }
            catch { }
            return o.OrderByDescending(v => v.ThisPc).ThenByDescending(v => v.When).ToList();
        }
        public class DestResult { public string Path, Error, Kind, File; public bool Ok, Wrote; public int Pruned, Removed; public int ClipsCopied; }
        // puts the version on a destination (copied to .part, read back and compared by SHA-256, then renamed) and prunes old ones
        public static DestResult Deliver(string dest, string zip, int keep, List<Item> clips)
        {
            var r = new DestResult { Path = dest };
            try
            {
                var root = System.IO.Path.GetPathRoot(dest);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { r.Kind = "unreachable"; r.Error = Unreachable(dest); return r; }
                var dir = DestDir(dest); Directory.CreateDirectory(dir);
                var to = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(zip));
                if (!File.Exists(to) || Backups.Sha256(to) != Backups.Sha256(zip)) { Backups.CopyVerified(zip, to); r.Wrote = true; }
                r.File = to;
                foreach (var old in Directory.GetFiles(dir, Prefix + "*.zip").Where(f => WhenOf(f) != null).OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(1, keep)).ToList())
                    if (Remove(old, dest)) r.Pruned++;
                foreach (var part in Directory.GetFiles(dir, "*.part")) try { if ((DateTime.Now - File.GetLastWriteTime(part)).TotalHours > 1) File.Delete(part); } catch { }   // our own unfinished copies only
                if (clips != null && clips.Count > 0) r.ClipsCopied = CopyClips(dest, clips);
                r.Ok = true;
            }
            catch (Exception ex) { Classify(dest, ex, r); }
            return r;
        }
        public static void Classify(string dest, Exception ex, DestResult r)
        {
            if (ex is UnauthorizedAccessException)
            {
                r.Kind = Guarded(dest) || CfaState() != false ? "blocked" : "denied";
                r.Error = r.Kind == "blocked" ? BlockedText : "Windows says Hallow Vault may not write to " + dest + " (access denied) - pick another folder.";
            }
            else if (ex is DirectoryNotFoundException || ex is DriveNotFoundException) { r.Kind = "unreachable"; r.Error = Unreachable(dest); }
            else if (ex is IOException && ((ex.HResult & 0xFFFF) == 112 || (ex.HResult & 0xFFFF) == 39)) { r.Kind = "full"; r.Error = dest + " is full - free some space or pick another destination."; }
            else if (ex is IOException && ((ex.HResult & 0xFFFF) == 53 || (ex.HResult & 0xFFFF) == 67 || (ex.HResult & 0xFFFF) == 21 || (ex.HResult & 0xFFFF) == 64)) { r.Kind = "unreachable"; r.Error = Unreachable(dest); }
            else { r.Kind = "error"; r.Error = ex.Message.TrimEnd('.'); }
        }
        public const string BlockedText = "Windows' Ransomware protection blocked this folder; pick another or allow Hallow Vault in Windows Security.";
        static string Unreachable(string dest) { return dest.StartsWith("\\\\") ? dest + " can't be reached right now (is the NAS on?) - Vault tries again later." : dest + " isn't there right now (unplugged?) - Vault catches up when it's back."; }

        // Clips favourites: copied once into <destination>\Hallow family backup\Clips favourites (same name and size = already there)
        static int CopyClips(string dest, List<Item> clips)
        {
            var dir = System.IO.Path.Combine(dest, Folder, ClipsFolder); Directory.CreateDirectory(dir); int n = 0;
            foreach (var c in clips)
            {
                var to = System.IO.Path.Combine(dir, c.Entry.Substring("clips/".Length));
                if (File.Exists(to) && new FileInfo(to).Length == c.Bytes) continue;
                Backups.CopyVerified(c.Path, to); n++;
            }
            return n;
        }

        // ---- old versions never vanish for good: the Recycle Bin on a fixed drive, else (USB drive, network share) "Removed versions"
        public static bool Remove(string file, string dest)
        {
            try
            {
                if (HasBin(file) && Recycler(file)) return true;
                if (!File.Exists(file)) return false;
                var dir = System.IO.Path.Combine(dest, Folder, RemovedFolder, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)));
                Directory.CreateDirectory(dir);
                var to = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file));
                if (File.Exists(to)) to = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(file) + " " + DateTime.Now.Ticks + ".zip");
                File.Move(file, to); return true;
            }
            catch { return false; }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveType(string root);
        static bool HasRecycleBin(string file)
        {
            try
            {
                if (file.StartsWith("\\\\")) return false;
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(file));
                return GetDriveType(root) == 3;   // DRIVE_FIXED: local disks have a Recycle Bin; removable and network drives don't
            }
            catch { return false; }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
        // FO_DELETE with FOF_ALLOWUNDO (to the Recycle Bin), silent, no confirmation - but FOF_WANTNUKEWARNING: if Windows would
        // delete it for good instead (a bin that's too small), it asks first rather than doing it quietly
        public static bool RecycleFile(string file)
        {
            var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = System.IO.Path.GetFullPath(file) + "\0\0", fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x4000 };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(file);
        }

        // ---- Windows Security's ransomware protection (Controlled folder access)
        public static bool? ReadCfa()
        {
            try
            {
                foreach (var k in new[] { @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access" })
                    using (var key = Registry.LocalMachine.OpenSubKey(k))
                    {
                        var v = key == null ? null : key.GetValue("EnableControlledFolderAccess");
                        if (v is int) return (int)v == 1;
                    }
            }
            catch { }
            return null;
        }
        // folders it guards out of the box (Documents, Desktop, Pictures, Videos, Music, Favorites - also under OneDrive)
        public static bool Guarded(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || folder.StartsWith("\\\\")) return false;
                var full = System.IO.Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                var g = new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Favorites }
                    .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
                var home = Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var n in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music", "Favorites" }) g.Add(System.IO.Path.Combine(home, n));
                try { foreach (var od in Directory.GetDirectories(home, "OneDrive*")) foreach (var n in new[] { "Documents", "Desktop", "Pictures" }) g.Add(System.IO.Path.Combine(od, n)); } catch { }
                return g.Any(p => full.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        // ---- a destination you picked or typed: null when it's fine, else what's wrong. Private network addresses only.
        public static string CheckDest(string path, IEnumerable<string> have, out string clean)
        {
            clean = (path ?? "").Trim().Trim('"').Replace('/', '\\'); if (clean.Length > 3) clean = clean.TrimEnd('\\');
            const string example = "like E:\\Backups, a USB drive (F:\\) or \\\\NAS\\share\\Backups";
            if (clean.Length == 0) return "Type a folder, " + example + ".";
            if (clean.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || clean.IndexOfAny(new[] { '*', '?', '<', '>', '|', '"' }) >= 0 || clean.Contains("\\..") || clean.StartsWith("\\\\?") || clean.StartsWith("\\\\.")) return "That isn't a folder path.";
            var unc = clean.StartsWith("\\\\"); var drive = clean.Length >= 2 && char.IsLetter(clean[0]) && clean[1] == ':' && (clean.Length == 2 || clean[2] == '\\');
            if (!unc && !drive) return "Type a full path, " + example + ".";
            if (drive && clean.Length == 2) clean += "\\";
            if (unc)
            {
                var parts = clean.Substring(2).Split('\\').Where(x => x.Length > 0).ToArray();
                if (parts.Length < 2) return "A network path needs a share: \\\\NAS\\share (and a folder in it if you like).";
                if (!PrivateHost(parts[0])) return "Hallow Vault only backs up to your own network: a NAS name like \\\\NAS, a .local / .lan name, or a private address (192.168.x.x, 10.x.x.x, 172.16-31.x.x, Tailscale 100.64-127.x.x).";
            }
            var c = clean;
            if (have != null && have.Any(h => string.Equals(h.TrimEnd('\\'), c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return "That destination is already in the list.";
            var full = clean.TrimEnd('\\') + "\\";
            foreach (var hd in new[] { Roaming, Local })
            {
                var h = hd.TrimEnd('\\') + "\\";
                if (full.StartsWith(h, StringComparison.OrdinalIgnoreCase) && full.Substring(h.Length).StartsWith("Hallow", StringComparison.OrdinalIgnoreCase)) return "Pick a folder outside the Hallow apps' own folders.";
            }
            if (full.StartsWith(KeysBackupFolder().TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return "Pick a folder outside Hallow Keys' backup folder.";
            try
            {
                var r = System.IO.Path.GetPathRoot(clean);
                if (!Directory.Exists(r)) return unc ? r + " can't be reached right now - is the NAS on, and does it open in File Explorer?" : r + " isn't there (unplugged?).";
                var dir = System.IO.Path.Combine(clean, Folder); Directory.CreateDirectory(dir);
                var probe = System.IO.Path.Combine(dir, ".hallow-vault-probe"); File.WriteAllText(probe, "ok"); File.Delete(probe);   // can Vault write there?
            }
            catch (UnauthorizedAccessException) { return Guarded(clean) || CfaState() != false ? BlockedText : "Windows says Hallow Vault may not write to " + clean + " (access denied) - pick another folder."; }
            catch (Exception ex) { return "Vault can't write to " + clean + ": " + ex.Message; }
            return null;
        }
        public static bool PrivateHost(string host)
        {
            host = (host ?? "").Trim().ToLowerInvariant(); if (host.Length == 0) return false;
            IPAddress ip;
            if (IPAddress.TryParse(host.Trim('[', ']'), out ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;
                var b = ip.GetAddressBytes();
                if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
                return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);   // fc00::/7, fe80::/10
            }
            if (host.EndsWith(".ipv6-literal.net")) return false;
            if (!host.Contains('.')) return Regex.IsMatch(host, @"^[a-z0-9][a-z0-9\-_]{0,62}$");   // a PC / NAS name on this network (NetBIOS)
            return Regex.IsMatch(host, @"\.(local|lan|home|internal|localdomain|home\.arpa|ts\.net)$");
        }
        public class DestInfo { public bool Reachable, Guarded; public long Free = -1, Total = -1; public string Kind = ""; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetDiskFreeSpaceEx(string dir, out ulong free, out ulong total, out ulong totalFree);
        public static DestInfo Info(string dest)
        {
            var i = new DestInfo { Guarded = Guarded(dest) };
            try
            {
                var root = System.IO.Path.GetPathRoot(dest);
                i.Reachable = !string.IsNullOrEmpty(root) && Directory.Exists(root);
                if (dest.StartsWith("\\\\")) i.Kind = "network share";
                else
                {
                    var t = GetDriveType(root);
                    var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                    i.Kind = t == 2 ? "USB drive" : t == 4 ? "network drive" : t == 3 ? (string.Equals(root, sys, StringComparison.OrdinalIgnoreCase) ? "this PC's system drive" : "another drive") : "drive";
                }
                ulong f, tot, tf;
                if (i.Reachable && GetDiskFreeSpaceEx(Directory.Exists(dest) ? dest : root, out f, out tot, out tf)) { i.Free = (long)f; i.Total = (long)tot; }
            }
            catch { }
            return i;
        }
        // a suggestion: another fixed drive (not the system drive, never Documents / Desktop / Pictures); null if there's none
        public static string Suggest()
        {
            try
            {
                var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
                var other = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && !string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase) && d.AvailableFreeSpace > 1L << 30);
                return other == null ? null : other.Name;
            }
            catch { return null; }
        }

        // ================================================================ test restore: every file in a version checked, nothing written
        public class Check { public bool Ok; public int Files; public long Bytes; public List<string> Problems = new List<string>(); public FamilyManifest Manifest; public string Summary; }
        public static Check Verify(string zip)
        {
            var c = new Check();
            try
            {
                using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var me = z.GetEntry(ManifestName);
                    if (me == null) { c.Problems.Add("it has no " + ManifestName + " (the list of files)"); }
                    else using (var s = me.Open()) c.Manifest = (FamilyManifest)new DataContractJsonSerializer(typeof(FamilyManifest)).ReadObject(s);
                    var listed = new HashSet<string>(StringComparer.Ordinal);
                    if (c.Manifest != null)
                        foreach (var it in c.Manifest.Items)
                        {
                            listed.Add(it.Entry);
                            var e = z.GetEntry(it.Entry);
                            if (e == null) { c.Problems.Add(it.Entry + " is missing"); continue; }
                            long n = 0; string h;
                            try
                            {
                                using (var s = e.Open()) using (var sha = SHA256.Create())
                                {
                                    var buf = new byte[1 << 16]; int k;
                                    while ((k = s.Read(buf, 0, buf.Length)) > 0) { sha.TransformBlock(buf, 0, k, null, 0); n += k; }
                                    sha.TransformFinalBlock(buf, 0, 0); h = Hex(sha.Hash);
                                }
                            }
                            catch (Exception ex) { c.Problems.Add(it.Entry + " can't be read (" + ex.Message.TrimEnd('.') + ")"); continue; }
                            if (n != it.Bytes) { c.Problems.Add(it.Entry + " is the wrong size (" + n + " of " + it.Bytes + " bytes)"); continue; }
                            if (!string.Equals(h, it.Sha256, StringComparison.OrdinalIgnoreCase)) { c.Problems.Add(it.Entry + " doesn't match its SHA-256"); continue; }
                            c.Files++; c.Bytes += n;
                        }
                    foreach (var e in z.Entries) if (e.FullName != ManifestName && !e.FullName.EndsWith("/") && !listed.Contains(e.FullName)) c.Problems.Add(e.FullName + " isn't in the list");
                }
            }
            catch (Exception ex) { c.Problems.Add("won't open: " + ex.Message.TrimEnd('.')); }
            c.Ok = c.Problems.Count == 0 && c.Manifest != null;
            c.Summary = c.Ok ? "All " + c.Files + " files check out (" + VaultSettings.Size(c.Bytes) + ") - this version can be restored." : c.Problems.Count + (c.Problems.Count == 1 ? " problem: " : " problems: ") + string.Join("; ", c.Problems.Take(3)) + (c.Problems.Count > 3 ? "; ..." : "");
            return c;
        }

        // ================================================================ restore
        public class Group { public string Key, Kind, App, Label; public int Files; public long Bytes; }
        public static List<Group> Groups(FamilyManifest m)
        {
            var o = new List<Group>(); if (m == null) return o;
            foreach (var g in m.Items.Concat(m.Clips ?? new List<FamilyEntry>()).GroupBy(x => x.Kind + ":" + x.App))
            {
                var f = g.First();
                o.Add(new Group { Key = g.Key, Kind = f.Kind, App = f.App, Label = Label(f.Kind, f.App), Files = g.Count(), Bytes = g.Sum(x => x.Bytes) });
            }
            return o.OrderBy(x => x.Kind == "settings" ? 0 : x.Kind == "keys" ? 1 : x.Kind == "authenticator" ? 2 : 3).ThenBy(x => x.Label).ToList();
        }
        public static string Untoken(string source)
        {
            if (source.StartsWith("%APPDATA%\\", StringComparison.OrdinalIgnoreCase)) return System.IO.Path.Combine(Roaming, source.Substring(10));
            if (source.StartsWith("%LOCALAPPDATA%\\", StringComparison.OrdinalIgnoreCase)) return System.IO.Path.Combine(Local, source.Substring(15));
            return source;
        }
        public class RestoreResult { public List<string> Done = new List<string>(), Skipped = new List<string>(), Notes = new List<string>(); public int Files; public string SafetyDir; }
        // settings go back into the app's real folder only once that app is closed (asked to quit through its pipe first); every file
        // they replace is kept in <safety> first. Keys / Authenticator backups go back as files into the folder that app's own restore
        // uses (never over a file that's already there) - you finish in that app. Clips favourites go back only where they're missing.
        public static RestoreResult Restore(string zip, IEnumerable<string> keys, string safetyRoot, string dest)
        {
            var res = new RestoreResult(); var want = new HashSet<string>(keys ?? new string[0]);
            var man = ReadManifest(zip); if (man == null) { res.Notes.Add("that version can't be read"); return res; }
            res.SafetyDir = System.IO.Path.Combine(safetyRoot, "family-restore-safety", DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
            using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Read))
                foreach (var g in Groups(man).Where(x => want.Contains(x.Key)))
                {
                    var items = man.Items.Where(x => x.Kind + ":" + x.App == g.Key).ToList();
                    if (g.Kind == "settings")
                    {
                        if (g.App.Equals("Vault", StringComparison.OrdinalIgnoreCase)) { res.Skipped.Add(g.Label + ": Hallow Vault can't replace its own settings while it runs - they're in the zip (" + System.IO.Path.GetFileName(zip) + ") if you need them"); continue; }
                        if (g.App != "Hallow")
                        {
                            string why; try { why = CloseApp(g.App); } catch (Exception ex) { why = ex.Message; }
                            if (why != null) { res.Skipped.Add(g.Label + ": " + why + "; nothing was changed"); continue; }
                        }
                        int n = 0;
                        foreach (var it in items)
                        {
                            var data = Bytes(z, it); if (data == null) { res.Notes.Add(it.Entry + " is damaged in this version - left as it is"); continue; }
                            var to = Untoken(it.Source);
                            if (File.Exists(to))
                            {
                                if (Sha(ReadBytes(to)) == it.Sha256) continue;   // already the same
                                var keep = System.IO.Path.Combine(res.SafetyDir, it.Entry.Replace('/', '\\')); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(keep)); File.Copy(to, keep, true);
                            }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                            File.WriteAllBytes(to + ".restoring", data);
                            if (File.Exists(to)) File.Replace(to + ".restoring", to, null); else File.Move(to + ".restoring", to);
                            n++;
                        }
                        res.Files += n; res.Done.Add(g.Label + ": " + (n == 0 ? "already the same" : n + (n == 1 ? " file" : " files") + " put back") + (g.App == "Hallow" ? "" : " - start Hallow " + g.App + " again"));
                    }
                    else if (g.Kind == "keys" || g.Kind == "authenticator")
                    {
                        int n = 0, same = 0;
                        foreach (var it in items)
                        {
                            var data = Bytes(z, it); if (data == null) { res.Notes.Add(it.Entry + " is damaged in this version - left out"); continue; }
                            var name = it.Entry.Substring(it.Entry.LastIndexOf('/') + 1);
                            var dir = g.Kind == "keys" ? KeysBackupFolder() : System.IO.Path.Combine(AuthDir, it.Entry.Split('/')[1]);
                            var to = System.IO.Path.Combine(dir, name);
                            if (File.Exists(to))
                            {
                                if (Sha(ReadBytes(to)) == it.Sha256) { same++; continue; }
                                to = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(name) + " (restored " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ")" + System.IO.Path.GetExtension(name));
                            }
                            Directory.CreateDirectory(dir); File.WriteAllBytes(to, data); n++;
                        }
                        res.Files += n;
                        var where = g.Kind == "keys" ? KeysBackupFolder() : AuthDir;
                        res.Done.Add(g.Label + ": " + n + (n == 1 ? " file" : " files") + " put in " + where + (same > 0 ? " (" + same + " already there)" : "") +
                            (g.Kind == "keys" ? " - finish in Hallow Keys: Import and export > Restore backup (master password and Secret Key, or your recovery code)" : " - finish on your phone: Hallow Authenticator > Restore from the PC (with that backup's password)"));
                    }
                    else if (g.Kind == "clips")
                    {
                        int n = 0; var cd = System.IO.Path.Combine(dest ?? "", Folder, ClipsFolder);
                        foreach (var c in man.Clips ?? new List<FamilyEntry>())
                        {
                            var from = System.IO.Path.Combine(cd, c.Entry.Substring("clips/".Length));
                            if (File.Exists(c.Source) || !File.Exists(from)) continue;
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(c.Source)); File.Copy(from, c.Source, false); n++;
                        }
                        res.Files += n; res.Done.Add(g.Label + ": " + (n == 0 ? "all still there" : n + " put back"));
                    }
                }
            if (!Directory.Exists(res.SafetyDir)) res.SafetyDir = null;
            return res;
        }
        // an entry's bytes, only if they match the SHA-256 recorded when the version was made
        static byte[] Bytes(ZipArchive z, FamilyEntry it)
        {
            try
            {
                var e = z.GetEntry(it.Entry); if (e == null) return null;
                using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); var b = ms.ToArray(); return Sha(b) == it.Sha256 ? b : null; }
            }
            catch { return null; }
        }
        // asks a running Hallow app to quit through its family pipe and waits for it (15 s). Test copies (HALLOW_FAMILY_TEST) only see
        // ".test" pipes, never a real app.
        public static string DefaultClose(string app)
        {
            Func<bool> running = () =>
            {
                string r; if (Link.SendTo(app, "ping", 300, 1500, out r) == 0) return true;
                if (Link.TestDir != null) return false;
                try { return Process.GetProcessesByName("Hallow" + app.Replace(" ", "")).Length > 0; } catch { return false; }
            };
            if (!running()) return null;
            string reply; var code = Link.SendTo(app, "quit", 500, 3000, out reply);
            var until = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < until) { if (!running()) return null; Thread.Sleep(300); }
            return "Hallow " + app + " is still running" + (code == 0 ? " (it was asked to quit)" : " and didn't answer when asked to quit") + " - close it and try again";
        }

        // ================================================================ health
        public static bool Stale(FamilySettings s, DateTime now)
        {
            if (s == null || !s.On || s.Dests.Count == 0) return false;
            DateTime t; var from = DateTime.TryParse(s.LastRun, out t) ? t : DateTime.TryParse(s.OnSince, out t) ? t : (DateTime?)null;
            return from != null && (now - from.Value).TotalDays >= StaleDays;
        }
    }
}
