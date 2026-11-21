// Hallow Vault - settings (%APPDATA%\HallowVault\vault.json) and the Game type shared with Hallow Hub's library scanner.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace HallowVault
{
    // A game found on this PC (same shape as Hallow Hub's, so the scanner is shared).
    [DataContract]
    public class Game
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Source;
        [DataMember] public string InstallDir;
        [DataMember] public string Launch;
        [DataMember] public string Args = "";
        public string Cover, Hero, Logo, Header, IconExe; public string OriginalName;
        public string Folder;   // v1.12: a game you added keeps its backup folder name when you rename it
        public string Exe;      // v1.12: a game you added: its .exe (running = that exact file)
        public DateTime UpdatedAt;
        public long SizeBytes; public string Drive; public string Uninstall;
        public bool NeedsUpdate, Updating; public double Progress = -1;
        public double SteamMinutes, Steam2Weeks; public DateTime? SteamLast;
        public List<Game> Alts = new List<Game>();
        public Game Parent;
        public string SourceName { get { return Source == "steam" ? "Steam" : Source == "battlenet" ? "Battle.net" : Source == "epic" ? "Epic Games" : Source == "ea" ? "EA app" : Source == "ubisoft" ? "Ubisoft Connect" : Source == "gog" ? "GOG" : Source == "riot" ? "Riot" : Source == "xbox" ? "Xbox" : Source == "hallow" ? "Hallow apps" : "Added by you"; } }
    }

    // v1.12: a game no launcher knows - its name, save folders and (optionally) its .exe, kept in vault.json
    [DataContract]
    public class CustomGame
    {
        [DataMember] public string Id, Name, Folder, Exe;
        [DataMember] public List<string> Folders = new List<string>();
        [OnDeserializing] void D(StreamingContext c) { Folders = new List<string>(); }
        public Game ToGame() { return new Game { Id = Id, Name = Name, Source = "custom", Folder = Folder, Exe = string.IsNullOrEmpty(Exe) ? null : Exe }; }

        // checks and stores a new or edited game; returns null when saved, or what's wrong. `taken`: the names of every other game Vault shows.
        public static string Save(VaultSettings s, CustomGame existing, string name, IEnumerable<string> folders, string exe, IEnumerable<string> taken, out CustomGame saved)
        {
            saved = null; name = (name ?? "").Trim(); exe = (exe ?? "").Trim().Trim('"');
            var fl = (folders ?? new string[0]).Select(f => (f ?? "").Trim().Trim('"')).Select(f => f.Length > 3 ? f.TrimEnd('\\') : f).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (name.Length == 0) return "Give the game a name.";
            if (name.Length > 100) return "That name is too long.";
            var n = Library.Norm(name);
            if (n.Length == 0) return "Give the game a name with letters or numbers in it.";
            if (taken != null && taken.Any(t => Library.Norm(t) == n)) return "Vault already has a game called " + name + ".";
            if (s.Custom.Any(c => c != existing && Library.Norm(c.Name) == n)) return "You already added a game called " + name + ".";
            if (fl.Count == 0) return "Add at least one save folder.";
            foreach (var f in fl)
            {
                if (f.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(f) || !(f.StartsWith("\\\\") || (f.Length >= 3 && f[1] == ':' && f[2] == '\\'))) return f + " isn't a full folder path.";
                if (Path.GetPathRoot(f).TrimEnd('\\').Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "Pick a save folder, not a whole drive (" + f + ").";
            }
            if (exe.Length > 0 && (exe.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) return "The game's program has to be an .exe file.";
            var c2 = existing ?? new CustomGame { Id = "vaultgame:" + DateTime.UtcNow.Ticks.ToString("x"), Folder = Backups.SafeName(name) };
            c2.Name = name; c2.Folders = fl; c2.Exe = exe.Length == 0 ? null : exe;
            if (existing == null) s.Custom.Add(c2);
            s.LastSig.Remove(c2.Id);   // back up with the new folders next time
            saved = c2; return null;
        }
        // forgets the game (its settings only); its backups stay in the vault
        public static void Remove(VaultSettings s, CustomGame c)
        {
            s.Custom.Remove(c); s.Off.Remove(c.Id); s.LastSig.Remove(c.Id); s.Alarms.Remove(c.Id); s.Extra.Remove(c.Id); s.Include.Remove(c.Id); s.Exclude.Remove(c.Id); s.Results.Remove(c.Id);
        }
    }

    [DataContract]
    public class VaultSettings
    {
        [DataMember] public string VaultDir = "";                       // "" = pick automatically (a second drive if there is one)
        [DataMember] public int Keep = 10;                              // backups kept per game
        [DataMember] public bool OnClose = true;                        // back up a game's saves when it closes
        [DataMember] public bool Daily = true;                          // and everything once a day
        [DataMember] public bool BeforeUpdate = true;                   // v1.2: and right before Steam updates it
        [DataMember] public string CopyDir = "";                        // v1.3: second copy of every backup ("" = off)
        [DataMember] public int MaxGameMB = 0, MaxTotalMB = 0;          // v1.3: size limits (0 = none); oldest versions go first
        [DataMember] public bool UseHallowIcon = true;                  // v1.5: hide our tray icon while Hallow Hub hosts the family menu
        [DataMember] public bool UseHallowAccent = true;                // v1.6: the family accent colour (Hallow Toolkit)
        [DataMember] public string LastCrashShown = "";                 // v1.6: the crash report we already told you about
        [DataMember] public Dictionary<string, string> Alarms = new Dictionary<string, string>();
        [DataMember] public bool UseManifest = true, ManifestConfig = true;   // v1.11: the online save database, and its settings files too
        [DataMember] public int EveryHours = 0;                         // v1.9: also back up every N hours (0 = off)
        [DataMember] public string LastScheduled = "";
        [DataMember] public string Compression = "best";                // v1.9: best / fast / none
        [DataMember] public Dictionary<string, List<string>> Include = new Dictionary<string, List<string>>(), Exclude = new Dictionary<string, List<string>>();   // v1.9: patterns per game   // v1.7: game id -> "<last good backup>\n<what looks wrong>"
        [DataMember] public string LastDaily = "";
        [DataMember] public bool TrayOnClose = true;
        [DataMember] public Dictionary<string, List<string>> Extra = new Dictionary<string, List<string>>();   // folders you added, per game
        [DataMember] public List<string> Off = new List<string>();      // games you don't want backed up
        [DataMember] public Dictionary<string, string> LastSig = new Dictionary<string, string>();   // fingerprint of the last backup, per game
        [DataMember] public string LastSeenVersion = "", LastUpdateCheck = "";
        [DataMember] public List<CustomGame> Custom = new List<CustomGame>();   // v1.12: games no launcher knows, added by you
        [DataMember] public bool WeeklyCheck = false;                   // v1.12: check every backup once a week (off by default)
        [DataMember] public string LastCheck = "";
        [DataMember] public Dictionary<string, string> Checks = new Dictionary<string, string>();   // v1.12: backup folder name -> "<level>|<when>|<summary>" of the last check
        [DataMember] public int CopyKeep = 0;                           // v1.13: versions kept per game in the second copy (0 = in step with the vault)
        [DataMember] public string CopyVerified = "";                   // v1.13: the last Verify of the second copy: "<when>|<checked>|<bad>|<repaired>|<unverifiable>"
        [DataMember] public Dictionary<string, string> Results = new Dictionary<string, string>();   // v1.13: game id -> "<ok|failed|refused>|<when>|<what>" of its last backup try
        [OnDeserializing] void D(StreamingContext c)
        {
            VaultDir = ""; Keep = 10; OnClose = true; Daily = true; LastDaily = ""; TrayOnClose = true; Extra = new Dictionary<string, List<string>>(); Off = new List<string>();
            LastSig = new Dictionary<string, string>(); LastSeenVersion = ""; LastUpdateCheck = ""; BeforeUpdate = true; CopyDir = ""; MaxGameMB = 0; MaxTotalMB = 0; UseHallowIcon = true; UseHallowAccent = true; LastCrashShown = ""; Alarms = new Dictionary<string, string>(); UseManifest = true; ManifestConfig = true; EveryHours = 0; LastScheduled = ""; Compression = "best"; Include = new Dictionary<string, List<string>>(); Exclude = new Dictionary<string, List<string>>();
            Custom = new List<CustomGame>(); WeeklyCheck = false; LastCheck = ""; Checks = new Dictionary<string, string>();
            CopyKeep = 0; CopyVerified = ""; Results = new Dictionary<string, string>();
        }

        public static string Dir { get { return Link.TestDir != null ? Path.Combine(Link.TestDir, "HallowVault") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowVault"); } }
        static string FilePath { get { return Path.Combine(Dir, "vault.json"); } }
        public static bool Frozen;
        static DataContractJsonSerializerSettings Opts() { return new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }; }
        public static VaultSettings Load()
        {
            try { if (File.Exists(FilePath)) using (var f = File.OpenRead(FilePath)) { var s = (VaultSettings)new DataContractJsonSerializer(typeof(VaultSettings), Opts()).ReadObject(f); if (s != null) return s; } }
            catch { try { File.Copy(FilePath, FilePath + ".broken", true); } catch { } }
            return new VaultSettings();
        }
        static readonly object saveLock = new object();   // static: the deserializer skips field initializers, so an instance lock would be null
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock)
                try
                {
                    Directory.CreateDirectory(Dir); var tmp = FilePath + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(VaultSettings), Opts()).WriteObject(f, this);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
                }
                catch { }
        }

        public static string Ago(DateTime? when)
        {
            if (when == null) return "never";
            var d = DateTime.Now - when.Value;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            if (d.TotalDays < 2) return "yesterday";
            if (d.TotalDays < 30) return (int)d.TotalDays + " days ago";
            return when.Value.ToString("d MMM yyyy");
        }
        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < (1L << 20)) return (b / 1024.0).ToString("0") + " KB";
            if (b < (1L << 30)) return (b / (double)(1L << 20)).ToString("0.#") + " MB";
            return (b / (double)(1L << 30)).ToString("0.#") + " GB";
        }
    }
}
