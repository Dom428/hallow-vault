// Hallow Vault v1.6 - crash reports, kept on this PC (<data folder>\crashes\<time>.txt, newest 20). Nothing is ever sent.
// A report holds the app, versions, time and the exception (type, message, stack, inner exceptions) - never settings or file contents.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace HallowVault
{
    public static class Crash
    {
        public static string Dir { get { return Path.Combine(VaultSettings.Dir, "crashes"); } }
        static bool written;

        public static void Install(Application app)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Write(e.ExceptionObject as Exception);
            if (app != null) app.DispatcherUnhandledException += (s, e) => Write(e.Exception);   // not handled: Vault still closes, as before
            TaskScheduler.UnobservedTaskException += (s, e) => { Write(e.Exception); e.SetObserved(); };
        }

        public static string Write(Exception ex)
        {
            if (ex == null || Snap.On) return null;
            try
            {
                lock (typeof(Crash))
                {
                    if (written && !(ex is AggregateException)) return null;   // the same crash arrives through two handlers
                    written = true;
                    Directory.CreateDirectory(Dir);
                    var now = DateTime.Now; var file = Path.Combine(Dir, now.ToString("yyyy-MM-dd HH-mm-ss") + ".txt");
                    var sb = new StringBuilder();
                    sb.AppendLine("Hallow Vault " + Program.Version + " closed unexpectedly.");
                    sb.AppendLine("Time:    " + now.ToString("yyyy-MM-dd'T'HH:mm:ss"));
                    sb.AppendLine("Windows: " + WindowsVersion());
                    sb.AppendLine(".NET:    " + Environment.Version);
                    sb.AppendLine();
                    for (var e = ex; e != null; e = e.InnerException)
                    {
                        sb.AppendLine(e == ex ? "Exception: " + e.GetType().FullName : "Inner exception: " + e.GetType().FullName);
                        sb.AppendLine("Message:   " + e.Message);
                        sb.AppendLine(e.StackTrace ?? "(no stack)");
                        sb.AppendLine();
                    }
                    sb.AppendLine("This report stays on your PC - nothing is sent anywhere.");
                    File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                    foreach (var old in Reports().Skip(20)) try { File.Delete(old); } catch { }
                    return file;
                }
            }
            catch { return null; }
        }
        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null)
                    {
                        var name = k.GetValue("ProductName") as string ?? "Windows"; int build; int.TryParse(k.GetValue("CurrentBuildNumber") as string, out build);
                        if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");   // Windows 11 still says 10 here
                        return name + " " + (k.GetValue("DisplayVersion") as string) + " (build " + build + "." + k.GetValue("UBR") + ")";
                    }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        // newest first
        public static string[] Reports()
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "????-??-?? ??-??-??.txt").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray() : new string[0]; }
            catch { return new string[0]; }
        }
        public static DateTime? Last()
        {
            var f = Reports().FirstOrDefault(); if (f == null) return null;
            DateTime d; return DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out d) ? d : File.GetLastWriteTime(f);
        }
    }
}
