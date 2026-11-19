// Hallow Vault v1.7 - move to a new PC: export every game's newest (and pinned) versions with a manifest,
// and on the other PC put them back with the paths re-mapped (your user folder, Documents, AppData, the Steam account).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace HallowVault
{
    // where things are on one PC
    [DataContract] public class Places
    {
        [DataMember] public string Profile, Docs, Local, Roaming, SteamPath, SteamUser;
        public static Places Here(string steamUser)
        {
            Func<Environment.SpecialFolder, string> f = Environment.GetFolderPath;
            return new Places { Profile = f(Environment.SpecialFolder.UserProfile), Docs = f(Environment.SpecialFolder.MyDocuments), Local = f(Environment.SpecialFolder.LocalApplicationData),
                Roaming = f(Environment.SpecialFolder.ApplicationData), SteamPath = Library.SteamPath(), SteamUser = steamUser };
        }
    }
    [DataContract] public class MoveGame
    {
        [DataMember] public string Name, Id, InstallDir, Folder;   // Folder: the game's folder inside the export
        [DataMember] public List<string> Files = new List<string>(); // newest first; Files[0] is what "Restore all newest" puts back
    }
    [DataContract] public class MoveManifest
    {
        [DataMember] public string App = "Hallow Vault", Version, Created, Machine;
        [DataMember] public Places From;
        [DataMember] public List<MoveGame> Games = new List<MoveGame>();
    }

    public static class Mover
    {
        public const string ManifestName = "hallow-vault-move.json";

        // Copies each game's newest backup and its pinned ones from the vault into <dest>\Hallow Vault move <date>. Returns that folder.
        public static string Export(string root, Places here, Func<string, string> installDirOf, string dest, out int games)
        {
            games = 0;
            var outDir = Path.Combine(dest, "Hallow Vault move " + DateTime.Now.ToString("yyyy-MM-dd HH.mm"));
            Directory.CreateDirectory(outDir);
            var man = new MoveManifest { Version = Program.Version, Created = DateTime.Now.ToString("o"), Machine = Environment.MachineName, From = here };
            foreach (var dir in Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0])
            {
                var zips = Backups.ZipsIn(dir); if (zips.Count == 0) continue;
                var pins = Backups.Pins(dir);
                var take = zips.Where((f, i) => i == 0 || pins.Contains(f.Name)).ToList();
                BackupInfo info = Backups.Read(take[0].FullName);
                var g = new MoveGame { Name = info != null && !string.IsNullOrEmpty(info.Game) ? info.Game : Path.GetFileName(dir), Id = info == null ? null : info.GameId, Folder = Path.GetFileName(dir) };
                g.InstallDir = g.Id == null ? null : installDirOf(g.Id);
                var to = Path.Combine(outDir, g.Folder); Directory.CreateDirectory(to);
                foreach (var f in take) { File.Copy(f.FullName, Path.Combine(to, f.Name), true); g.Files.Add(f.Name); }
                var keptPins = take.Where(f => pins.Contains(f.Name)).Select(f => f.Name).ToList();
                if (keptPins.Count > 0) File.WriteAllLines(Path.Combine(to, Backups.PinsFile), keptPins);
                man.Games.Add(g); games++;
            }
            using (var fs = File.Create(Path.Combine(outDir, ManifestName))) new DataContractJsonSerializer(typeof(MoveManifest)).WriteObject(fs, man);
            return outDir;
        }

        public static MoveManifest Load(string folder)
        {
            var f = Path.Combine(folder, ManifestName); if (!File.Exists(f)) return null;
            try { using (var fs = File.OpenRead(f)) return (MoveManifest)new DataContractJsonSerializer(typeof(MoveManifest)).ReadObject(fs); } catch { return null; }
        }

        // Puts one game's newest exported version back on this PC: a safety backup of the saves here first (if there are any),
        // the exported versions (and their pins) copied into this vault, then the files restored to the re-mapped paths.
        public static int ImportGame(string folder, MoveManifest man, MoveGame mg, Places here, string root, Game local, List<SaveRoot> current)
        {
            var src = Path.Combine(folder, mg.Folder);
            var b = Backups.Read(Path.Combine(src, mg.Files[0])); if (b == null) throw new InvalidDataException(mg.Files[0] + " can't be read");
            int sk; if (current != null && current.Count > 0) Backups.Make(root, local, current, "before importing", out sk);
            var dst = Backups.GameDir(root, local); Directory.CreateDirectory(dst);
            var pins = Backups.Pins(src);
            foreach (var f in mg.Files)
            {
                var to = Path.Combine(dst, f);
                if (!File.Exists(to)) File.Copy(Path.Combine(src, f), to);
                if (pins.Contains(f)) Backups.SetPin(to, true);
            }
            var newInstall = local == null ? null : local.InstallDir;
            return Backups.Restore(b, p => Map(p, man.From, here, mg.InstallDir, newInstall));
        }

        // where a path from the old PC belongs on this one (unchanged when no rule fits - same layout)
        public static string Map(string path, Places from, Places to, string oldInstall, string newInstall)
        {
            if (string.IsNullOrEmpty(path) || from == null || to == null) return path;
            Func<string, string, string> under = (p, root) =>
            {
                if (string.IsNullOrEmpty(root)) return null; root = root.TrimEnd('\\');
                return p.Equals(root, StringComparison.OrdinalIgnoreCase) ? "" : p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length) : null;
            };
            // the game folder itself (saves kept inside it, like Delta Force)
            if (!string.IsNullOrEmpty(newInstall)) { var r = under(path, oldInstall); if (r != null) return newInstall.TrimEnd('\\') + r; }
            // Steam: any account's userdata\<id> -> this PC's signed-in account
            if (!string.IsNullOrEmpty(from.SteamPath))
            {
                var m = Regex.Match(path, "^" + Regex.Escape(from.SteamPath.TrimEnd('\\')) + @"\\userdata\\\d+(\\.*)?$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var rest = m.Groups[1].Value;
                    if (!string.IsNullOrEmpty(to.SteamUser)) return to.SteamUser.TrimEnd('\\') + rest;
                    if (!string.IsNullOrEmpty(to.SteamPath)) return to.SteamPath.TrimEnd('\\') + path.Substring(from.SteamPath.TrimEnd('\\').Length);
                }
            }
            // the most specific known folder first (Documents may be in OneDrive, AppData may have moved)
            var pairs = new[] { Tuple.Create(from.Docs, to.Docs), Tuple.Create(from.Local, to.Local), Tuple.Create(from.Roaming, to.Roaming), Tuple.Create(from.SteamPath, to.SteamPath), Tuple.Create(from.Profile, to.Profile) }
                .Where(p => !string.IsNullOrEmpty(p.Item1) && !string.IsNullOrEmpty(p.Item2)).OrderByDescending(p => p.Item1.Length);
            foreach (var p in pairs) { var r = under(path, p.Item1); if (r != null) return p.Item2.TrimEnd('\\') + r; }
            return path;
        }
    }
}
