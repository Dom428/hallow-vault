// Hallow Vault - finds installed games: Steam, Battle.net, Epic Games, plus games you add yourself.
// Only reads launcher files and the registry; artwork comes from Steam's own on-disk cache (nothing is downloaded).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HallowVault
{
    public static class Library
    {
        // Steam tools that aren't games
        static readonly Regex NotAGame = new Regex(@"Redistributable|Steamworks|Proton|SteamVR|Steam Linux Runtime|Wallpaper Engine|Soundtrack|Dedicated Server|\bSDK\b", RegexOptions.IgnoreCase);
        static readonly HashSet<string> NotAGameIds = new HashSet<string> { "228980", "431960", "1070560", "1391110", "1628350", "250820" };

        public static List<Game> Scan(List<Game> custom)
        {
            var list = new List<Game>();
            try { list.AddRange(Steam()); } catch { }
            try { list.AddRange(BattleNet()); } catch { }
            try { list.AddRange(Epic()); } catch { }
            try { list.AddRange(EA()); } catch { }
            try { list.AddRange(Ubisoft()); } catch { }
            try { list.AddRange(Gog()); } catch { }
            try { list.AddRange(Riot()); } catch { }
            try { list.AddRange(Xbox()); } catch { }
            foreach (var c in custom) if (c != null && !string.IsNullOrEmpty(c.Launch)) { c.IconExe = c.Launch; c.Alts = new List<Game>(); c.Parent = null; list.Add(c); }
            foreach (var g in list.Where(x => x.Source != "steam"))
            {
                try { var f = g.IconExe ?? (File.Exists(g.Launch) ? g.Launch : null); if (f != null) { g.UpdatedAt = File.GetLastWriteTime(f); g.Drive = Path.GetPathRoot(f).TrimEnd('\\'); } } catch { }
            }
            try { SteamPlaytime(list); } catch { }
            return Merge(list.GroupBy(g => g.Id).Select(x => x.First()).ToList());
        }

        // ---- Steam: libraryfolders.vdf -> steamapps\appmanifest_*.acf
        public static string SteamPath()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                var p = k == null ? null : k.GetValue("SteamPath") as string;
                return string.IsNullOrEmpty(p) ? null : p.Replace('/', '\\');
            }
        }
        // every Steam library folder (C:\Program Files (x86)\Steam, G:\SteamLibrary, ...)
        public static List<string> SteamLibraries()
        {
            var steam = SteamPath(); var libs = new List<string>(); if (steam == null || !Directory.Exists(steam)) return libs;
            libs.Add(steam);
            var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf, Encoding.UTF8), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
            return libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }
        public static string EpicManifests { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests"); } }
        // the icon Steam made for a game (used for desktop shortcuts)
        public static string SteamIcon(string appid)
        {
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + appid, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + appid })
                using (var k = Registry.LocalMachine.OpenSubKey(root))
                {
                    var ic = k == null ? null : k.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrEmpty(ic) && File.Exists(ic.Trim('"'))) return ic.Trim('"');
                }
            return null;
        }
        static IEnumerable<Game> Steam()
        {
            var seen = new HashSet<string>();
            var steam = SteamPath();
            foreach (var lib in SteamLibraries())
            {
                var apps = Path.Combine(lib, "steamapps"); if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string t; try { t = File.ReadAllText(acf, Encoding.UTF8); } catch { continue; }
                    var id = Field(t, "appid"); var name = Field(t, "name"); var dir = Field(t, "installdir");
                    if (id == null || name == null || dir == null || !seen.Add(id)) continue;
                    if (NotAGameIds.Contains(id) || NotAGame.IsMatch(name)) continue;
                    var g = new Game { Id = "steam:" + id, Name = Clean(name), Source = "steam", InstallDir = Path.Combine(apps, "common", dir), Launch = "steam://rungameid/" + id };
                    long size; long.TryParse(Field(t, "SizeOnDisk") ?? "0", out size); g.SizeBytes = size;
                    int flags; int.TryParse(Field(t, "StateFlags") ?? "4", out flags);
                    g.Updating = (flags & 1024) != 0; g.NeedsUpdate = !g.Updating && (flags & 2) != 0;
                    long todo, done;
                    if (long.TryParse(Field(t, "BytesToDownload") ?? "0", out todo) && todo > 0 && long.TryParse(Field(t, "BytesDownloaded") ?? "0", out done))
                    { g.Progress = Math.Min(1.0, done / (double)todo); if (done < todo) { g.Updating = true; g.NeedsUpdate = false; } }
                    g.Drive = Path.GetPathRoot(lib).TrimEnd('\\');
                    try { g.UpdatedAt = File.GetLastWriteTime(acf); } catch { }
                    var cache = Path.Combine(steam, @"appcache\librarycache", id);
                    if (Directory.Exists(cache))
                    {
                        var files = Directory.GetFiles(cache, "*", SearchOption.AllDirectories);
                        Func<string, string> pick = n => files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase));
                        g.Cover = pick("library_600x900.jpg") ?? pick("library_capsule.jpg");
                        g.Hero = pick("library_hero.jpg"); g.Logo = pick("logo.png"); g.Header = pick("library_header.jpg") ?? pick("header.jpg");
                    }
                    yield return g;
                }
            }
        }
        static string Field(string acf, string key) { var m = Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : null; }
        static string Clean(string n) { return n.Replace("\u00AE", "").Replace("\u2122", "").Trim(); }   // drop (R) and TM

        // ---- v1.1: your Steam playtime and last-played (Steam keeps them in userdata\<account>\config\localconfig.vdf)
        static void SteamPlaytime(List<Game> games)
        {
            var steam = SteamPath(); if (steam == null) return;
            var ud = Path.Combine(steam, "userdata"); if (!Directory.Exists(ud)) return;
            string file = null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                var active = k == null ? null : k.GetValue("ActiveUser");
                if (active is int && (int)active != 0) { var f = Path.Combine(ud, ((uint)(int)active).ToString(), @"config\localconfig.vdf"); if (File.Exists(f)) file = f; }
            }
            if (file == null)   // Steam closed: the account that used this PC last
                file = Directory.GetDirectories(ud).Select(d => Path.Combine(d, @"config\localconfig.vdf")).Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (file == null) return;
            var root = Vdf.Parse(File.ReadAllText(file, Encoding.UTF8));
            var apps = Vdf.Path(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps") as Dictionary<string, object>;
            if (apps == null) return;
            foreach (var g in games.Where(x => x.Source == "steam"))
            {
                object o; if (!apps.TryGetValue(g.Id.Substring(6), out o)) continue;
                var a = o as Dictionary<string, object>; if (a == null) continue;
                double v; long l;
                if (double.TryParse(Vdf.Str(a, "Playtime"), out v)) g.SteamMinutes = v;
                if (double.TryParse(Vdf.Str(a, "Playtime2wks"), out v)) g.Steam2Weeks = v;
                if (long.TryParse(Vdf.Str(a, "LastPlayed"), out l) && l > 0) g.SteamLast = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(l);
            }
        }

        // ---- v1.1: the same game in two stores becomes one card. The main copy is the one played or updated most recently.
        public static string Norm(string name) { return Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }
        static List<Game> Merge(List<Game> list)
        {
            var result = new List<Game>();
            foreach (var grp in list.GroupBy(g => Norm(g.Name)))
            {
                var all = grp.ToList();
                if (all.Count == 1) { result.Add(all[0]); continue; }
                var main = all.OrderByDescending(g => g.SteamLast ?? DateTime.MinValue).ThenByDescending(g => g.UpdatedAt).First();
                foreach (var g in all) if (g != main) { g.Parent = main; main.Alts.Add(g); }
                result.Add(main);
            }
            return result;
        }

        // ---- Battle.net: Blizzard games in the uninstall list, launched through Battle.net
        static readonly Dictionary<string, string> BnetCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Overwatch", "Pro" }, { "Diablo IV", "Fen" }, { "Diablo III", "D3" }, { "Diablo II: Resurrected", "OSI" }, { "World of Warcraft", "WoW" },
            { "Hearthstone", "WTCG" }, { "StarCraft II", "S2" }, { "StarCraft", "S1" }, { "Heroes of the Storm", "Hero" }, { "Warcraft III: Reforged", "W3" }, { "Diablo Immortal", "ANBS" } };
        static IEnumerable<Game> BattleNet()
        {
            string bnet = null; var games = new List<Game>();
            foreach (var e in Uninstall())
            {
                if (!string.Equals(e.Publisher, "Blizzard Entertainment", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Name == "Battle.net") { var exe = Path.Combine(e.Location ?? "", "Battle.net.exe"); if (File.Exists(exe)) bnet = exe; continue; }
                if (string.IsNullOrEmpty(e.Location) || !Directory.Exists(e.Location)) continue;
                var icon = e.Icon != null && File.Exists(e.Icon.Split(',')[0].Trim('"')) ? e.Icon.Split(',')[0].Trim('"') : null;
                games.Add(new Game { Id = "bnet:" + e.Name, Name = Clean(e.Name), Source = "battlenet", InstallDir = e.Location.TrimEnd('\\'), Launch = icon ?? "", IconExe = icon, Uninstall = e.Uninstall });
            }
            foreach (var g in games)
            {
                string code;
                if (bnet != null && BnetCodes.TryGetValue(g.Name, out code)) { g.Launch = bnet; g.Args = "--exec=\"launch " + code + "\""; }
                if (!string.IsNullOrEmpty(g.Launch)) yield return g;
            }
        }
        class Entry { public string Name, Publisher, Location, Icon, Uninstall; }
        static IEnumerable<Entry> Uninstall()
        {
            var roots = new[] { Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Tuple.Create(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall") };
            foreach (var r in roots)
                using (var k = r.Item1.OpenSubKey(r.Item2))
                {
                    if (k == null) continue;
                    foreach (var n in k.GetSubKeyNames())
                        using (var s = k.OpenSubKey(n))
                        {
                            if (s == null) continue;
                            var name = s.GetValue("DisplayName") as string; if (string.IsNullOrEmpty(name)) continue;
                            yield return new Entry { Name = name, Publisher = s.GetValue("Publisher") as string, Location = s.GetValue("InstallLocation") as string, Icon = s.GetValue("DisplayIcon") as string, Uninstall = s.GetValue("UninstallString") as string };
                        }
                }
        }

        // ---- Epic Games: one JSON manifest per installed game
        static IEnumerable<Game> Epic()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) yield break;
            var js = new JavaScriptSerializer();
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                Dictionary<string, object> m; try { m = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f)); } catch { continue; }
                Func<string, string> s = k => { object v; return m.TryGetValue(k, out v) ? v as string : null; };
                var name = s("DisplayName"); var app = s("AppName"); var loc = s("InstallLocation"); var exe = s("LaunchExecutable");
                if (name == null || app == null || loc == null || !Directory.Exists(loc)) continue;
                object cats; if (m.TryGetValue("AppCategories", out cats) && cats is System.Collections.ArrayList && !((System.Collections.ArrayList)cats).Contains("games")) continue;
                var full = exe == null ? null : Path.Combine(loc, exe);
                yield return new Game { Id = "epic:" + app, Name = Clean(name), Source = "epic", InstallDir = loc.TrimEnd('\\'),
                    Launch = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(app) + "?action=launch&silent=true", IconExe = full != null && File.Exists(full) ? full : null };
            }
        }

        // v1.4: size of a folder (skips links so nothing is counted twice; unreadable folders count as 0)
        public static long DirSize(string dir)
        {
            long total = 0; var stack = new Stack<string>(); stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(d);
                    foreach (var f in di.EnumerateFiles()) { try { total += f.Length; } catch { } }
                    foreach (var sub in di.EnumerateDirectories()) if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub.FullName);
                }
                catch { }
            }
            return total;
        }

        // ---- v1.5: EA app - EA games have an __Installer folder in their install location
        static IEnumerable<Game> EA()
        {
            foreach (var e in Uninstall())
            {
                if (e.Publisher == null || e.Publisher.IndexOf("Electronic Arts", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (string.IsNullOrEmpty(e.Location) || !Directory.Exists(Path.Combine(e.Location, "__Installer"))) continue;
                var exe = e.Icon == null ? null : e.Icon.Split(',')[0].Trim('"');
                if (exe == null || !File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new Game { Id = "ea:" + Norm(e.Name), Name = Clean(e.Name), Source = "ea", InstallDir = e.Location.TrimEnd('\\'), Launch = exe, IconExe = exe, Uninstall = e.Uninstall };
            }
        }
        // ---- Ubisoft Connect: HKLM\...\Ubisoft\Launcher\Installs\<id>, started with uplay://launch/<id>/0
        static IEnumerable<Game> Ubisoft()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            {
                if (k == null) yield break;
                var names = Uninstall().Where(e => e.Name != null).ToList();
                foreach (var id in k.GetSubKeyNames())
                    using (var s = k.OpenSubKey(id))
                    {
                        var dir = s == null ? null : s.GetValue("InstallDir") as string;
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
                        using (var u = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install " + id))
                        { var dn = u == null ? null : u.GetValue("DisplayName") as string; if (!string.IsNullOrEmpty(dn)) name = dn; }
                        var exe = Directory.GetFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                        yield return new Game { Id = "ubi:" + id, Name = Clean(name), Source = "ubisoft", InstallDir = dir.Replace('/', '\\').TrimEnd('\\'), Launch = "uplay://launch/" + id + "/0", IconExe = exe };
                    }
            }
        }
        // ---- GOG Galaxy: HKLM\...\GOG.com\Games\<id> (gameName, path, exe)
        static IEnumerable<Game> Gog()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            {
                if (k == null) yield break;
                foreach (var id in k.GetSubKeyNames())
                    using (var s = k.OpenSubKey(id))
                    {
                        if (s == null) continue;
                        var name = s.GetValue("gameName") as string; var dir = s.GetValue("path") as string; var exe = s.GetValue("exe") as string;
                        if (name == null || dir == null || !Directory.Exists(dir) || exe == null || !File.Exists(exe)) continue;
                        yield return new Game { Id = "gog:" + id, Name = Clean(name), Source = "gog", InstallDir = dir.TrimEnd('\\'), Launch = exe, IconExe = exe };
                    }
            }
        }
        // ---- Riot: the Riot Client starts its games (--launch-product); installed ones have a product_settings.yaml with their path
        static readonly Dictionary<string, string> RiotNames = new Dictionary<string, string> {
            { "valorant", "VALORANT" }, { "league_of_legends", "League of Legends" }, { "bacon", "Legends of Runeterra" }, { "lion", "2XKO" }, { "teamfighttactics", "Teamfight Tactics" } };
        static IEnumerable<Game> Riot()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");
            var cfg = Path.Combine(root, "RiotClientInstalls.json"); if (!File.Exists(cfg)) yield break;
            var rc = Regex.Match(File.ReadAllText(cfg), "\"rc_default\"\\s*:\\s*\"([^\"]+)\""); if (!rc.Success) yield break;
            var client = rc.Groups[1].Value.Replace('/', '\\'); if (!File.Exists(client)) yield break;
            var meta = Path.Combine(root, "Metadata"); if (!Directory.Exists(meta)) yield break;
            foreach (var d in Directory.GetDirectories(meta, "*.live"))
            {
                var product = Path.GetFileName(d); product = product.Substring(0, product.Length - 5);
                var yaml = Directory.GetFiles(d, "*.product_settings.yaml").FirstOrDefault(); if (yaml == null) continue;
                var m = Regex.Match(File.ReadAllText(yaml), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)\"?"); if (!m.Success) continue;
                var dir = m.Groups[1].Value.Trim().Replace('/', '\\'); if (!Directory.Exists(dir)) continue;
                string name; if (!RiotNames.TryGetValue(product, out name)) name = product.Replace('_', ' ');
                var exe = Directory.GetFiles(dir, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                yield return new Game { Id = "riot:" + product, Name = name, Source = "riot", InstallDir = dir.TrimEnd('\\'), Launch = client, Args = "--launch-product=" + product + " --launch-patchline=live", IconExe = exe };
            }
        }
        // ---- Xbox app (PC Game Pass): <drive>:\XboxGames\<game>\Content\MicrosoftGame.config, started through Windows' app list
        static IEnumerable<Game> Xbox()
        {
            var aumids = AppsFolder(); if (aumids.Count == 0) yield break;
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames"); if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var cfg = Path.Combine(dir, @"Content\MicrosoftGame.config"); if (!File.Exists(cfg)) continue;
                    string idName, publisher, display; try
                    {
                        var x = System.Xml.Linq.XDocument.Load(cfg).Root;
                        var ident = x.Element("Identity"); idName = (string)ident.Attribute("Name"); publisher = (string)ident.Attribute("Publisher");
                        var sv = x.Element("ShellVisuals"); display = sv == null ? null : (string)sv.Attribute("DefaultDisplayName");
                    }
                    catch { continue; }
                    if (idName == null || publisher == null) continue;
                    var family = idName + "_" + PublisherId(publisher);
                    var aumid = aumids.FirstOrDefault(a => a.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));
                    if (aumid == null) continue;   // leftover files, not installed
                    var content = Path.Combine(dir, "Content");
                    var exe = Directory.GetFiles(content, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                    yield return new Game { Id = "xbox:" + idName, Name = Clean(display ?? Path.GetFileName(dir)), Source = "xbox", InstallDir = dir, Launch = "shell:AppsFolder\\" + aumid, IconExe = exe };
                }
            }
        }
        // the 13-letter id Windows derives from a package publisher (SHA-256 of the UTF-16 name, first 8 bytes, Crockford base32)
        public static string PublisherId(string publisher)
        {
            byte[] h; using (var sha = System.Security.Cryptography.SHA256.Create()) h = sha.ComputeHash(Encoding.Unicode.GetBytes(publisher));
            var bits = new StringBuilder(); for (int i = 0; i < 8; i++) bits.Append(Convert.ToString(h[i], 2).PadLeft(8, '0')); bits.Append('0');
            const string a = "0123456789abcdefghjkmnpqrstvwxyz"; var sb = new StringBuilder();
            for (int i = 0; i < 65; i += 5) sb.Append(a[Convert.ToInt32(bits.ToString(i, 5), 2)]);
            return sb.ToString();
        }
        static List<string> AppsFolder()
        {
            var list = new List<string>();
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application"); var sh = Activator.CreateInstance(t);
                var ns = t.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { "shell:AppsFolder" });
                var items = ns.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, ns, null);
                foreach (var it in (System.Collections.IEnumerable)items)
                {
                    var path = it.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, it, null) as string;
                    if (path != null && path.Contains("!")) list.Add(path);
                }
            }
            catch { }
            return list;
        }

        // ---- a game you add yourself (any .exe)
        public static Game FromExe(string exe)
        {
            var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName;
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
            return new Game { Id = "custom:" + exe.ToLowerInvariant(), Name = Clean(name), Source = "custom", InstallDir = Path.GetDirectoryName(exe), Launch = exe, IconExe = exe };
        }
    }
    // Valve's KeyValues text format: "key" "value" and "key" { ... }. Keys are case-insensitive.
    public static class Vdf
    {
        public static Dictionary<string, object> Parse(string t) { int i = 0; return Block(t, ref i); }
        static Dictionary<string, object> Block(string t, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Token(t, ref i);
                if (key == null || key == "}") return d;
                var val = Token(t, ref i);
                if (val == null) return d;
                if (val == "{") d[key] = Block(t, ref i); else d[key] = val;
            }
        }
        static string Token(string t, ref int i)
        {
            while (i < t.Length)
            {
                char c = t[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
                if (c == '{' || c == '}') { i++; return c.ToString(); }
                if (c == '"')
                {
                    var sb = new StringBuilder(); i++;
                    while (i < t.Length && t[i] != '"') { if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i] == 'n' ? '\n' : t[i]); } else sb.Append(t[i]); i++; }
                    i++; return sb.ToString();
                }
                var st = i; while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}') i++;
                return t.Substring(st, i - st);
            }
            return null;
        }
        public static object Path(Dictionary<string, object> d, params string[] keys)
        {
            object cur = d;
            foreach (var k in keys) { var m = cur as Dictionary<string, object>; if (m == null || !m.TryGetValue(k, out cur)) return null; }
            return cur;
        }
        public static string Str(Dictionary<string, object> d, string k) { object o; return d.TryGetValue(k, out o) ? o as string : null; }
    }
}