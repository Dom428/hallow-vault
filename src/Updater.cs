// Hallow Vault v1.8 - updates from GitHub releases (same as Hallow Hub) (asks first, checks the download's SHA-256 before installing).
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace HallowVault
{
    [DataContract] public class GhAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "browser_download_url")] public string Url;
        [DataMember(Name = "digest")] public string Digest;   // "sha256:..."
    }
    [DataContract] public class GhRelease
    {
        [DataMember(Name = "tag_name")] public string Tag;
        [DataMember(Name = "body")] public string Notes;
        [DataMember(Name = "assets")] public GhAsset[] Assets;
        public Version Version { get { Version v; return Version.TryParse((Tag ?? "").TrimStart('v', 'V'), out v) ? v : new Version(0, 0); } }
        public GhAsset Zip { get { return Assets == null ? null : Assets.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)); } }
    }

    public static class Updater
    {
        public const string Repo = "Dom428/hallow-vault";
        public static Version Current { get { return typeof(Updater).Assembly.GetName().Version; } }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "HallowVault";
            return wc;
        }

        // Newest release on GitHub, or null if it can't be reached.
        public static GhRelease Latest()
        {
            try
            {
                using (var wc = Client())
                {
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    var json = wc.DownloadData("https://api.github.com/repos/" + Repo + "/releases/latest");
                    using (var ms = new MemoryStream(json)) return (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(ms);
                }
            }
            catch { return null; }
        }
        public static bool IsNewer(GhRelease r) { return r != null && r.Zip != null && r.Version > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build)); }

        // Downloads the release zip, checks its SHA-256 against GitHub's, unpacks it and runs its installer
        // (which closes this copy, installs the new one and opens it). Returns an error message, or null when it started.
        public static string Install(GhRelease r)
        {
            var a = r.Zip; if (a == null) return "That release has no zip.";
            if (string.IsNullOrEmpty(a.Digest) || !a.Digest.StartsWith("sha256:")) return "GitHub didn't give a checksum for the download, so it wasn't installed.";
            var dir = Path.Combine(Path.GetTempPath(), "HallowVaultUpdate");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, "update.zip");
                using (var wc = Client()) wc.DownloadFile(a.Url, zip);
                string hash;
                using (var f = File.OpenRead(zip)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (hash != a.Digest.Substring(7).ToLowerInvariant()) return "The download didn't match GitHub's checksum, so it wasn't installed.";
                var x = Path.Combine(dir, "files");
                ZipFile.ExtractToDirectory(zip, x);
                var script = Directory.GetFiles(x, "Install.ps1", SearchOption.AllDirectories).FirstOrDefault();   // the zip has a "Hallow Vault" folder inside
                if (script == null) return "The update is missing its installer.";
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true });
                return null;
            }
            catch (Exception e) { return "Update failed: " + e.Message; }
        }
    }
}
