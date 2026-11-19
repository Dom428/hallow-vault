// Hallow Vault - settings (%APPDATA%\HallowVault\vault.json) and the Game type shared with Hallow Hub's library scanner.
using System;
using System.Collections.Generic;
using System.IO;
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
        public DateTime UpdatedAt;
        public long SizeBytes; public string Drive; public string Uninstall;
        public bool NeedsUpdate, Updating; public double Progress = -1;
        public double SteamMinutes, Steam2Weeks; public DateTime? SteamLast;
        public List<Game> Alts = new List<Game>();
        public Game Parent;
        public string SourceName { get { return Source == "steam" ? "Steam" : Source == "battlenet" ? "Battle.net" : Source == "epic" ? "Epic Games" : Source == "ea" ? "EA app" : Source == "ubisoft" ? "Ubisoft Connect" : Source == "gog" ? "GOG" : Source == "riot" ? "Riot" : Source == "xbox" ? "Xbox" : Source == "hallow" ? "Hallow apps" : "Added by you"; } }
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
        [DataMember] public Dictionary<string, string> Alarms = new Dictionary<string, string>();   // v1.7: game id -> "<last good backup>\n<what looks wrong>"
        [DataMember] public string LastDaily = "";
        [DataMember] public bool TrayOnClose = true;
        [DataMember] public Dictionary<string, List<string>> Extra = new Dictionary<string, List<string>>();   // folders you added, per game
        [DataMember] public List<string> Off = new List<string>();      // games you don't want backed up
        [DataMember] public Dictionary<string, string> LastSig = new Dictionary<string, string>();   // fingerprint of the last backup, per game
        [DataMember] public string LastSeenVersion = "", LastUpdateCheck = "";
        [OnDeserializing] void D(StreamingContext c)
        {
            VaultDir = ""; Keep = 10; OnClose = true; Daily = true; LastDaily = ""; TrayOnClose = true; Extra = new Dictionary<string, List<string>>(); Off = new List<string>();
            LastSig = new Dictionary<string, string>(); LastSeenVersion = ""; LastUpdateCheck = ""; BeforeUpdate = true; CopyDir = ""; MaxGameMB = 0; MaxTotalMB = 0; UseHallowIcon = true; UseHallowAccent = true; LastCrashShown = ""; Alarms = new Dictionary<string, string>();
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
