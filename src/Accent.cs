// Hallow Vault v1.6 - the shared Hallow accent colour (%APPDATA%\Hallow\theme.json, written by Hallow Toolkit).
// The theme is loaded from Theme.xaml with Vault's own orange swapped for the accent; a bad or missing file means orange.
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace HallowVault
{
    public static class Accent
    {
        public const string Own = "#F07820";
        const string OwnLight = "#F59A50";
        public static string Current = Own;

        public static string Folder { get { return Link.TestDir != null ? Path.Combine(Link.TestDir, "Hallow") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hallow"); } }
        // the family accent, or null when there is none (or the file is broken)
        public static string Shared()
        {
            try
            {
                var f = Path.Combine(Folder, "theme.json"); if (!File.Exists(f)) return null;
                string t; using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(s)) t = r.ReadToEnd();
                var m = Regex.Match(t, "\"accent\"\\s*:\\s*\"(#[0-9A-Fa-f]{6})\"");
                return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
            }
            catch { return null; }
        }
        public static string Want(bool useShared) { var s = useShared ? Shared() : null; return s ?? Own; }

        // (re)loads the theme with this accent
        public static void Apply(Application app, string accent)
        {
            string xaml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowVault.Theme.xaml")) using (var r = new StreamReader(s)) xaml = r.ReadToEnd();
            if (!string.Equals(accent, Own, StringComparison.OrdinalIgnoreCase))
                xaml = Regex.Replace(xaml, Own, accent, RegexOptions.IgnoreCase).Replace(OwnLight, Lighter(accent));
            var d = (ResourceDictionary)XamlReader.Parse(xaml);
            app.Resources.MergedDictionaries.Clear(); app.Resources.MergedDictionaries.Add(d);
            Current = accent;
        }
        static string Lighter(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Func<byte, byte> up = v => (byte)(v + (255 - v) * 0.3);
            return string.Format("#{0:X2}{1:X2}{2:X2}", up(c.R), up(c.G), up(c.B));
        }
        public static SolidColorBrush Brush() { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Current)); }

        // calls `changed` (on the UI thread, 300 ms after the last change) whenever theme.json changes; the folder may appear later
        public static void Watch(System.Windows.Threading.Dispatcher ui, Action changed)
        {
            FileSystemWatcher w = null;
            var debounce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(300) };
            debounce.Tick += (s, e) => { debounce.Stop(); try { changed(); } catch { } };
            FileSystemEventHandler poke = (s, e) => ui.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
            Action tryWatch = () =>
            {
                if (w != null || !Directory.Exists(Folder)) return;
                try
                {
                    w = new FileSystemWatcher(Folder, "theme.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime };
                    w.Changed += poke; w.Created += poke; w.Deleted += poke; w.Renamed += (s, e) => poke(s, null);
                    w.EnableRaisingEvents = true;
                    poke(null, null);   // it may have been written before we watched
                }
                catch { w = null; }
            };
            tryWatch();
            var look = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(10) };
            look.Tick += (s, e) => { if (w == null) tryWatch(); else look.Stop(); };
            look.Start();
        }
    }
}
