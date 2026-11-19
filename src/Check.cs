// Hallow Vault v1.12 - Check backups: every zip opens, every file inside matches its CRC-32, and the file list matches
// the one Vault recorded in the zip's vault.json (backups from 1.12 on). Read-only: never changes, moves or deletes a backup.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HallowVault
{
    // Level: 0 = fine (green), 1 = worth a look (amber), 2 = damaged (red); -1 = gone while checking (pruned)
    public class ZipCheck { public string File; public int Level; public string Note; public int Files; public long Bytes; }
    public class FolderCheck
    {
        public string Name, Folder, Dir; public int Level; public DateTime When = DateTime.Now;
        public List<ZipCheck> Zips = new List<ZipCheck>(); public List<string> Notes = new List<string>();
        public string Summary
        {
            get
            {
                var z = Zips.Where(x => x.Level >= 0).ToList();
                if (z.Count == 0) return Notes.Count > 0 ? string.Join("; ", Notes) : "no backups";
                int red = z.Count(x => x.Level == 2), amber = z.Count(x => x.Level == 1);
                var s = red == 0 && amber == 0 ? (z.Count == 1 ? "1 backup, fine" : "all " + z.Count + " backups fine")
                    : (red > 0 ? red + " of " + z.Count + (z.Count == 1 ? " backup" : " backups") + " damaged" : "") + (red > 0 && amber > 0 ? ", " : "") + (amber > 0 ? amber + (red > 0 ? "" : " of " + z.Count) + (amber == 1 ? " needs" : " need") + " a look" : "");
                return s + (Notes.Count > 0 ? "; " + string.Join("; ", Notes) : "");
            }
        }
    }

    public static class Checker
    {
        public static readonly string[] Words = { "fine", "worth a look", "damaged" };
        public static readonly string[] Colors = { "#6ED28C", "#F0BE5A", "#F07070" };

        // ---- one zip
        public static ZipCheck Zip(string file)
        {
            var r = new ZipCheck { File = file };
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    r.Bytes = fs.Length;
                    List<Central> cd;
                    try { cd = ReadCentral(fs); } catch (Exception ex) { r.Level = 2; r.Note = "won't open: " + ex.Message; return r; }
                    int hdrBad = 0, hdrOdd = 0;
                    foreach (var c in cd) { var h = LocalHeader(fs, c); if (h == 2) hdrBad++; else if (h == 1) hdrOdd++; }
                    if (hdrBad > 0) { r.Level = 2; r.Note = hdrBad + (hdrBad == 1 ? " file can't be found where" : " files can't be found where") + " the zip's table of contents says"; return r; }
                    fs.Position = 0;
                    ZipArchive z;
                    try { z = new ZipArchive(fs, ZipArchiveMode.Read, true); } catch (Exception ex) { r.Level = 2; r.Note = "won't open: " + ex.Message; return r; }
                    using (z)
                    {
                        var entries = z.Entries.ToList();
                        if (entries.Count != cd.Count) { r.Level = 2; r.Note = "its table of contents doesn't add up (" + entries.Count + " vs " + cd.Count + " entries)"; return r; }
                        var bad = new List<string>(); BackupInfo info = null; bool infoBroken = false;
                        var have = new Dictionary<string, long>(StringComparer.Ordinal);
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i]; var c = cd[i]; long n; uint crc; byte[] keep = null;
                            try
                            {
                                using (var s = e.Open())
                                {
                                    if (e.FullName == "vault.json" && c.Size < (4 << 20)) { var ms = new MemoryStream(); crc = Crc32(s, out n, ms); keep = ms.ToArray(); }
                                    else crc = Crc32(s, out n, null);
                                }
                            }
                            catch (Exception ex) { bad.Add(e.FullName + " can't be read (" + ex.Message.TrimEnd('.') + ")"); continue; }
                            if (n != c.Size) { bad.Add(e.FullName + " is the wrong size (" + n + " of " + c.Size + " bytes)"); continue; }
                            if (crc != c.Crc) { bad.Add(e.FullName + " doesn't match its checksum"); continue; }
                            if (e.FullName == "vault.json")
                            {
                                try { info = (BackupInfo)new DataContractJsonSerializer(typeof(BackupInfo)).ReadObject(new MemoryStream(keep ?? new byte[0])); } catch { infoBroken = true; }
                            }
                            else if (!e.FullName.EndsWith("/")) have[e.FullName] = n;
                        }
                        r.Files = have.Count;
                        if (bad.Count > 0) { r.Level = 2; r.Note = bad.Count + (bad.Count == 1 ? " file is" : " files are") + " damaged: " + string.Join("; ", bad.Take(3)) + (bad.Count > 3 ? "; ..." : ""); return r; }
                        var notes = new List<string>(); int level = 0;
                        if (hdrOdd > 0) { level = 1; notes.Add(hdrOdd + (hdrOdd == 1 ? " file's own header doesn't" : " files' own headers don't") + " match the zip's table of contents (Vault can still read " + (hdrOdd == 1 ? "it" : "them") + "; other zip tools may not)"); }
                        if (info == null) { level = 1; notes.Add(infoBroken ? "its vault.json can't be read, so Restore can't tell where the files go (the files themselves are fine)" : "it has no vault.json, so Restore can't tell where the files go (the files themselves are fine)"); }
                        else
                        {
                            if (info.Files != null)   // v1.12 backups: compare with the recorded file list
                            {
                                var want = new Dictionary<string, long>(StringComparer.Ordinal);
                                foreach (var l in info.Files) { var t = l.LastIndexOf('\t'); long sz; if (t > 0 && long.TryParse(l.Substring(t + 1), out sz)) want[l.Substring(0, t)] = sz; else want[l] = -1; }
                                var missing = want.Keys.Where(k => !have.ContainsKey(k)).ToList();
                                var wrong = want.Where(kv => kv.Value >= 0 && have.ContainsKey(kv.Key) && have[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
                                var extra = have.Keys.Where(k => !want.ContainsKey(k)).ToList();
                                if (missing.Count > 0) { r.Level = 2; r.Note = missing.Count + (missing.Count == 1 ? " file is" : " files are") + " missing from the zip: " + string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? ", ..." : ""); return r; }
                                if (wrong.Count > 0) { r.Level = 2; r.Note = wrong.Count + (wrong.Count == 1 ? " file isn't" : " files aren't") + " the size Vault wrote: " + string.Join(", ", wrong.Take(3)) + (wrong.Count > 3 ? ", ..." : ""); return r; }
                                if (extra.Count > 0) { level = 1; notes.Add(extra.Count + (extra.Count == 1 ? " file is" : " files are") + " in the zip but not in Vault's list (" + string.Join(", ", extra.Take(3)) + (extra.Count > 3 ? ", ..." : "") + ")"); }
                            }
                            var roots = info.Roots == null ? 0 : info.Roots.Count;
                            var stray = have.Keys.Count(k => { var sl = k.IndexOf('/'); int idx; return sl < 0 || !int.TryParse(k.Substring(0, sl), out idx) || idx < 0 || idx >= roots; });
                            if (stray > 0) { level = 1; notes.Add(stray + (stray == 1 ? " file isn't" : " files aren't") + " in a save folder Vault knows, so Restore skips " + (stray == 1 ? "it" : "them")); }
                            if (have.Count == 0 && roots > 0) { level = 1; notes.Add("it's empty - no save files in it"); }
                            if (info.Files == null && level == 0) notes.Add("made before 1.12, so checked by the zip's own checksums");
                        }
                        r.Level = level; r.Note = notes.Count == 0 ? null : string.Join("; ", notes);
                    }
                }
            }
            catch (FileNotFoundException) { r.Level = -1; r.Note = "removed while checking (pruned)"; }
            catch (DirectoryNotFoundException) { r.Level = -1; r.Note = "removed while checking"; }
            catch (Exception ex) { r.Level = 2; r.Note = "can't be read: " + ex.Message; }
            return r;
        }

        // ---- one game's backup folder (every Vault zip in it, newest first)
        public static FolderCheck Folder(string dir, string name, Func<bool> cancel = null)
        {
            var fc = new FolderCheck { Dir = dir, Folder = Path.GetFileName(dir.TrimEnd('\\')), Name = name };
            if (!Directory.Exists(dir)) { fc.Notes.Add("no backups yet"); fc.Level = 0; return fc; }
            foreach (var f in Backups.ZipsIn(dir))
            {
                if (cancel != null && cancel()) break;
                fc.Zips.Add(Zip(f.FullName));
            }
            if (fc.Name == null) { var first = fc.Zips.FirstOrDefault(z => z.Level >= 0); var bi = first == null ? null : Backups.Read(first.File); fc.Name = bi != null && !string.IsNullOrEmpty(bi.Game) ? bi.Game : fc.Folder; }
            try
            {
                var parts = new DirectoryInfo(dir).GetFiles("*.zip.part").Where(p => (DateTime.Now - p.LastWriteTime).TotalMinutes > 30).ToList();   // a backup that was cut off (Vault or the PC stopped mid-backup)
                if (parts.Count > 0) fc.Notes.Add(parts.Count + (parts.Count == 1 ? " unfinished backup file" : " unfinished backup files") + " (" + parts[0].Name + (parts.Count > 1 ? ", ..." : "") + ") - left from a backup that was cut off; Vault never uses " + (parts.Count == 1 ? "it" : "them"));
            }
            catch { }
            fc.Level = Math.Max(fc.Zips.Count == 0 ? 0 : fc.Zips.Max(z => z.Level), fc.Notes.Any(n => n.Contains("unfinished")) ? 1 : 0);
            if (fc.Zips.Count == 0 && fc.Notes.Count == 0) fc.Notes.Add("no backups yet");
            return fc;
        }

        // ---- the whole vault: every game folder that has backups (games you removed too). `names`: folder name -> game name.
        public static List<FolderCheck> All(string root, Dictionary<string, string> names, Action<int, int> progress = null, Func<bool> cancel = null)
        {
            var res = new List<FolderCheck>();
            if (!Directory.Exists(root)) return res;
            var dirs = Directory.GetDirectories(root).Where(d => Backups.ZipsIn(d).Count > 0 || SafeAny(d, "*.zip.part")).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < dirs.Count; i++)
            {
                if (cancel != null && cancel()) break;
                if (progress != null) progress(i, dirs.Count);
                string nm; names.TryGetValue(Path.GetFileName(dirs[i]), out nm);
                res.Add(Folder(dirs[i], nm, cancel));
            }
            return res;
        }
        static bool SafeAny(string dir, string pattern) { try { return Directory.GetFiles(dir, pattern).Length > 0; } catch { return false; } }

        // "<level>|<when>|<summary>" for vault.json, and back
        public static string Pack(FolderCheck f) { return f.Level + "|" + f.When.ToString("o") + "|" + f.Summary.Replace("|", "/"); }
        public static bool Unpack(string s, out int level, out DateTime when, out string summary)
        {
            level = 0; when = DateTime.MinValue; summary = null;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(new[] { '|' }, 3); if (p.Length < 3 || !int.TryParse(p[0], out level)) return false;
            DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out when); summary = p[2]; return true;
        }

        // ---- CRC-32 (the zip one), over a whole stream; `copy` gets the bytes too when given
        static readonly uint[] Table = MakeTable();
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { var c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        public static uint Crc32(Stream s, out long length, Stream copy)
        {
            uint crc = 0xFFFFFFFFu; var buf = new byte[81920]; int n; length = 0;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
                length += n; if (copy != null) copy.Write(buf, 0, n);
            }
            return crc ^ 0xFFFFFFFFu;
        }

        // ---- the zip's central directory (its table of contents): each entry's CRC-32 and size, in order. Zip64 too.
        class Central { public uint Crc; public long Size, Local; public byte[] Name; }
        // each file's own header inside the zip (other zip tools read these): 2 = missing, 1 = its name doesn't match the table of contents
        static int LocalHeader(Stream fs, Central c)
        {
            if (c.Local < 0 || c.Local + 30 > fs.Length) return 2;
            var h = new byte[30]; fs.Position = c.Local; ReadFull(fs, h, 30);
            if (U32(h, 0) != 0x04034b50) return 2;
            int nl = U16(h, 26); if (nl != c.Name.Length || c.Local + 30 + nl > fs.Length) return 1;
            var n = new byte[nl]; ReadFull(fs, n, nl);
            for (int i = 0; i < nl; i++) if (n[i] != c.Name[i]) return 1;
            return 0;
        }
        static List<Central> ReadCentral(Stream fs)
        {
            var len = fs.Length; if (len < 22) throw new InvalidDataException("the file is too short to be a zip");
            var tailLen = (int)Math.Min(len, 22 + 65535 + 20); var tail = new byte[tailLen];
            fs.Position = len - tailLen; ReadFull(fs, tail, tailLen);
            int eocd = -1; for (int i = tailLen - 22; i >= 0; i--) if (U32(tail, i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("the end of the zip is missing (cut off?)");
            long count = U16(tail, eocd + 10), size = U32(tail, eocd + 12), offset = U32(tail, eocd + 16);
            var eocdPos = len - tailLen + eocd;
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
            {
                if (eocd < 20 || U32(tail, eocd - 20) != 0x07064b50) throw new InvalidDataException("the zip64 end record is missing");
                var z64 = (long)U64(tail, eocd - 20 + 8); var rec = new byte[56];
                if (z64 < 0 || z64 + 56 > len) throw new InvalidDataException("the zip64 end record points outside the file");
                fs.Position = z64; ReadFull(fs, rec, 56);
                if (U32(rec, 0) != 0x06064b50) throw new InvalidDataException("the zip64 end record is damaged");
                count = (long)U64(rec, 32); size = (long)U64(rec, 40); offset = (long)U64(rec, 48); eocdPos = z64;
            }
            if (offset < 0 || size < 0 || offset + size > eocdPos) throw new InvalidDataException("its table of contents points outside the file (cut off?)");
            if (size > (256L << 20)) throw new InvalidDataException("its table of contents is unreasonably big");
            var cd = new byte[size]; fs.Position = offset; ReadFull(fs, cd, (int)size);
            var res = new List<Central>(); int p = 0;
            for (long k = 0; k < count; k++)
            {
                if (p + 46 > cd.Length || U32(cd, p) != 0x02014b50) throw new InvalidDataException("its table of contents is damaged");
                var c = new Central { Crc = U32(cd, p + 16), Size = U32(cd, p + 24), Local = U32(cd, p + 42) };
                long comp = U32(cd, p + 20);
                int nameLen = U16(cd, p + 28), extraLen = U16(cd, p + 30), commentLen = U16(cd, p + 32);
                if (p + 46 + nameLen + extraLen + commentLen > cd.Length) throw new InvalidDataException("its table of contents is damaged");
                c.Name = new byte[nameLen]; Array.Copy(cd, p + 46, c.Name, 0, nameLen);
                if (c.Size == 0xFFFFFFFF || comp == 0xFFFFFFFF || c.Local == 0xFFFFFFFF)   // zip64: the real values are in extra field 0x0001, in this order
                {
                    int x = p + 46 + nameLen, end = x + extraLen;
                    while (x + 4 <= end)
                    {
                        int id = U16(cd, x), dl = U16(cd, x + 2);
                        if (id == 1)
                        {
                            int q = x + 4, qe = Math.Min(end, q + dl);
                            if (c.Size == 0xFFFFFFFF && q + 8 <= qe) { c.Size = (long)U64(cd, q); q += 8; }
                            if (comp == 0xFFFFFFFF && q + 8 <= qe) q += 8;
                            if (c.Local == 0xFFFFFFFF && q + 8 <= qe) c.Local = (long)U64(cd, q);
                            break;
                        }
                        x += 4 + dl;
                    }
                }
                res.Add(c); p += 46 + nameLen + extraLen + commentLen;
            }
            return res;
        }
        static void ReadFull(Stream s, byte[] b, int n) { int got = 0; while (got < n) { var k = s.Read(b, got, n - got); if (k <= 0) throw new InvalidDataException("the file ends early"); got += k; } }
        static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static uint U32(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }
        static ulong U64(byte[] b, int i) { return U32(b, i) | ((ulong)U32(b, i + 4) << 32); }
    }
}
