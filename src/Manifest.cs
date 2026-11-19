// Hallow Vault v1.11 - the online save database: Ludusavi's manifest (github.com/mtkennerly/ludusavi-manifest, MIT),
// compiled from PCGamingWiki (CC BY-NC-SA 3.0). Downloaded from GitHub, re-checked weekly (only re-downloaded when its
// git sha changes), parsed once into a small index (Windows save/config paths and HKCU registry keys) cached next to it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowVault
{
    public class ManGame
    {
        public string Name; public List<long> Steam = new List<long>(); public List<string> InstallDirs = new List<string>();
        public List<Tuple<bool, string>> Files = new List<Tuple<bool, string>>();      // (config only, path template)
        public List<Tuple<bool, string>> Registry = new List<Tuple<bool, string>>();   // (config only, HKCU\Software\...)
    }

    public static class Manifest
    {
        public const string RawUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/refs/heads/data/data/manifest.yaml";
        public const string ApiUrl = "https://api.github.com/repos/mtkennerly/ludusavi-manifest/contents/data?ref=data";
        public static string Dir { get { return Path.Combine(VaultSettings.Dir, "manifest"); } }
        static string IndexFile { get { return Path.Combine(Dir, "index.txt"); } }
        static string MetaFile { get { return Path.Combine(Dir, "sha.txt"); } }

        // ---- the loaded index
        static Dictionary<long, ManGame> bySteam = new Dictionary<long, ManGame>();
        static Dictionary<string, ManGame> byName = new Dictionary<string, ManGame>();
        public static int Count { get; private set; }
        public static DateTime? Updated { get; private set; }
        public static bool Loaded { get { return Count > 0; } }

        public static ManGame For(Game g)
        {
            if (!Loaded || g == null) return null;
            ManGame m;
            foreach (var x in new[] { g }.Concat(g.Alts))
            {
                long id; if (x.Source == "steam" && long.TryParse(x.Id.Substring(6), out id) && bySteam.TryGetValue(id, out m)) return m;
            }
            foreach (var x in new[] { g }.Concat(g.Alts))
            {
                if (byName.TryGetValue(Library.Norm(x.Name), out m)) return m;
                if (!string.IsNullOrEmpty(x.InstallDir) && byName.TryGetValue("dir:" + Library.Norm(Path.GetFileName(x.InstallDir.TrimEnd('\\'))), out m)) return m;
            }
            return null;
        }

        // ---- the cached index on disk: G<tab>name<tab>steam ids<tab>install dirs, then F/R<tab>s|c<tab>path lines
        public static bool Load()
        {
            try
            {
                if (!File.Exists(IndexFile)) return false;
                var s = new Dictionary<long, ManGame>(); var n = new Dictionary<string, ManGame>(); ManGame cur = null; int count = 0;
                foreach (var line in File.ReadLines(IndexFile, Encoding.UTF8))
                {
                    if (line.Length < 2) continue;
                    var p = line.Split('\t');
                    if (p[0] == "G")
                    {
                        cur = new ManGame { Name = p[1] }; count++;
                        foreach (var id in p[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) { long v; if (long.TryParse(id, out v)) { cur.Steam.Add(v); if (!s.ContainsKey(v)) s[v] = cur; } }
                        cur.InstallDirs.AddRange(p[3].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                        var nn = Library.Norm(cur.Name); if (nn.Length > 0 && !n.ContainsKey(nn)) n[nn] = cur;
                        foreach (var d in cur.InstallDirs) { var dn = "dir:" + Library.Norm(d); if (dn.Length > 4 && !n.ContainsKey(dn)) n[dn] = cur; }
                    }
                    else if (p[0] == "A" && p.Length >= 3) { ManGame t; var an = Library.Norm(p[1]); if (an.Length > 0 && !n.ContainsKey(an) && n.TryGetValue(Library.Norm(p[2]), out t)) n[an] = t; }
                    else if (cur != null && p.Length >= 3 && p[0] == "F") cur.Files.Add(Tuple.Create(p[1] == "c", p[2]));
                    else if (cur != null && p.Length >= 3 && p[0] == "R") cur.Registry.Add(Tuple.Create(p[1] == "c", p[2]));
                }
                bySteam = s; byName = n; Count = count; Updated = File.GetLastWriteTime(IndexFile);
                return count > 0;
            }
            catch { return false; }
        }

        // ---- parse manifest.yaml (only what Vault needs) into index text. Throws on a broken file.
        public static string BuildIndex(string yamlFile, out int games)
        {
            var sb = new StringBuilder(); games = 0; var aliases = new List<Tuple<string, string>>(); var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in Yaml.TopLevel(yamlFile))
            {
                var name = block.Item1; var g = block.Item2 as Dictionary<string, object>; if (g == null) continue;
                var alias = Yaml.Str(g, "alias"); if (alias != null) { aliases.Add(Tuple.Create(name, alias)); continue; }
                var files = new List<string>(); var regs = new List<string>();
                var fm = g.ContainsKey("files") ? g["files"] as Dictionary<string, object> : null;
                if (fm != null)
                    foreach (var kv in fm)
                    {
                        var spec = kv.Value as Dictionary<string, object>;
                        if (!ForWindows(spec, true)) continue;
                        var path = kv.Key;
                        if (path.Contains("<xdg") || path.StartsWith("~")) continue;
                        files.Add((Tag(spec)) + "\t" + path.Replace('\t', ' '));
                    }
                var rm = g.ContainsKey("registry") ? g["registry"] as Dictionary<string, object> : null;
                if (rm != null)
                    foreach (var kv in rm)
                    {
                        var spec = kv.Value as Dictionary<string, object>;
                        if (!ForWindows(spec, false)) continue;
                        var key = kv.Key.Replace('/', '\\').Trim('\\');
                        if (!key.StartsWith(@"HKEY_CURRENT_USER\Software\", StringComparison.OrdinalIgnoreCase)) continue;   // only HKCU\Software is backed up
                        regs.Add(Tag(spec) + "\t" + Reg.Prefix + key.Substring(@"HKEY_CURRENT_USER\".Length));
                    }
                if (files.Count == 0 && regs.Count == 0) continue;
                var ids = new List<long>();
                var st = g.ContainsKey("steam") ? g["steam"] as Dictionary<string, object> : null; long sid;
                if (st != null && long.TryParse(Yaml.Str(st, "id") ?? "", out sid)) ids.Add(sid);
                var idm = g.ContainsKey("id") ? g["id"] as Dictionary<string, object> : null;
                if (idm != null && idm.ContainsKey("steamExtra")) { var l = idm["steamExtra"] as List<object>; if (l != null) foreach (var o in l) if (long.TryParse(o as string ?? "", out sid)) ids.Add(sid); }
                var dirs = g.ContainsKey("installDir") && g["installDir"] is Dictionary<string, object> ? ((Dictionary<string, object>)g["installDir"]).Keys.ToList() : new List<string>();
                sb.Append("G\t").Append(Clean(name)).Append('\t').Append(string.Join(",", ids)).Append('\t').Append(string.Join("|", dirs.Select(Clean))).Append('\n');
                foreach (var f in files) sb.Append("F\t").Append(f).Append('\n');
                foreach (var r in regs) sb.Append("R\t").Append(r).Append('\n');
                written.Add(name); games++;
            }
            // other names for games in the index ("alias: <real title>")
            foreach (var a in aliases.Where(x => written.Contains(x.Item2))) sb.Append("A\t").Append(Clean(a.Item1)).Append('\t').Append(Clean(a.Item2)).Append('\n');
            return sb.ToString();
        }
        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('|', '/'); }
        static string Tag(Dictionary<string, object> spec)
        {
            var tags = spec == null || !spec.ContainsKey("tags") ? null : spec["tags"] as List<object>;
            if (tags == null || tags.Count == 0) return "s";   // untagged: always backed up
            return tags.Contains("save") ? "s" : tags.Contains("config") ? "c" : "s";
        }
        // no "when", or any "when" that doesn't rule out Windows
        static bool ForWindows(Dictionary<string, object> spec, bool hasOs)
        {
            var when = spec == null || !spec.ContainsKey("when") ? null : spec["when"] as List<object>;
            if (when == null || when.Count == 0) return true;
            foreach (var w in when.OfType<Dictionary<string, object>>()) { var os = Yaml.Str(w, "os"); if (os == null || os == "windows") return true; }
            return false;
        }

        // ---- download and weekly re-check. Returns a message for the status line, or null when nothing changed.
        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.UserAgent] = "HallowVault"; return wc;
        }
        public static string KnownSha() { try { return File.Exists(MetaFile) ? File.ReadAllText(MetaFile).Trim() : null; } catch { return null; } }
        public static string Refresh(out bool changed)
        {
            changed = false;
            string sha;
            try
            {
                using (var wc = Client())
                {
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    var list = wc.DownloadString(ApiUrl);
                    var m = Regex.Match(list, "\"name\"\\s*:\\s*\"manifest\\.yaml\"[^}]*?\"sha\"\\s*:\\s*\"([0-9a-f]{40})\"");
                    if (!m.Success) return "Couldn't read the save database's version on GitHub - using " + (Loaded ? "the copy from " + Updated.Value.ToString("d MMM") : "Vault's own detection") + ".";
                    sha = m.Groups[1].Value;
                }
            }
            catch (Exception ex) { return "Couldn't check the save database (" + ex.Message + ") - " + (Loaded ? "using the copy from " + Updated.Value.ToString("d MMM") : "using Vault's own detection") + "."; }
            if (sha == KnownSha() && File.Exists(IndexFile)) { try { File.SetLastWriteTime(MetaFile, DateTime.Now); } catch { } return null; }
            var part = Path.Combine(Dir, "manifest.yaml.part"); var idxPart = IndexFile + ".part";
            try
            {
                Directory.CreateDirectory(Dir);
                using (var wc = Client()) wc.DownloadFile(RawUrl, part);
                if (GitSha(part) != sha) throw new InvalidDataException("the download didn't match GitHub's checksum");
                int games; var sw = Stopwatch.StartNew();
                var idx = BuildIndex(part, out games);
                if (games < 1000) throw new InvalidDataException("only " + games + " games in it");
                File.WriteAllText(idxPart, "#\t" + sha + "\t" + games + "\t" + sw.ElapsedMilliseconds + "ms\n" + idx, new UTF8Encoding(false));
                Replace(part, Path.Combine(Dir, "manifest.yaml")); Replace(idxPart, IndexFile);
                File.WriteAllText(MetaFile, sha);
                changed = true;
                return "Save database updated: " + games.ToString("N0") + " games.";
            }
            catch (Exception ex)
            {
                try { File.Delete(part); } catch { } try { File.Delete(idxPart); } catch { }
                return "The save database couldn't be updated (" + ex.Message + ") - " + (Loaded ? "kept the copy from " + Updated.Value.ToString("d MMM") : "using Vault's own detection") + ".";
            }
        }
        static void Replace(string from, string to) { if (File.Exists(to)) File.Replace(from, to, null); else File.Move(from, to); }
        // git's blob sha-1 (what GitHub's contents API reports): sha1("blob <length>\0" + bytes)
        public static string GitSha(string file)
        {
            using (var sha = SHA1.Create()) using (var f = File.OpenRead(file))
            {
                var head = Encoding.ASCII.GetBytes("blob " + f.Length + "\0"); sha.TransformBlock(head, 0, head.Length, null, 0);
                var buf = new byte[1 << 16]; int n; while ((n = f.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public static bool DueForCheck() { try { return !File.Exists(MetaFile) || !File.Exists(IndexFile) || (DateTime.Now - File.GetLastWriteTime(MetaFile)).TotalDays >= 7; } catch { return true; } }

        // ---- path templates -> real folders and files on this PC
        // Roots: the Steam folder (userdata lives there) and the game's library folder (steamapps lives there) - <root> means either
        public class Where { public string Base, GameDir, SteamId; public List<string> Roots = new List<string>(), SteamAccounts = new List<string>(); }
        public static List<string> Expand(string template, Where w)
        {
            if (template.Contains("<root>"))
            {
                var all = new List<string>();
                foreach (var r in w.Roots.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var hit in Expand(template.Replace("<root>", r.TrimEnd('\\')), w)) if (!all.Contains(hit, StringComparer.OrdinalIgnoreCase)) all.Add(hit);
                return all;
            }
            var res = new List<string>();
            Func<Environment.SpecialFolder, string> sf = Environment.GetFolderPath;
            var t = template.Replace('/', '\\');
            var map = new Dictionary<string, string> {
                { "<home>", sf(Environment.SpecialFolder.UserProfile) }, { "<winAppData>", sf(Environment.SpecialFolder.ApplicationData) },
                { "<winLocalAppData>", sf(Environment.SpecialFolder.LocalApplicationData) }, { "<winDocuments>", sf(Environment.SpecialFolder.MyDocuments) },
                { "<winPublic>", Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public" }, { "<winProgramData>", sf(Environment.SpecialFolder.CommonApplicationData) },
                { "<winDir>", sf(Environment.SpecialFolder.Windows) }, { "<osUserName>", Environment.UserName },
                { "<base>", w.Base }, { "<game>", w.GameDir }, { "<storeGameId>", w.SteamId } };
            foreach (var kv in map)
            {
                if (!t.Contains(kv.Key)) continue;
                if (string.IsNullOrEmpty(kv.Value)) return res;   // e.g. <base> for a game that isn't installed
                t = t.Replace(kv.Key, kv.Value.TrimEnd('\\'));
            }
            if (t.Contains("<")) { if (!t.Contains("<storeUserId>")) return res; }
            var users = t.Contains("<storeUserId>") ? (w.SteamAccounts.Count > 0 ? w.SteamAccounts : new List<string> { "*" }) : new List<string> { null };
            foreach (var u in users)
            {
                var p = u == null ? t : t.Replace("<storeUserId>", u);
                foreach (var h in Glob(p)) { string hit; try { hit = Path.GetFullPath(h).TrimEnd('\\'); } catch { continue; } if (!res.Contains(hit, StringComparer.OrdinalIgnoreCase)) res.Add(hit); }
                if (res.Count > 200) break;
            }
            return res;
        }
        // * ? [abc] in any part, ** for any depth; returns existing files and folders
        public static List<string> Glob(string path)
        {
            var res = new List<string>();
            if (path.IndexOfAny(new[] { '*', '?', '[' }) < 0) { if (Directory.Exists(path) || File.Exists(path)) res.Add(path.TrimEnd('\\')); return res; }
            var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            int i = 0; var start = new StringBuilder();
            while (i < parts.Count && parts[i].IndexOfAny(new[] { '*', '?', '[' }) < 0) { start.Append(parts[i]).Append('\\'); i++; }
            var cur = new List<string> { start.ToString().TrimEnd('\\') };
            if (cur[0].EndsWith(":")) cur[0] += "\\";
            for (; i < parts.Count && cur.Count > 0; i++)
            {
                var seg = parts[i]; var last = i == parts.Count - 1; var next = new List<string>();
                if (seg == "**")
                {
                    foreach (var c in cur) { if (!Directory.Exists(c)) continue; next.Add(c); try { next.AddRange(Directory.EnumerateDirectories(c, "*", SearchOption.AllDirectories).Take(2000)); } catch { } }
                    if (last) { res.AddRange(next); return res.Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList(); }
                }
                else
                {
                    var re = new Regex("^" + Regex.Escape(seg).Replace(@"\*", ".*").Replace(@"\?", ".").Replace(@"\[", "[").Replace(@"]", "]") + "$", RegexOptions.IgnoreCase);
                    foreach (var c in cur)
                    {
                        if (!Directory.Exists(c)) continue;
                        try
                        {
                            foreach (var e in last ? Directory.EnumerateFileSystemEntries(c) : Directory.EnumerateDirectories(c))
                                if (re.IsMatch(Path.GetFileName(e))) next.Add(e);
                        }
                        catch { }
                        if (next.Count > 2000) break;
                    }
                }
                cur = next.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            res.AddRange(cur.Where(c => Directory.Exists(c) || File.Exists(c)));
            return res.Take(500).ToList();
        }
    }

    // A YAML reader for the subset the manifest uses: block maps and lists by indentation, plain / "double" / 'single' quoted
    // scalars, {} and [] for empty, "- key: value" list items. Comments and "---" are skipped. Anything else throws.
    public static class Yaml
    {
        // one top-level entry at a time (the file is 18 MB; only one game's lines are held at once)
        public static IEnumerable<Tuple<string, object>> TopLevel(string file)
        {
            var lines = new List<string>(); string key = null; int no = 0, keyLine = 0;
            foreach (var raw in File.ReadLines(file, Encoding.UTF8))
            {
                no++;
                if (raw.Length == 0 || raw.StartsWith("#") || raw == "---") continue;
                if (raw[0] != ' ')
                {
                    if (key != null) yield return Tuple.Create(key, Parse(lines, keyLine));
                    var colon = KeyEnd(raw, 0); if (colon < 0) throw new FormatException("line " + no + ": expected \"name:\"");
                    key = Scalar(raw.Substring(0, colon).Trim(), no); lines.Clear(); keyLine = no;
                    var rest = raw.Substring(colon + 1).Trim(); if (rest.Length > 0) lines.Add("  " + rest);   // "name: {}"
                    continue;
                }
                lines.Add(raw);
            }
            if (key != null) yield return Tuple.Create(key, Parse(lines, keyLine));
        }
        public static string Str(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) ? o as string : null; }

        static object Parse(List<string> lines, int firstNo)
        {
            if (lines.Count == 0) return new Dictionary<string, object>();
            if (lines.Count == 1 && lines[0].Trim() == "{}") return new Dictionary<string, object>();
            int i = 0; return Node(lines, ref i, Indent(lines[0]), firstNo);
        }
        static int Indent(string l) { int n = 0; while (n < l.Length && l[n] == ' ') n++; return n; }
        static object Node(List<string> L, ref int i, int ind, int no)
        {
            if (L[i].Substring(ind).StartsWith("- ") || L[i].Substring(ind) == "-") return Seq(L, ref i, ind, no);
            return Map(L, ref i, ind, no);
        }
        static Dictionary<string, object> Map(List<string> L, ref int i, int ind, int no)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            while (i < L.Count)
            {
                var l = L[i]; var li = Indent(l); if (li < ind) break;
                if (li > ind) throw new FormatException("line " + (no + i + 1) + ": unexpected indent");
                var body = l.Substring(ind);
                if (body.StartsWith("- ")) break;
                var colon = KeyEnd(body, 0); if (colon < 0) throw new FormatException("line " + (no + i + 1) + ": expected \"key:\"");
                var k = Scalar(body.Substring(0, colon).Trim(), no + i + 1); var rest = body.Substring(colon + 1).Trim(); i++;
                if (rest.Length > 0) d[k] = Value(rest, no + i);
                else if (i < L.Count && (Indent(L[i]) > ind || (Indent(L[i]) == ind && L[i].Substring(ind).StartsWith("- ")))) d[k] = Node(L, ref i, Indent(L[i]), no);
                else d[k] = null;
            }
            return d;
        }
        static List<object> Seq(List<string> L, ref int i, int ind, int no)
        {
            var list = new List<object>();
            while (i < L.Count)
            {
                var l = L[i]; var li = Indent(l); if (li != ind || !(l.Substring(ind).StartsWith("- ") || l.Substring(ind) == "-")) break;
                var rest = l.Substring(ind + 1).TrimStart(); var itemInd = ind + (l.Length - ind - rest.Length);
                if (rest.Length == 0) { i++; list.Add(i < L.Count && Indent(L[i]) > ind ? Node(L, ref i, Indent(L[i]), no) : null); continue; }
                if (KeyEnd(rest, 0) >= 0)
                {
                    // "- key: value" starts a map whose other keys are indented to line up with "key"
                    L[i] = new string(' ', itemInd) + rest;
                    list.Add(Map(L, ref i, itemInd, no));
                }
                else { list.Add(Value(rest, no + i + 1)); i++; }
            }
            return list;
        }
        static object Value(string v, int no)
        {
            if (v == "{}") return new Dictionary<string, object>();
            if (v == "[]") return new List<object>();
            if (v.StartsWith("{") || v.StartsWith("[") || v.StartsWith("|") || v.StartsWith(">") || v.StartsWith("&") || v.StartsWith("*")) throw new FormatException("line " + no + ": YAML feature not supported: " + v);
            return Scalar(v, no);
        }
        // where "key:" ends (a colon followed by a space or the end, outside quotes); -1 if this isn't a key
        static int KeyEnd(string s, int from)
        {
            if (s.StartsWith("\""))
            {
                int j = 1; while (j < s.Length && s[j] != '"') { if (s[j] == '\\') j++; j++; }
                return j + 1 < s.Length && s[j + 1] == ':' && (j + 2 == s.Length || s[j + 2] == ' ') ? j + 1 : -1;
            }
            if (s.StartsWith("'"))
            {
                int j = 1; while (j < s.Length) { if (s[j] == '\'') { if (j + 1 < s.Length && s[j + 1] == '\'') { j += 2; continue; } break; } j++; }
                return j + 1 < s.Length && s[j + 1] == ':' && (j + 2 == s.Length || s[j + 2] == ' ') ? j + 1 : -1;
            }
            for (int j = from; j < s.Length; j++) if (s[j] == ':' && (j + 1 == s.Length || s[j + 1] == ' ')) return j;
            return -1;
        }
        public static string Scalar(string s, int no)
        {
            if (s.StartsWith("\""))
            {
                if (s.Length < 2 || !s.EndsWith("\"")) throw new FormatException("line " + no + ": unclosed quote");
                var sb = new StringBuilder();
                for (int j = 1; j < s.Length - 1; j++)
                {
                    var c = s[j];
                    if (c != '\\') { sb.Append(c); continue; }
                    j++; var e = s[j];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break; case 't': sb.Append('\t'); break; case '0': sb.Append('\0'); break;
                        case 'x': sb.Append((char)Convert.ToInt32(s.Substring(j + 1, 2), 16)); j += 2; break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(j + 1, 4), 16)); j += 4; break;
                        default: sb.Append(e); break;   // \" \\ \/
                    }
                }
                return sb.ToString();
            }
            if (s.StartsWith("'"))
            {
                if (s.Length < 2 || !s.EndsWith("'")) throw new FormatException("line " + no + ": unclosed quote");
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            }
            return s;
        }
    }
}
