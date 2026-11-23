// Hallow Vault - backs up your game saves and settings (and the Hallow apps' own settings) as zip files,
// when a game closes and once a day, and puts any version back. WPF built in code (C# 5 / .NET Framework 4.8).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Vault")]
[assembly: AssemblyProduct("Hallow Vault")]
[assembly: AssemblyDescription("Backs up your game saves and the whole Hallow family, and puts any version back")]
[assembly: AssemblyVersion("1.16.0.0")]
[assembly: AssemblyFileVersion("1.16.0.0")]

namespace HallowVault
{
    public static class Program
    {
        public const string Version = "1.16";
        [STAThread]
        public static void Main(string[] args)
        {
            // Hallow family link: send one command to the running copy and print the reply (never opens a window)
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Link.Command(string.Join(" ", args.Skip(ci + 1)))); return; }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; VaultSettings.Frozen = true;
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
                new VaultWindow(true).SnapAll(args[si + 1]); return;
            }
            bool created;
            var sfx = Link.TestDir != null ? ".test" : "";   // a test copy runs beside the real one
            var mutex = new Mutex(true, @"Local\HallowVault" + sfx, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowVaultShow" + sfx);
            if (!created) { showEvent.Set(); return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Crash.Install(app); LoadTheme(app);
            var w = new VaultWindow(args.Contains("--tray"));
            // test copies only (HALLOW_FAMILY_TEST): crash on purpose to check the crash report
            if (Link.TestDir != null && args.Contains("--test-crash")) w.Dispatcher.BeginInvoke(new Action(() => { throw new InvalidOperationException("Test crash (asked for with --test-crash)"); }), DispatcherPriority.ApplicationIdle);
            new Thread(() => { while (showEvent.WaitOne()) w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        static void LoadTheme(Application app) { var s = VaultSettings.Load(); Accent.Apply(app, Accent.Want(s.UseHallowAccent), Look.Want(s.Look)); }   // v1.6: with the Hallow accent; v1.14: light or dark
    }

    public partial class VaultWindow : Window
    {
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static SolidColorBrush B(string hex) { return Look.B(hex); }   // v1.14: written with the dark values, swapped for the light ones in light mode (v1.15: tagged, so open windows recolour)
        static SolidColorBrush T(string hex) { return Look.T(hex); }   // v1.15: the same colour as small text (darker green / amber on light)

        readonly VaultSettings S = VaultSettings.Load();
        readonly bool startHidden; bool exiting, toldTray;
        List<Game> games = new List<Game>();
        readonly Dictionary<string, List<SaveRoot>> roots = new Dictionary<string, List<SaveRoot>>();
        readonly Dictionary<string, List<BackupInfo>> history = new Dictionary<string, List<BackupInfo>>();
        readonly Tracker tracker = new Tracker();
        string steamUser;
        StackPanel list; TextBlock summary, status; Border settingsPanel; ToggleButton gearBtn; Button allBtn;
        WF.NotifyIcon tray; DispatcherTimer poll, hourly, updWatch, hostWatch;
        readonly Dictionary<string, bool> updState = new Dictionary<string, bool>();   // v1.2: Steam app id -> update waiting/running
        readonly Dictionary<string, int> settleTries = new Dictionary<string, int>();   // v1.2: game id -> times we waited for its files to settle
        readonly object busy = new object(); bool working; readonly Queue<Tuple<Game, string>> queue = new Queue<Tuple<Game, string>>();

        string Root { get { return !string.IsNullOrEmpty(S.VaultDir) ? S.VaultDir : Backups.DefaultRoot(); } }
        static Game Hallow() { return new Game { Id = "hallow", Name = "Hallow apps", Source = "hallow" }; }

        public VaultWindow(bool hidden)
        {
            startHidden = hidden;
            // v1.9: your include / leave-out patterns and the compression level
            Saves.Filters = id => { List<string> inc, exc; S.Include.TryGetValue(id, out inc); S.Exclude.TryGetValue(id, out exc); return inc == null && exc == null ? null : Tuple.Create((inc ?? new List<string>()).ToArray(), (exc ?? new List<string>()).ToArray()); };
            ApplyCompression();
            // v1.11: the online save database (loaded from its cached index - fast)
            Saves.ManifestFor = g => S.UseManifest ? Manifest.For(g) : null; Saves.ManifestConfig = S.ManifestConfig;
            if (S.UseManifest) { var lsw = Stopwatch.StartNew(); if (Manifest.Load()) manifestLoadMs = lsw.ElapsedMilliseconds; }
            if (Link.TestDir != null) Family.Home = System.IO.Path.Combine(Link.TestDir, "profile");   // v1.16: test copies back up a fake family, never the real one
            Title = "Hallow Vault"; Width = 1060; Height = 740; MinWidth = 880; MinHeight = 560;
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text"); FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = AppIcon(); } catch { }
            Content = Build();
            Look.Follow(this);   // v1.14: the title bar matches light / dark
            Closing += (s, e) =>
            {
                if (!exiting && S.TrayOnClose && !Snap.On)
                {
                    e.Cancel = true; Hide();
                    if (!toldTray && tray != null) { toldTray = true; Notify("Hallow Vault", hosted ? "Still running - it backs up your saves when a game closes. It's in the Hallow icon's menu." : "Still running in the tray - it backs up your saves when a game closes. Right-click the icon to quit."); }
                }
                else Quit();
            };
            Rescan();
            if (Snap.On) return;
            SetupTray();
            tracker.Stopped = (g, since) =>
            {
                var main = g.Parent ?? g;
                if (!S.OnClose || S.Off.Contains(main.Id)) return;
                // give the game a few seconds to finish writing, then back up if anything changed
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t.Tick += (o, e) => { t.Stop(); Enqueue(main, main.Name + " closed"); };
                t.Start();
            };
            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            poll.Tick += (s, e) => { try { tracker.Poll(games.Concat(games.SelectMany(x => x.Alts)).ToList()); } catch { } };
            poll.Start();
            hourly = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            hourly.Tick += (s, e) => CheckDaily();
            hourly.Start();
            updWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            updWatch.Tick += (s, e) => CheckSteamUpdates();
            updWatch.Start();
            hostWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            hostWatch.Tick += (s, e) => CheckTrayHost();
            hostWatch.Start(); CheckTrayHost();
            sched = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };   // v1.9: every N hours
            sched.Tick += (s, e) => CheckSchedule();
            sched.Start();
            hourly.Tick += (s, e) => CheckManifest(false);   // v1.11: weekly re-check (asked every half hour, acts once a week)
            hourly.Tick += (s, e) => CheckWeekly();   // v1.12: the weekly backup check (off by default)
            var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) }; firstCheck.Tick += (s, e) => { firstCheck.Stop(); CheckWeekly(); }; firstCheck.Start();
            Dispatcher.BeginInvoke(new Action(() => CheckManifest(false)), DispatcherPriority.ApplicationIdle);
            copyWatch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) }; copyWatch.Tick += (s, e) => CheckCopy(); copyWatch.Start();   // v1.10
            Dispatcher.BeginInvoke(new Action(CheckCopy), DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(new Action(CheckDaily), DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(new Action(CheckSteamUpdates), DispatcherPriority.ApplicationIdle);
            DateTime lastCheck; if (!DateTime.TryParse(S.LastUpdateCheck, out lastCheck) || (DateTime.Now - lastCheck).TotalHours > 20) Dispatcher.BeginInvoke(new Action(() => CheckForUpdate(false)), DispatcherPriority.ApplicationIdle);
            hourly.Tick += (s, e) => { DateTime lc; if (!DateTime.TryParse(S.LastUpdateCheck, out lc) || (DateTime.Now - lc).TotalHours > 20) CheckForUpdate(false); };
            FamilyStart();   // v1.16: the Hallow family backup (daily, and after changes)
            Link.Serve(LinkCommand);
            // What's new after an update (1.0 didn't remember the version, so someone with backups already is coming from 1.0)
            if (string.IsNullOrEmpty(S.LastSeenVersion)) S.LastSeenVersion = S.LastSig.Count > 0 ? "1.0" : Program.Version;
            S.Save();
            IsVisibleChanged += (s, e) => { if (IsVisible) { Dispatcher.BeginInvoke(new Action(ShowWhatsNew), DispatcherPriority.ApplicationIdle); Dispatcher.BeginInvoke(new Action(ShowCrash), DispatcherPriority.ApplicationIdle); } };
            Accent.Watch(Dispatcher, ApplyAccent);
            // v1.14: "Same as Windows" follows Windows' app mode live (a registry look every 3 s, only acts on a change)
            lastSysLight = Look.SystemLight();
            sysWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            sysWatch.Tick += (s, e) => { var v = Look.SystemLight(); if (v != lastSysLight) { lastSysLight = v; ApplyAccent(); } };
            sysWatch.Start();
            if (!startHidden) Show();
        }

        // ================================================================ v1.6: Hallow accent and crash reports
        DispatcherTimer sysWatch; bool? lastSysLight;
        // the accent and (v1.14) light / dark: theme.json changed, Windows' mode changed, or a setting - rebuilds the window when either changed
        void ApplyAccent()
        {
            var want = Accent.Want(S.UseHallowAccent); var light = Look.Want(S.Look);
            if (string.Equals(want, Accent.Current, StringComparison.OrdinalIgnoreCase) && light == Accent.CurrentLight) return;
            var oldDict = Accent.Dict;
            Accent.Apply(Application.Current, want, light);
            // v1.15: windows that are already open (dialogs, What's new, the backup check) are recoloured in place - nothing typed is lost
            foreach (Window ow in Application.Current.Windows) if (ow != this) { try { Look.Restyle(ow, oldDict, Accent.Dict); } catch { } }
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text"); Look.TitleBar(this);
            var open = settingsPanel != null && settingsPanel.Visibility == Visibility.Visible; var msg = status == null ? null : status.Text;
            Content = Build(); Fill();
            if (open) { gearBtn.IsChecked = true; settingsPanel.Visibility = Visibility.Visible; }
            if (msg != null) Status(msg);
        }
        // once per crash report: "Hallow Vault closed unexpectedly last time"
        void ShowCrash()
        {
            if (!IsVisible || Snap.On) return;
            var f = Crash.Reports().FirstOrDefault(); if (f == null || System.IO.Path.GetFileName(f) == S.LastCrashShown) return;
            S.LastCrashShown = System.IO.Path.GetFileName(f); S.Save();
            BuildCrash(f, this).Show();
        }
        Window BuildCrash(string file, Window owner)
        {
            var w = Dialog("Hallow Vault", 480, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Hallow Vault closed unexpectedly last time", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
            var when = Crash.Last();
            sp.Children.Add(new TextBlock { Text = (when == null ? "" : "On " + when.Value.ToString("dddd d MMM, HH:mm") + ". ") + "A report was saved on this PC - nothing was sent anywhere. Your backups are fine.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var det = new Button { Content = "Show details", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            det.Click += (s, e) => { try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { } };
            var fol = new Button { Content = "Open folder", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            fol.Click += (s, e) => { try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } };
            var ok = new Button { Content = "OK", Style = (Style)FindResource("Accent"), MinWidth = 80, IsDefault = true, IsCancel = true };
            ok.Click += (s, e) => w.Close();
            row.Children.Add(det); row.Children.Add(fol); row.Children.Add(ok); sp.Children.Add(row);
            w.Content = sp; return w;
        }

        // ================================================================ v1.11: the online save database
        TextBlock manState; long manifestLoadMs = -1; bool manChecking;
        void ShowManifestState()
        {
            if (manState == null) return;
            if (!S.UseManifest) { manState.Text = "Off - Vault uses only its own detection."; manState.Foreground = (Brush)FindResource("Muted"); return; }
            if (manChecking && !Manifest.Loaded) { manState.Text = "Downloading the save database..."; manState.Foreground = T("#F0BE5A"); return; }
            if (!Manifest.Loaded) { manState.Text = "Not downloaded yet - Vault uses its own detection until it is."; manState.Foreground = T("#F0BE5A"); return; }
            manState.Text = Manifest.Count.ToString("N0") + " games  \u00B7  updated " + VaultSettings.Ago(Manifest.Updated) + (manChecking ? "  \u00B7  checking for a newer one..." : ""); manState.Foreground = T("#6ED28C");
        }
        // first run: download; then once a week: re-download only when GitHub's sha changed. A failure keeps the old copy.
        void CheckManifest(bool now)
        {
            if (Snap.On || !S.UseManifest || manChecking) return;
            if (!now && Manifest.Loaded && !Manifest.DueForCheck()) return;
            manChecking = true; ShowManifestState();
            new Thread(() =>
            {
                bool changed; var msg = Manifest.Refresh(out changed);
                var sw2 = Stopwatch.StartNew(); var loaded = changed ? Manifest.Load() : Manifest.Loaded; var ms = sw2.ElapsedMilliseconds;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    manChecking = false;
                    if (msg != null) { Log.Add("Save database", msg + (changed ? " (index loaded in " + ms + " ms)" : "")); Status(msg); }
                    if (changed || (now && loaded)) Rescan();
                    ShowManifestState();
                }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        // ================================================================ v1.12: check backups (read-only) and games you add
        bool checking; string checkProgress; List<FolderCheck> checkResults; string checkScope;   // checkScope: null = the whole vault, else one game folder
        readonly Dictionary<string, ZipCheck> zipChecks = new Dictionary<string, ZipCheck>(StringComparer.OrdinalIgnoreCase);
        Window checkWin; StackPanel checkBody; TextBlock checkState; Button checkBtn;
        Dictionary<string, string> FolderNames(string root)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in games) { var f = System.IO.Path.GetFileName(Backups.GameDir(root, x)); if (!d.ContainsKey(f)) d[f] = x.Name; }
            return d;
        }
        // g = null: every backup in the vault. quiet: no window (the weekly check, the family link). notify: tell you when something's wrong.
        void RunCheck(Game g, bool quiet = false, bool notify = false)
        {
            if (checking) { if (!quiet) { ShowCheckWindow(); Status("A check is already running."); } return; }
            checking = true; var root = Root; var names = FolderNames(root);
            var dir = g == null ? null : Backups.GameDir(root, g); var gname = g == null ? null : g.Name;
            checkProgress = g == null ? "Checking every backup..." : "Checking " + Poss(g.Name) + " backups...";
            if (!quiet && !Snap.On) ShowCheckWindow();
            Status(checkProgress); ShowCheckState(); RefreshCheckWindow();
            new Thread(() =>
            {
                List<FolderCheck> res; string err = null;
                try
                {
                    res = g == null ? Checker.All(root, names, (i, n) => Dispatcher.BeginInvoke(new Action(() => { checkProgress = "Checking " + (i + 1) + " of " + n + (n == 1 ? " game..." : " games..."); ShowCheckState(); RefreshCheckWindow(); })))
                        : new List<FolderCheck> { Checker.Folder(dir, gname) };
                }
                catch (Exception ex) { res = new List<FolderCheck>(); err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => CheckDone(g, dir, res, err, notify)));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }
        void ApplyCheck(string dir, List<FolderCheck> res)
        {
            foreach (var f in res) { if (f.Zips.Count > 0 || dir != null) S.Checks[f.Folder] = Checker.Pack(f); foreach (var z in f.Zips) zipChecks[z.File] = z; }
            if (dir == null)
            {
                foreach (var k in S.Checks.Keys.ToList()) if (!res.Any(f => f.Folder.Equals(k, StringComparison.OrdinalIgnoreCase))) S.Checks.Remove(k);   // folders with no backups left
                S.LastCheck = DateTime.Now.ToString("o");
            }
            checkResults = res; checkScope = dir;
        }
        void CheckDone(Game g, string dir, List<FolderCheck> res, string err, bool notify)
        {
            checking = false;
            if (err != null) { Status("Couldn't check the backups: " + err); Log.Add(g == null ? "Backup check" : g.Name, "check failed: " + err); ShowCheckState(); RefreshCheckWindow(); return; }
            ApplyCheck(dir, res); S.Save();
            var msg = CheckMessage(res, g);
            Log.Add(g == null ? "Backup check" : g.Name, "checked: " + msg);
            Status(msg);
            if (notify && res.Any(f => f.Level > 0)) Notify("Hallow Vault - backup check", msg + (hosted ? " (Hallow Vault > Check backups)" : ""));
            Fill(); ShowCheckState(); RefreshCheckWindow();
        }
        static string CheckMessage(List<FolderCheck> res, Game g)
        {
            var zips = res.SelectMany(f => f.Zips).Where(z => z.Level >= 0).ToList();
            int red = zips.Count(z => z.Level == 2), amber = zips.Count(z => z.Level == 1), extra = res.Count(f => f.Notes.Any(n => n.Contains("unfinished")));
            var who = g == null ? res.Count + (res.Count == 1 ? " game, " : " games, ") : Poss(g.Name) + " ";
            if (zips.Count == 0) return g == null ? "No backups to check yet." : g.Name + " has no backups to check yet.";
            var s = "Checked " + who + zips.Count + (zips.Count == 1 ? " backup" : " backups") + ": ";
            if (red == 0 && amber == 0) s += zips.Count == 1 ? "it's fine." : "all fine.";
            else s += (red > 0 ? red + " damaged" : "") + (red > 0 && amber > 0 ? ", " : "") + (amber > 0 ? amber + " worth a look" : "") + ", " + (zips.Count - red - amber) + " fine.";
            if (extra > 0) s += " Unfinished backup files in " + extra + (extra == 1 ? " folder." : " folders.");
            return s + " Nothing was changed.";
        }
        void CheckWeekly()
        {
            if (!S.WeeklyCheck || checking || Snap.On) return;
            DateTime last; if (DateTime.TryParse(S.LastCheck, out last) && (DateTime.Now - last).TotalDays < 7) return;
            RunCheck(null, true, true);
            if (!string.IsNullOrEmpty(S.CopyDir)) VerifyCopyNow(true);   // v1.13: and the second copy, by SHA-256
        }
        // the coloured dot next to a game (its last check)
        FrameworkElement CheckDot(string gameDir)
        {
            string packed; if (!S.Checks.TryGetValue(System.IO.Path.GetFileName(gameDir), out packed)) return null;
            int level; DateTime when; string sum; if (!Checker.Unpack(packed, out level, out when, out sum)) return null;
            level = Math.Max(0, Math.Min(2, level));
            var e = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = B(Checker.Colors[level]), Margin = new Thickness(9, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var host = new Border { Background = Brushes.Transparent, ToolTip = "Backup check " + VaultSettings.Ago(when) + ": " + sum + (level > 0 ? " (Check backups for details)" : "") };
            if (level == 0) host.Child = e;
            else host.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { e, new TextBlock { Text = Checker.Words[level], FontSize = 10.5, Foreground = T(Checker.Colors[level]), Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center } } };
            return host;
        }
        void ShowCheckState()
        {
            if (checkState == null) return;
            if (checking) { checkState.Text = checkProgress; checkState.Foreground = T("#F0BE5A"); return; }
            DateTime last;
            if (!DateTime.TryParse(S.LastCheck, out last)) { checkState.Text = "Not checked yet."; checkState.Foreground = (Brush)FindResource("Muted"); return; }
            int red = 0, amber = 0, n = 0;
            foreach (var v in S.Checks.Values) { int l; DateTime w; string su; if (Checker.Unpack(v, out l, out w, out su)) { n++; if (l == 2) red++; else if (l == 1) amber++; } }
            if (red + amber == 0) { checkState.Text = "Last check " + VaultSettings.Ago(last) + ": " + (n == 1 ? "1 game's backups fine." : "all " + n + " games' backups fine."); checkState.Foreground = T(Checker.Colors[0]); }
            else { checkState.Text = "Last check " + VaultSettings.Ago(last) + ": " + (red > 0 ? red + (red == 1 ? " game has" : " games have") + " damaged backups" : "") + (red > 0 && amber > 0 ? ", " : "") + (amber > 0 ? amber + (amber == 1 ? " game needs" : " games need") + " a look" : "") + " - Check all now for details."; checkState.Foreground = T(Checker.Colors[red > 0 ? 2 : 1]); }
        }
        void ShowCheckWindow()
        {
            if (checkWin != null) { try { checkWin.Activate(); } catch { } return; }
            checkWin = BuildCheck(this); checkWin.Closed += (s, e) => { checkWin = null; checkBody = null; };
            checkWin.Show();
        }
        Window BuildCheck(Window owner)
        {
            var w = Dialog("Check backups", 700, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Check backups", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Every zip must open, every file in it must match its checksum, and the files must match the list Vault wrote down when it made the backup. This only reads - nothing is changed or deleted.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            checkBody = new StackPanel(); sp.Children.Add(checkBody);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var again = new Button { Content = "Check again", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            again.Click += (s, e) => { var g = checkScope == null ? null : games.FirstOrDefault(x => string.Equals(Backups.GameDir(Root, x), checkScope, StringComparison.OrdinalIgnoreCase)); RunCheck(g); };
            var close = new Button { Content = "Close", Style = (Style)FindResource("Btn"), IsCancel = true };
            close.Click += (s, e) => w.Close();
            row.Children.Add(again); row.Children.Add(close); sp.Children.Add(row);
            w.Content = sp; RefreshCheckWindow(); return w;
        }
        void RefreshCheckWindow()
        {
            if (checkBody == null) return;
            checkBody.Children.Clear();
            if (checking) { checkBody.Children.Add(new TextBlock { Text = checkProgress, Foreground = T("#F0BE5A"), FontWeight = FontWeights.SemiBold }); return; }
            var res = checkResults;
            if (res == null) { checkBody.Children.Add(new TextBlock { Text = "Not checked yet.", Foreground = (Brush)FindResource("Muted") }); return; }
            var head = new TextBlock { Text = CheckMessage(res, checkScope == null ? null : new Game { Name = res.Count > 0 ? res[0].Name : "This game" }), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                Foreground = T(Checker.Colors[res.Count == 0 ? 0 : Math.Max(0, res.Max(f => f.Level))]) };
            checkBody.Children.Add(head);
            var lst = new StackPanel();
            foreach (var f in res.OrderByDescending(x => x.Level).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var lv = Math.Max(0, f.Level);
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = B(Checker.Colors[lv]), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(dot, Dock.Left); d.Children.Add(dot);
                var nm = new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, Width = 210, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Dir }; DockPanel.SetDock(nm, Dock.Left); d.Children.Add(nm);
                d.Children.Add(new TextBlock { Text = f.Summary, TextWrapping = TextWrapping.Wrap, Foreground = lv == 0 ? (Brush)FindResource("Muted") : T(Checker.Colors[lv]) });
                lst.Children.Add(d);
                foreach (var z in f.Zips.Where(x => x.Level > 0))
                {
                    var zd = new DockPanel { Margin = new Thickness(20, 0, 0, 4) };
                    var zz = z; var show = new Button { Content = "Open folder", Tag = "icon", Width = 28, Height = 26, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Show this backup in its folder" };
                    show.Click += (s, e) => { try { Process.Start("explorer.exe", "/select,\"" + zz.File + "\""); } catch { } };
                    DockPanel.SetDock(show, Dock.Right); zd.Children.Add(show);
                    var bi = System.IO.Path.GetFileNameWithoutExtension(z.File); DateTime made;
                    var label = DateTime.TryParseExact(bi, "yyyy-MM-dd HH.mm.ss", null, System.Globalization.DateTimeStyles.None, out made) ? made.ToString("d MMM yyyy, HH:mm") : System.IO.Path.GetFileName(z.File);
                    zd.Children.Add(new TextBlock { Text = label + " - " + Checker.Words[z.Level] + ": " + z.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = T(Checker.Colors[z.Level]) });
                    lst.Children.Add(zd);
                }
            }
            if (res.Count == 0) lst.Children.Add(new TextBlock { Text = "No backups in " + Root + " yet.", Foreground = (Brush)FindResource("Muted") });
            checkBody.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (res.Any(f => f.Level == 2))
                checkBody.Children.Add(new TextBlock { Text = "A damaged backup can't be put back in full. Vault keeps it anyway (nothing is deleted) and your other versions aren't affected - press Back up on that game for a fresh copy, and pin a good version if you want it kept.", Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        }

        // add or edit a game no launcher knows (c = null: a new one)
        Window BuildCustom(CustomGame c, Window owner)
        {
            var w = Dialog(c == null ? "Add a game" : "Edit " + c.Name, 580, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = c == null ? "Add a game" : "Edit " + c.Name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = "For a game no launcher knows - a DRM-free copy, an emulator, an old game. Vault backs up its save folders like any other game: daily, when you press Back up, and when it closes if you pick its program.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            sp.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var nameBox = new TextBox { Style = (Style)FindResource("Field"), Text = c == null ? "" : c.Name }; sp.Children.Add(nameBox);
            sp.Children.Add(new TextBlock { Text = "Save folders", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            var folders = c == null ? new List<string>() : c.Folders.ToList();
            var fl = new StackPanel(); sp.Children.Add(fl);
            Action redraw = null;
            redraw = () =>
            {
                fl.Children.Clear();
                if (folders.Count == 0) fl.Children.Add(new TextBlock { Text = "None yet - add the folder where the game keeps its saves.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 4) });
                foreach (var f in folders.ToList())
                {
                    var ff = f; var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var rm = new Button { Content = "Remove", Tag = "icon", Width = 28, Height = 26, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Stop backing up this folder (backups already made stay)" };
                    rm.Click += (s, e) => { folders.Remove(ff); redraw(); };
                    DockPanel.SetDock(rm, Dock.Right); d.Children.Add(rm);
                    var there = Directory.Exists(ff) || File.Exists(ff);
                    d.Children.Add(new TextBlock { Text = ff + (there ? "" : "   (not found)"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = ff, VerticalAlignment = VerticalAlignment.Center, Foreground = there ? (Brush)FindResource("Text") : T("#F0BE5A") });
                    fl.Children.Add(d);
                }
            };
            redraw();
            var addF = new Button { Content = "Add folder...", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            addF.Click += (s, e) =>
            {
                using (var d = new WF.FolderBrowserDialog { Description = "Pick a folder where the game keeps its saves", ShowNewFolderButton = false })
                {
                    if (folders.Count > 0) d.SelectedPath = folders[folders.Count - 1];
                    if (d.ShowDialog() != WF.DialogResult.OK) return;
                    if (!folders.Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) folders.Add(d.SelectedPath);
                    redraw();
                }
            };
            sp.Children.Add(addF);
            sp.Children.Add(new TextBlock { Text = "The game's program (optional)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            var exe = c == null ? null : c.Exe;
            var exeRow = new DockPanel();
            var exeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var clear = new Button { Content = "Clear", Tag = "icon", Width = 28, Height = 26, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "No program" };
            var pickExe = new Button { Content = "Pick .exe...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            Action showExe = () => { exeText.Text = string.IsNullOrEmpty(exe) ? "None" : exe; exeText.ToolTip = exe; exeText.Foreground = string.IsNullOrEmpty(exe) ? (Brush)FindResource("Muted") : (Brush)FindResource("Text"); clear.Visibility = string.IsNullOrEmpty(exe) ? Visibility.Collapsed : Visibility.Visible; };
            clear.Click += (s, e) => { exe = null; showExe(); };
            pickExe.Click += (s, e) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = "Pick the game's .exe", Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
                if (!string.IsNullOrEmpty(exe)) try { d.InitialDirectory = System.IO.Path.GetDirectoryName(exe); } catch { }
                if (d.ShowDialog(w) == true) { exe = d.FileName; showExe(); }
            };
            showExe();
            DockPanel.SetDock(clear, Dock.Right); exeRow.Children.Add(clear); DockPanel.SetDock(pickExe, Dock.Right); exeRow.Children.Add(pickExe); exeRow.Children.Add(exeText); sp.Children.Add(exeRow);
            sp.Children.Add(new TextBlock { Text = "With its program, Vault notices when the game is running and backs up a few seconds after it closes - only that exact .exe counts.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var err = new TextBlock { Foreground = T("#F07070"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed }; sp.Children.Add(err);
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            if (c != null)
            {
                var remove = new Button { Content = "Remove game", Style = (Style)FindResource("Btn") };
                remove.Click += (s, e) =>
                {
                    var dir = Backups.GameDir(Root, c.ToGame()); var n = Backups.ZipsIn(dir).Count;
                    if (MessageBox.Show(w, "Remove " + c.Name + " from Hallow Vault?\n\n" + (n > 0 ? "Its " + n + (n == 1 ? " backup stays" : " backups stay") + " in " + dir + " - nothing is deleted. Add the game again with the same name to see " + (n == 1 ? "it" : "them") + " again." : "Nothing is deleted."), "Remove game", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    var nm = c.Name; CustomGame.Remove(S, c); S.Save(); w.Close(); Rescan();
                    Log.Add(nm, "removed from Vault (its backups were kept)");
                    Status(nm + " removed from Vault." + (n > 0 ? " Its backups are still in " + dir + "." : ""));
                };
                DockPanel.SetDock(remove, Dock.Left); row.Children.Add(remove);
            }
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var save = new Button { Content = "Save", Style = (Style)FindResource("Accent"), MinWidth = 80, IsDefault = true };
            save.Click += (s, e) =>
            {
                var taken = games.Where(x => c == null || x.Id != c.Id).Select(x => x.Name).ToList();
                CustomGame saved; var problem = CustomGame.Save(S, c, nameBox.Text, folders, exe, taken, out saved);
                if (problem != null) { err.Text = problem; err.Visibility = Visibility.Visible; return; }
                S.Save(); w.Close(); Rescan();
                var g = games.FirstOrDefault(x => x.Id == saved.Id);
                Log.Add(saved.Name, c == null ? "added by you (" + saved.Folders.Count + (saved.Folders.Count == 1 ? " folder" : " folders") + ")" : "edited");
                var found = g != null && roots.ContainsKey(g.Id) && roots[g.Id].Count > 0;
                Status(saved.Name + (c == null ? " added" : " saved") + (found ? " - it's backed up like any other game." : " - no files in its save folders yet, so there's nothing to back up for now."));
                if (c == null && found && !S.Off.Contains(g.Id)) Enqueue(g, "you added it");   // a first backup right away
            };
            btns.Children.Add(cancel); btns.Children.Add(save); row.Children.Add(btns); sp.Children.Add(row);
            w.Content = sp; return w;
        }

        // ================================================================ v1.9: schedule, compression, filters, log
        DispatcherTimer sched;
        void ApplyCompression() { Backups.Level = S.Compression == "fast" ? System.IO.Compression.CompressionLevel.Fastest : S.Compression == "none" ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal; }
        void CheckSchedule()
        {
            if (S.EveryHours <= 0) return;
            DateTime last; if (DateTime.TryParse(S.LastScheduled, out last) && (DateTime.Now - last).TotalHours < S.EveryHours) return;
            S.LastScheduled = DateTime.Now.ToString("o"); S.Save();
            foreach (var g in Backable()) Enqueue(g, "every " + (S.EveryHours == 1 ? "hour" : S.EveryHours + " hours"));
        }
        Window BuildLog(Window owner)
        {
            var w = Dialog("Backup log", 760, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Backup log", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Newest first - what was backed up, skipped, refused or failed. It stays on this PC.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 10) });
            var lst = new StackPanel(); var lines = Log.Recent(300);
            if (lines.Count == 0) lst.Children.Add(new TextBlock { Text = "Nothing yet.", Foreground = (Brush)FindResource("Muted") });
            foreach (var l in lines)
            {
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                var t = new TextBlock { Text = l[0].Length >= 16 ? l[0].Substring(5, 11) : l[0], Width = 92, Foreground = (Brush)FindResource("Muted") }; DockPanel.SetDock(t, Dock.Left); d.Children.Add(t);
                var gname = new TextBlock { Text = l[1], Width = 150, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }; DockPanel.SetDock(gname, Dock.Left); d.Children.Add(gname);
                var what = l[2]; var col = what.StartsWith("backed up") || what.StartsWith("restored") || what.StartsWith("imported") ? "#6ED28C" : what.StartsWith("refused") || what.StartsWith("failed") ? "#F07070" : what.StartsWith("waiting") ? "#F0BE5A" : null;
                d.Children.Add(new TextBlock { Text = what, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = what, Foreground = col == null ? (Brush)FindResource("Muted") : T(col) });
                lst.Children.Add(d);
            }
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var open = new Button { Content = "Open folder", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (s, e) => { try { Process.Start("explorer.exe", System.IO.File.Exists(Log.File) ? "/select,\"" + Log.File + "\"" : "\"" + VaultSettings.Dir + "\""); } catch { } };
            var close = new Button { Content = "Close", Style = (Style)FindResource("Btn"), IsCancel = true };
            row.Children.Add(open); row.Children.Add(close); sp.Children.Add(row);
            w.Content = sp; return w;
        }
        // include / leave-out patterns for one game, with a live count of what a backup would hold
        Window BuildFilters(Game g, Window owner)
        {
            var w = Dialog("Files to back up - " + g.Name, 560, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Files to back up for " + g.Name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = "One pattern per line. * is any text, ? one character. \"*.sav\" matches by file name anywhere; \"Screenshots\\*\" matches a folder inside the save folder.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            List<string> inc0, exc0; S.Include.TryGetValue(g.Id, out inc0); S.Exclude.TryGetValue(g.Id, out exc0);
            Func<string, List<string>, TextBox> box = (label, cur) =>
            {
                sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
                var tb = new TextBox { Style = (Style)FindResource("Field"), AcceptsReturn = true, Height = 76, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = cur == null ? "" : string.Join("\r\n", cur), FontFamily = new FontFamily("Consolas") };
                sp.Children.Add(tb); return tb;
            };
            var inc = box("Only back up (empty = everything)", inc0); var exc = box("Leave out", exc0);
            var count = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(count);
            Func<TextBox, List<string>> lines = tb => tb.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            var rs0 = Saves.Find(g, steamUser, ExtraOf(g));
            Action recount = () =>
            {
                int n = 0, all = 0; long b = 0; var i = lines(inc).ToArray(); var x = lines(exc).ToArray();
                foreach (var r in rs0.Where(r => !Reg.Is(r.Path)))
                {
                    var probe = new SaveRoot { Path = r.Path }; var keep = new SaveRoot { Path = r.Path, Include = i, Exclude = x };
                    foreach (var f in Saves.FilesOf(probe)) { all++; if (Saves.Wanted(f.Item2, keep)) { n++; try { b += new FileInfo(f.Item1).Length; } catch { } } }
                }
                count.Text = "With these rules a backup holds " + n + " of " + all + " files (" + VaultSettings.Size(b) + ")" + (rs0.Any(r => Reg.Is(r.Path)) ? ", plus its registry key." : ".");
            };
            inc.TextChanged += (s, e) => recount(); exc.TextChanged += (s, e) => recount(); recount();
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var save = new Button { Content = "Save", Style = (Style)FindResource("Accent"), MinWidth = 80 };
            save.Click += (s, e) =>
            {
                var i = lines(inc); var x = lines(exc);
                if (i.Count > 0) S.Include[g.Id] = i; else S.Include.Remove(g.Id);
                if (x.Count > 0) S.Exclude[g.Id] = x; else S.Exclude.Remove(g.Id);
                S.LastSig.Remove(g.Id); S.Save(); w.Close();
                roots[g.Id] = Saves.Find(g, steamUser, ExtraOf(g)); Fill();
                Status(i.Count + x.Count == 0 ? "Everything in " + g.Name + "'s save folders is backed up again." : "Saved - " + g.Name + "'s next backup follows these rules.");
            };
            row.Children.Add(cancel); row.Children.Add(save); sp.Children.Add(row);
            w.Content = sp; return w;
        }

        // ================================================================ v1.8: updates from GitHub (asks first, checks the SHA-256)
        Button updateBtn; GhRelease pending;
        void CheckForUpdate(bool manual)
        {
            if (Snap.On) return;
            if (manual) Status("Checking GitHub for a newer Hallow Vault...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) Status("Couldn't reach GitHub - try again later."); return; }
                    if (!Updater.IsNewer(r)) { if (manual) Status("You have the newest version (v" + Program.Version + ")."); return; }
                    pending = r; updateBtn.Content = "Update to v" + r.Version.ToString(2); updateBtn.Visibility = Visibility.Visible;
                    updateBtn.ToolTip = (r.Notes ?? "").Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes;
                    Status("Hallow Vault v" + r.Version.ToString(2) + " is out - click Update at the top (your backups and settings are kept).");
                }));
            }) { IsBackground = true }.Start();
        }
        void InstallUpdate()
        {
            if (pending == null) return;
            if (Link.TestDir != null) { Status("Test copy: updates aren't installed."); return; }
            lock (busy) if (working) { Status("A backup is running - update when it's done."); return; }
            if (MessageBox.Show(this, "Install Hallow Vault v" + pending.Version.ToString(2) + " now?\n\nIt downloads from GitHub, checks the file, and reopens by itself. Your backups and settings are kept.", "Update Hallow Vault", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            updateBtn.IsEnabled = false; updateBtn.Content = "Updating...";
            var r = pending; S.Save();
            new Thread(() =>
            {
                var err = Updater.Install(r);
                Dispatcher.BeginInvoke(new Action(() => { if (err != null) { Status(err); updateBtn.IsEnabled = true; updateBtn.Content = "Update to v" + r.Version.ToString(2); } else Status("Installing the update - Hallow Vault reopens in a few seconds."); }));
            }) { IsBackground = true }.Start();
        }

        void ShowWhatsNew()
        {
            if (S.LastSeenVersion == Program.Version || !IsVisible || Snap.On) return;
            var items = WhatsNew.Since(S.LastSeenVersion);
            S.LastSeenVersion = Program.Version; S.Save();
            if (items.Count > 0) new WhatsNewWindow(items) { Owner = this }.Show();
        }

        // ================================================================ Hallow family link
        // one command line from another Hallow app -> one reply line ("ok", "ok <json>" or "error <message>")
        string LinkCommand(string line)
        {
            var sp = line.IndexOf(' ');
            var cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant(); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            if (cmd == "ping") return "ok " + Link.Json(new Dictionary<string, object> { { "app", "Vault" }, { "version", Program.Version } });
            if (cmd != "status" && cmd != "backup" && cmd != "restore" && cmd != "open" && cmd != "menu" && cmd != "invoke" && cmd != "quit" && cmd != "pin" && cmd != "unpin" && cmd != "check" && cmd != "verify" && cmd != "backup-now") return "error unknown command";
            var r = Dispatcher.Invoke(new Func<string>(() => LinkOnUi(cmd, arg)), TimeSpan.FromSeconds(5));
            return r as string ?? "error Hallow Vault is busy, try again";
        }
        string LinkOnUi(string cmd, string arg)
        {
            if (cmd == "status") return "ok " + Link.Json(StatusData());
            if (cmd == "open") { ShowFromTray(); if (arg.Equals("family", StringComparison.OrdinalIgnoreCase)) ShowFamily(); return "ok"; }
            if (cmd == "backup-now") return FamilyBackupNow();   // v1.16: the Hallow family backup, now (in the background; the result is in status "family")
            if (cmd == "quit") { var qt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; qt.Tick += (s, e) => { qt.Stop(); Quit(); }; qt.Start(); return "ok"; }   // after the reply is sent
            if (cmd == "menu") return "ok " + Link.Json(new Dictionary<string, object> { { "app", "Vault" }, { "items", MenuItems() } });
            if (cmd == "invoke") return InvokeItem(arg);
            if (cmd == "check")   // v1.12: checks in the background (no window); the result is in status ("lastCheck", each game's "check")
            {
                if (checking) return "error a check is already running";
                if (arg.Length == 0 || arg.Equals("all", StringComparison.OrdinalIgnoreCase)) { RunCheck(null, true); return "ok"; }
                var cgm = FindGame(arg); if (cgm == null) return "error Hallow Vault doesn't know a game called " + arg;
                RunCheck(cgm, true); return "ok";
            }
            if (cmd == "verify")   // v1.13: verify the second copy in the background; the result is in status (secondCopy.verified / mismatched)
            {
                if (string.IsNullOrEmpty(S.CopyDir)) return "error no second copy is set up";
                if (verifying) return "error a verify is already running";
                VerifyCopyNow(true); return "ok";
            }
            if (arg.Length == 0) return "error which game? (a game name, or all)";
            if (cmd == "backup" && arg.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var list = Backable().ToList(); foreach (var g in list) Enqueue(g, "asked by another Hallow app");
                return "ok";
            }
            var game = FindGame(arg);
            if (game == null) return "error Hallow Vault doesn't know a game called " + arg;
            if (roots[game.Id].Count == 0) return "error no save folder found for " + game.Name;
            if (cmd == "pin" || cmd == "unpin")   // v1.7: pins the newest version (unpin: the newest pinned one)
            {
                var hs = Backups.List(Root, game); var b = cmd == "pin" ? hs.FirstOrDefault() : hs.FirstOrDefault(x => x.Pinned);
                if (b == null) return "error " + (cmd == "pin" ? "no backups of " : "no pinned backup of ") + game.Name;
                Backups.SetPin(b.File, cmd == "pin"); history[game.Id] = Backups.List(Root, game); Fill(); return "ok";
            }
            if (cmd == "restore") { var gr = game; Dispatcher.BeginInvoke(new Action(() => { ShowFromTray(); RestoreWindow(gr); })); return "ok"; }
            if (S.Off.Contains(game.Id)) return "error " + game.Name + " is turned off in Hallow Vault";
            Enqueue(game, "asked by another Hallow app", true);
            return "ok";
        }
        // by name (case and punctuation don't matter), or by install folder
        Game FindGame(string arg)
        {
            var a = arg.Trim().Trim('"'); var n = Library.Norm(a); var dir = a.TrimEnd('\\');
            Func<Game, bool> byDir = g => !string.IsNullOrEmpty(g.InstallDir) && string.Equals(g.InstallDir.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase);
            return games.FirstOrDefault(g => string.Equals(g.Name, a, StringComparison.OrdinalIgnoreCase))
                ?? games.FirstOrDefault(g => n.Length > 0 && Library.Norm(g.Name) == n)
                ?? games.FirstOrDefault(g => byDir(g) || g.Alts.Any(byDir));
        }
        // the same JSON as status.json
        Dictionary<string, object> StatusData()
        {
            var gl = new List<object>();
            foreach (var g in games.Where(x => roots.ContainsKey(x.Id) && roots[x.Id].Count > 0).OrderBy(x => x.Source == "hallow").ThenBy(x => x.Name))
            {
                List<BackupInfo> hs; if (!history.TryGetValue(g.Id, out hs)) hs = new List<BackupInfo>();
                gl.Add(new Dictionary<string, object> { { "name", g.Name }, { "installDir", string.IsNullOrEmpty(g.InstallDir) ? null : g.InstallDir },
                    { "lastBackup", Link.Time(hs.Count == 0 ? (DateTime?)null : hs[0].When) }, { "versions", hs.Count }, { "sizeBytes", hs.Sum(x => x.Bytes) }, { "off", S.Off.Contains(g.Id) }, { "pinned", hs.Count(x => x.Pinned) }, { "alarm", S.Alarms.ContainsKey(g.Id) },
                    { "custom", g.Source == "custom" }, { "check", CheckWord(Backups.GameDir(Root, g)) },
                    { "steamCloud", steamCloud.Contains(g.Id) }, { "lastResult", ResultOf(g.Id) } });   // v1.13
            }
            int cl; DateTime cw; string cs; int fine = 0, warn = 0, bad = 0;
            foreach (var v in S.Checks.Values) if (Checker.Unpack(v, out cl, out cw, out cs)) { if (cl == 2) bad++; else if (cl == 1) warn++; else fine++; }
            DateTime lastChk; var hasChk = DateTime.TryParse(S.LastCheck, out lastChk);
            var check = new Dictionary<string, object> { { "when", hasChk ? Link.Time(lastChk) : null }, { "running", checking }, { "fine", fine }, { "warning", warn }, { "damaged", bad }, { "weekly", S.WeeklyCheck } };
            bool isBusy; lock (busy) isBusy = working;
            var d = new Dictionary<string, object> { { "app", "Vault" }, { "version", Program.Version }, { "updated", Link.Time(DateTime.Now) }, { "running", !exiting }, { "busy", isBusy }, { "lastCrash", Link.Time(Crash.Last()) }, { "look", Accent.CurrentLight ? "light" : "dark" }, { "secondCopy", string.IsNullOrEmpty(S.CopyDir) || copyState == null ? null : CopyStatus() }, { "lastCheck", check }, { "games", gl } };
            // v1.13: backup health in one place (for Hallow Hub and Hallow Guard): the newest backup, whether the latest backup try worked, and which games need a look
            DateTime? newest = null; foreach (var hs in history.Values) if (hs.Count > 0 && (newest == null || hs[0].When > newest)) newest = hs[0].When;
            d["lastBackupAt"] = Link.Time(newest);
            var latest = S.Results.Select(kv => Tuple.Create(kv.Key, ParseResult(kv.Value))).Where(x => x.Item2 != null && games.Any(gm => gm.Id == x.Item1 && !S.Off.Contains(gm.Id))).OrderByDescending(x => x.Item2.Item2).FirstOrDefault();
            d["lastBackupOk"] = latest == null ? (object)null : latest.Item2.Item1 == "ok";
            var failing = new List<object>();
            foreach (var g in games.Where(x => !S.Off.Contains(x.Id) && ((roots.ContainsKey(x.Id) && roots[x.Id].Count > 0) || S.Alarms.ContainsKey(x.Id))).OrderBy(x => x.Name))
            {
                var why = new List<string>(); var r = S.Results.ContainsKey(g.Id) ? ParseResult(S.Results[g.Id]) : null;
                if (r != null && r.Item1 == "failed") why.Add("backup failed");
                if (S.Alarms.ContainsKey(g.Id) || (r != null && r.Item1 == "refused")) why.Add("saves look damaged");
                if (CheckWord(Backups.GameDir(Root, g)) == "damaged") why.Add("a backup is damaged");
                if (why.Count > 0) failing.Add(new Dictionary<string, object> { { "name", g.Name }, { "why", string.Join(", ", why) } });
            }
            d["failingGames"] = failing;
            d["family"] = FamilyStatus();   // v1.16
            return d;
        }
        Dictionary<string, object> CopyStatus()   // v1.10 fields, plus v1.13's path, retention and the last Verify
        {
            DateTime vw; int vc, vb, vr, vo; var hasV = ParseVerified(out vw, out vc, out vb, out vr, out vo);
            return new Dictionary<string, object> { { "reachable", copyState.Reachable }, { "behind", copyState.Behind }, { "conflicts", copyState.Conflicts.Count },
                { "path", S.CopyDir }, { "keep", S.CopyKeep }, { "verified", hasV ? Link.Time(vw.ToLocalTime()) : null }, { "copies", hasV ? vc : 0 }, { "mismatched", hasV ? vb : 0 } };
        }
        static Tuple<string, DateTime, string> ParseResult(string packed)   // "<ok|failed|refused>|<when>|<what>"
        {
            var p = (packed ?? "").Split(new[] { '|' }, 3); DateTime w;
            return p.Length == 3 && DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out w) ? Tuple.Create(p[0], w, p[2]) : null;
        }
        object ResultOf(string id)
        {
            string v; var r = S.Results.TryGetValue(id, out v) ? ParseResult(v) : null;
            return r == null ? null : new Dictionary<string, object> { { "result", r.Item1 }, { "when", Link.Time(r.Item2) }, { "what", r.Item3 } };
        }
        string CheckWord(string gameDir)   // v1.12: "ok" / "warning" / "damaged" from the last check, or null
        {
            string p; int l; DateTime w; string s;
            if (!S.Checks.TryGetValue(System.IO.Path.GetFileName(gameDir), out p) || !Checker.Unpack(p, out l, out w, out s)) return null;
            return l >= 2 ? "damaged" : l == 1 ? "warning" : "ok";
        }
        void WriteStatus() { if (!Snap.On) Link.WriteStatus(StatusData()); }

        // ================================================================ v1.7: save alarm and moving PCs
        Game alarmGame;   // the game a clicked balloon opens Restore for
        static string Poss(string n) { return n.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? n + "'" : n + "'s"; }
        string AlarmText(Game g, string stored) { var i = stored.IndexOf('\n'); return Poss(g.Name) + " saves look damaged: " + (i < 0 ? stored : stored.Substring(i + 1)) + ". Vault didn't back them up, so your last good version is safe - Restore puts it back."; }
        void RaiseAlarm(Game g, string why)
        {
            var hs = Backups.List(Root, g); var key = (hs.Count > 0 ? System.IO.Path.GetFileName(hs[0].File) : "") + "\n" + why;
            string was; S.Alarms.TryGetValue(g.Id, out was);
            var lastKey = was == null ? null : was.Split('\n')[0];
            S.Alarms[g.Id] = key; S.Save(); Fill();
            Status(AlarmText(g, key));
            if (lastKey == key.Split('\n')[0]) return;   // already told you about this one
            alarmGame = g;
            Notify(Poss(g.Name) + " saves look damaged", why + ". Vault kept your old versions - restore the last good one?" + (hosted ? " (Hallow Vault > Restore)" : " Click here."));
        }
        void ExportForNewPc()
        {
            using (var d = new WF.FolderBrowserDialog { Description = "Where should the export go? A USB stick or a cloud folder works - a folder called \"Hallow Vault move <date>\" is made inside.", ShowNewFolderButton = true })
            {
                if (d.ShowDialog() != WF.DialogResult.OK) return;
                var root = Root; var dest = d.SelectedPath; var here = Places.Here(steamUser); var all = games.ToList();
                Status("Exporting your newest versions to " + dest + "...");
                new Thread(() =>
                {
                    string m;
                    try
                    {
                        int n; var outDir = Mover.Export(root, here, id => { var g = all.FirstOrDefault(x => x.Id == id) ?? all.SelectMany(x => x.Alts).FirstOrDefault(x => x.Id == id); return g == null ? null : g.InstallDir; }, dest, out n);
                        m = n + (n == 1 ? " game" : " games") + " exported to " + outDir + ". On the new PC: Hallow Vault > Settings > Import.";
                    }
                    catch (Exception ex) { m = "Couldn't export: " + ex.Message; }
                    Dispatcher.BeginInvoke(new Action(() => Status(m)));
                }) { IsBackground = true }.Start();
            }
        }
        void ImportFromOldPc()
        {
            using (var d = new WF.FolderBrowserDialog { Description = "Pick the \"Hallow Vault move\" folder you exported on your old PC.", ShowNewFolderButton = false })
            {
                if (d.ShowDialog() != WF.DialogResult.OK) return;
                var man = Mover.Load(d.SelectedPath);
                if (man == null) { Status("That folder has no Hallow Vault export in it (" + Mover.ManifestName + ")."); return; }
                BuildImport(d.SelectedPath, man, this).ShowDialog();
            }
        }
        Game LocalGame(MoveGame mg)
        {
            return games.FirstOrDefault(x => x.Id == mg.Id || x.Alts.Any(a => a.Id == mg.Id)) ?? games.FirstOrDefault(x => Library.Norm(x.Name) == Library.Norm(mg.Name));
        }
        Window BuildImport(string folder, MoveManifest man, Window owner)
        {
            var w = Dialog("Import saves", 600, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Import saves from " + (man.Machine ?? "your old PC"), FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "The newest version of each ticked game is put back here, with its folders moved to this PC (your user folder, Documents, the Steam account). The saves already here are backed up first.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var lst = new StackPanel(); var boxes = new List<Tuple<CheckBox, MoveGame>>();
            foreach (var mg in man.Games.Where(x => x.Files.Count > 0).OrderBy(x => x.Name))
            {
                var local = LocalGame(mg); var b = Backups.Read(System.IO.Path.Combine(folder, mg.Folder, mg.Files[0]));
                var label = mg.Name + (b == null ? "" : "   \u00B7   " + b.When.ToString("d MMM yyyy, HH:mm") + "   \u00B7   " + VaultSettings.Size(b.Bytes)) + (local == null ? "   \u00B7   not installed here" : "") + (mg.Files.Count > 1 ? "   \u00B7   +" + (mg.Files.Count - 1) + " pinned" : "");
                var c = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = local != null && b != null, IsEnabled = b != null, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 0, 0, 2) };
                boxes.Add(Tuple.Create(c, mg)); lst.Children.Add(c);
            }
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var go = new Button { Content = "Restore all newest", Style = (Style)FindResource("Accent") };
            go.Click += (s, e) =>
            {
                var pick = boxes.Where(x => x.Item1.IsChecked == true).Select(x => x.Item2).ToList(); if (pick.Count == 0) return;
                var running = pick.Select(LocalGame).Where(x => x != null && Running(x)).Select(x => x.Name).ToList();
                if (running.Count > 0) { MessageBox.Show(w, "Close " + string.Join(", ", running) + " first - a running game would overwrite the restored files.", "Import", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (MessageBox.Show(w, "Put back the newest saves of " + pick.Count + (pick.Count == 1 ? " game" : " games") + "?\n\nThe saves already on this PC are backed up first.", "Import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var here = Places.Here(steamUser); int ok = 0; var fails = new List<string>();
                foreach (var mg in pick)
                {
                    try
                    {
                        var local = LocalGame(mg) ?? new Game { Id = mg.Id ?? "custom:" + Library.Norm(mg.Name), Name = mg.Name, Source = "custom" };
                        var cur = Saves.Find(local, steamUser, ExtraOf(local));
                        var nf = Mover.ImportGame(folder, man, mg, here, Root, local, cur);
                        Log.Add(local.Name, "imported from " + (man.Machine ?? "another PC") + " (" + nf + " files)");
                        S.LastSig.Remove(local.Id); S.Alarms.Remove(local.Id); ok++;
                    }
                    catch (Exception ex) { fails.Add(mg.Name + " (" + ex.Message + ")"); }
                }
                S.Save(); w.Close(); Rescan();
                Status(ok + (ok == 1 ? " game" : " games") + " put back from " + (man.Machine ?? "your old PC") + ". The saves from just before are kept as a backup." + (fails.Count > 0 ? " Couldn't import: " + string.Join(", ", fails) + "." : ""));
            };
            row.Children.Add(cancel); row.Children.Add(go); sp.Children.Add(row);
            w.Content = sp; return w;
        }

        // ================================================================ v1.5: one Hallow icon (Hallow Hub hosts the tray menu)
        // the same useful actions as our own tray menu (Hub adds Open and Exit)
        List<object> MenuItems()
        {
            bool isBusy; lock (busy) isBusy = working;
            Func<string, string, bool, Dictionary<string, object>> it = (id, label, on) => new Dictionary<string, object> { { "id", id }, { "label", label }, { "enabled", on } };
            return new List<object> {
                it("backup-all", isBusy ? "Backing up..." : "Back up everything now", !isBusy), it("restore", "Restore...", true), it("vault", "Open the vault folder", true),
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "onclose" }, { "label", "Back up when a game closes" }, { "checked", S.OnClose } },
                new Dictionary<string, object> { { "id", "-" } },
                it("family-now", famBusy ? "Backing up the Hallow family..." : "Back up the Hallow family now", F.On && F.Dests.Count > 0 && !famBusy), it("family", "Hallow family backup...", true),   // v1.16
            };
        }
        string InvokeItem(string id)
        {
            switch (id.ToLowerInvariant())
            {
                case "backup-all": foreach (var g in Backable()) Enqueue(g, "you asked"); return "ok";
                case "restore": ShowFromTray(); Status("Pick Restore next to a game to put an older version back."); return "ok";
                case "vault": Directory.CreateDirectory(Root); Process.Start("explorer.exe", "\"" + Root + "\""); return "ok";
                case "onclose": S.OnClose = !S.OnClose; S.Save(); Fill(); return "ok";
                case "family-now": return FamilyBackupNow();
                case "family": ShowFromTray(); ShowFamily(); return "ok";
                default: return "error no menu item called " + id;
            }
        }
        bool hosted, asking;   // hosted: Hub showed it hosts the icon last time we asked
        // on start and every 5 s: does Hub host the family icon? Hide ours while it does; never leave Vault without an icon.
        void CheckTrayHost()
        {
            if (tray == null || asking) return;
            if (!S.UseHallowIcon) { SetHosted(false); return; }
            asking = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r; bool on = false;
                try { on = Link.SendTo("Hub", "tray-host", 300, 1500, out r) == 0 && r.Replace(" ", "").Contains("\"on\":true"); } catch { }
                Dispatcher.BeginInvoke(new Action(() => { asking = false; SetHosted(on && S.UseHallowIcon); }));
            });
        }
        void SetHosted(bool on) { hosted = on; if (tray != null && !exiting && !ownBalloon) tray.Visible = !on; }
        bool ownBalloon;
        // a balloon: through Hub while our icon is hidden, else (or if Hub doesn't take it) from our own icon for a moment
        void Notify(string title, string text)
        {
            if (tray == null) return;
            if (!hosted) { tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r; var ok = false;
                try { ok = Link.SendTo("Hub", "notify Vault\t" + title + "\t" + text, 300, 1500, out r) == 0; } catch { }
                if (ok) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ownBalloon = true; tray.Visible = true; tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None);
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    t.Tick += (o, e) => { t.Stop(); ownBalloon = false; SetHosted(hosted); };
                    t.Start();
                }));
            });
        }

        static ImageSource AppIcon()
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            return Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        // ================================================================ layout
        UIElement Build()
        {
            var root = new Grid { Margin = new Thickness(22, 14, 22, 12) };
            foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = h });
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            try { title.Children.Add(new Image { Source = AppIcon(), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) }); } catch { }
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Accent.Brush(), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " VAULT", FontSize = 22, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(title, Dock.Left); head.Children.Add(title);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            allBtn = new Button { Content = "Back up everything", Style = (Style)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Back up every game whose saves changed since its last backup" };
            allBtn.Click += (s, e) => { foreach (var g in Backable()) Enqueue(g, "you asked"); };
            updateBtn = new Button { Content = "Update", Style = (Style)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };   // v1.8
            updateBtn.Click += (s, e) => InstallUpdate();
            if (pending != null) { updateBtn.Content = "Update to v" + pending.Version.ToString(2); updateBtn.Visibility = Visibility.Visible; }
            right.Children.Add(updateBtn);
            var addGame = new Button { Content = "Add a game", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Back up a game no launcher knows: give it a name and its save folders" };   // v1.12
            addGame.Click += (s, e) => BuildCustom(null, this).ShowDialog();
            right.Children.Add(addGame);
            var famBtn = new Button { Content = "Family backup", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Back up every Hallow app's settings and Hallow Keys' / Authenticator's encrypted backups to another drive, a USB drive or your NAS" };   // v1.16
            famBtn.Click += (s, e) => ShowFamily();
            right.Children.Add(famBtn);
            right.Children.Add(allBtn);
            checkBtn = new Button { Content = "Check backups", Tag = "icon", Width = 36, Height = 34, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Check every backup: each zip opens and every file in it matches (nothing is changed)" };   // v1.12
            checkBtn.Click += (s, e) => RunCheck(null);
            right.Children.Add(checkBtn);
            var open = new Button { Content = "Open the vault", Tag = "icon", Width = 36, Height = 34, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Open the folder with your backups" };
            open.Click += (s, e) => { Directory.CreateDirectory(Root); Process.Start("explorer.exe", "\"" + Root + "\""); };
            right.Children.Add(open);
            var refresh = new Button { Content = "Refresh", Tag = "icon", Width = 36, Height = 34, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Look again for games and save folders" };
            refresh.Click += (s, e) => { Rescan(); Status("Looked again - " + games.Count(g => roots[g.Id].Count > 0) + " games with saves found."); };
            right.Children.Add(refresh);
            gearBtn = new ToggleButton { Content = "Settings", Tag = "icon", Width = 36, Height = 34, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), ToolTip = "Settings" };
            gearBtn.Checked += (s, e) => settingsPanel.Visibility = Visibility.Visible;
            gearBtn.Unchecked += (s, e) => settingsPanel.Visibility = Visibility.Collapsed;
            right.Children.Add(gearBtn);
            head.Children.Add(right);
            Grid.SetRow(head, 0); root.Children.Add(head);

            var sumBox = new Border { Background = (Brush)FindResource("Panel"), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(0, 0, 0, 14) };
            var sumRow = new StackPanel { Orientation = Orientation.Horizontal };
            var si = Icons.Make("shield-halved", 18, B("#6ED28C")); si.Margin = new Thickness(0, 0, 12, 0); si.VerticalAlignment = VerticalAlignment.Center; sumRow.Children.Add(si);
            summary = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            sumRow.Children.Add(summary); sumBox.Child = sumRow;
            Grid.SetRow(sumBox, 1); root.Children.Add(sumBox);

            list = new StackPanel();
            var sv = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(sv, 2); root.Children.Add(sv);
            status = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(2, 10, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(status, 3); root.Children.Add(status);
            var outer = new Grid(); outer.Children.Add(root);
            settingsPanel = BuildSettings(); outer.Children.Add(settingsPanel);
            return outer;
        }

        Border BuildSettings()
        {
            var b = new Border { Background = B("#241E2E"), BorderBrush = B("#3A3248"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 14, 18, 16), Width = 430, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 62, 22, 0), Visibility = Visibility.Collapsed, Effect = Look.Light ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, Opacity = 0.14, ShadowDepth = 3 } : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 4 } };   // v1.14: softer on light
            var sp = new StackPanel();
            var sh = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var close = new Button { Content = "x", Tag = "icon", Style = (Style)FindResource("Btn"), Width = 30, Height = 28, Padding = new Thickness(0) };
            close.Click += (s, e) => gearBtn.IsChecked = false; DockPanel.SetDock(close, Dock.Right); sh.Children.Add(close);
            var gi = Icons.Make("gear", 16, Accent.Brush()); gi.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(gi, Dock.Left); sh.Children.Add(gi);
            sh.Children.Add(new TextBlock { Text = "Settings", FontWeight = FontWeights.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(sh);
            sp.Children.Add(new TextBlock { Text = "Backups go to", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var where = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var pathText = new TextBlock { Text = Root, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var change = new Button { Content = "Change...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            change.Click += (s, e) =>
            {
                using (var d = new WF.FolderBrowserDialog { Description = "Where should Hallow Vault keep your backups? A second drive or your server (like Gloam) is safest.", SelectedPath = Root, ShowNewFolderButton = true })
                    if (d.ShowDialog() == WF.DialogResult.OK) { S.VaultDir = d.SelectedPath; S.Save(); pathText.Text = Root; Rescan(); Status("Backups now go to " + Root + ". Old backups stay where they were."); }
            };
            DockPanel.SetDock(change, Dock.Right); where.Children.Add(change); where.Children.Add(pathText); sp.Children.Add(where);
            sp.Children.Add(new TextBlock { Text = "A second drive or a network folder is safest - if this drive dies, your backups survive.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var keep = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var keepBox = new ComboBox { Style = (Style)FindResource("Combo"), Width = 150 };
            foreach (var n in new[] { 5, 10, 20, 50 }) keepBox.Items.Add(new ComboBoxItem { Content = n + " per game", Tag = n });
            keepBox.SelectedItem = keepBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.Keep) ?? keepBox.Items[1];
            keepBox.SelectionChanged += (s, e) => { var it = keepBox.SelectedItem as ComboBoxItem; if (it != null) { S.Keep = (int)it.Tag; S.Save(); } };
            DockPanel.SetDock(keepBox, Dock.Right); keep.Children.Add(keepBox);
            keep.Children.Add(new TextBlock { Text = "Backups to keep (oldest go first)", VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(keep);
            // v1.9: every N hours, compression, log
            Func<string, object[], string[], object, Action<object>, ComboBox> pickRow = (label, vals, names, cur, set) =>
            {
                var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var box = new ComboBox { Style = (Style)FindResource("Combo"), Width = 150 };
                for (int i = 0; i < vals.Length; i++) box.Items.Add(new ComboBoxItem { Content = names[i], Tag = vals[i] });
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, cur)) ?? box.Items[0];
                box.SelectionChanged += (s, e) => { var it = box.SelectedItem as ComboBoxItem; if (it != null) { set(it.Tag); S.Save(); } };
                DockPanel.SetDock(box, Dock.Right); dp.Children.Add(box);
                dp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(dp); return box;
            };
            pickRow("Also back up every", new object[] { 0, 1, 2, 4, 8, 12 }, new[] { "Off", "hour", "2 hours", "4 hours", "8 hours", "12 hours" }, S.EveryHours, v => { S.EveryHours = (int)v; S.LastScheduled = DateTime.Now.ToString("o"); });
            pickRow("Compression", new object[] { "best", "fast", "none" }, new[] { "Smallest", "Faster", "None (fastest)" }, S.Compression, v => { S.Compression = (string)v; ApplyCompression(); });
            Func<string, bool, Action<bool>, CheckBox> sw = (text, on, set) =>
            {
                var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Style = (Style)FindResource("Switch") };
                c.Click += (s, e) => { set(c.IsChecked == true); S.Save(); }; sp.Children.Add(c); return c;
            };
            BuildOffPc(sp);
            // v1.16: the Hallow family backup
            var famRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var famOpen = new Button { Content = "Family backup...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            famOpen.Click += (s, e) => ShowFamily();
            DockPanel.SetDock(famOpen, Dock.Right); famRow.Children.Add(famOpen);
            famRow.Children.Add(new TextBlock { Text = "Hallow family backup", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(famRow);
            sp.Children.Add(new TextBlock { Text = F.Dests.Count == 0 ? "Not set up: every Hallow app's settings and Hallow Keys' / Authenticator's encrypted backups, to another drive, a USB drive or your NAS." : (F.On ? "On" : "Off") + " - " + F.Dests.Count + (F.Dests.Count == 1 ? " destination" : " destinations") + ", last new version " + FamilyAgo() + ".", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            sw("Back up a game's saves when it closes", S.OnClose, v => S.OnClose = v);
            sw("Back up a game right before Steam updates it", S.BeforeUpdate, v => S.BeforeUpdate = v);
            sw("Back up everything once a day", S.Daily, v => S.Daily = v);
            // v1.12: check backups (read-only)
            var chkRow = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
            var chkNow = new Button { Content = "Check all now", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Open every backup and check each file against its checksum - nothing is changed" };
            chkNow.Click += (s, e) => RunCheck(null);
            DockPanel.SetDock(chkNow, Dock.Right); chkRow.Children.Add(chkNow);
            chkRow.Children.Add(new TextBlock { Text = "Check backups", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(chkRow);
            checkState = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }; sp.Children.Add(checkState); ShowCheckState();
            sw("Check every backup once a week", S.WeeklyCheck, v => S.WeeklyCheck = v);
            sp.Children.Add(new TextBlock { Text = "Every zip must open and every file in it must match its checksum and Vault's list. Only reads - a check never changes or deletes a backup. You're told if something's wrong.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, -2, 0, 8) });
            sw("Keep running in the tray when I close the window", S.TrayOnClose, v => S.TrayOnClose = v);
            sw("Start with Windows (in the tray)", StartsWithWindows(), v => SetStartWithWindows(v));
            sw("Use the Hallow icon (Hallow Hub's tray menu, instead of a Vault icon)", S.UseHallowIcon, v => { S.UseHallowIcon = v; CheckTrayHost(); });
            sw("Use the Hallow accent colour (picked in Hallow Toolkit)", S.UseHallowAccent, v => { S.UseHallowAccent = v; Dispatcher.BeginInvoke(new Action(ApplyAccent)); });
            // v1.14: light / dark - Follow Hallow (Hallow Toolkit's Light / Dark / Same as Windows) or Vault's own choice
            pickRow("Look", new object[] { "hallow", "light", "dark" }, new[] { "Follow Hallow", "Light", "Dark" }, S.LookSetting(), v => { S.Look = (string)v; Dispatcher.BeginInvoke(new Action(ApplyAccent)); });
            // v1.11: the online save database
            sw("Online save database (where 19,000+ games keep their saves)", S.UseManifest, v => { S.UseManifest = v; if (v && !Manifest.Loaded) CheckManifest(true); else Rescan(); ShowManifestState(); });
            sp.Children.Add(new TextBlock { Text = "Ludusavi's save database, built from PCGamingWiki. Vault downloads it from GitHub and checks once a week for a newer one - nothing about your PC is sent. Vault's own detection still adds anything it misses.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, -2, 0, 4) });
            manState = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(46, 0, 0, 6) }; sp.Children.Add(manState); ShowManifestState();
            sw("Also back up settings files the database lists (not only saves)", S.ManifestConfig, v => { S.ManifestConfig = v; Saves.ManifestConfig = v; Rescan(); });
            sp.Children.Add(new TextBlock { Text = "Backups are ordinary .zip files you can open anywhere. Logs, caches and crash dumps are left out, and so are secrets like app keys.\n\nSave database: Ludusavi's manifest by Matthew T. Kennerly (MIT, github.com/mtkennerly/ludusavi-manifest), compiled from PCGamingWiki (CC BY-NC-SA 3.0, pcgamingwiki.com).",
                Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
                        var verRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var chk = new Button { Content = "Check for updates", Style = (Style)FindResource("Btn") }; chk.Click += (s, e) => CheckForUpdate(true);
            DockPanel.SetDock(chk, Dock.Right); verRow.Children.Add(chk);
            var logBtn = new Button { Content = "Backup log", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "What was backed up, skipped, refused or failed, and when" };   // v1.9
            logBtn.Click += (s, e) => BuildLog(this).ShowDialog();
            DockPanel.SetDock(logBtn, Dock.Right); verRow.Children.Add(logBtn);
            verRow.Children.Add(new TextBlock { Text = "Hallow Vault " + Program.Version + "  -  icons: Font Awesome Free (CC BY 4.0)", Foreground = (Brush)FindResource("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(verRow);
            b.Child = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return b;
        }

        // v1.3: second copy folder and size limits
        void BuildOffPc(StackPanel sp)
        {
            sp.Children.Add(new TextBlock { Text = "Second copy", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var path = new TextBlock { Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var off = new Button { Content = "Turn off", Tag = "icon", Width = 32, Height = 30, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Stop making a second copy (copies already made stay)" };
            var pick = new Button { Content = "Pick...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            Action show = () => { path.Text = string.IsNullOrEmpty(S.CopyDir) ? "Off" : S.CopyDir; off.Visibility = string.IsNullOrEmpty(S.CopyDir) ? Visibility.Collapsed : Visibility.Visible; ShowCopyState(); ShowVerifyState(); };
            pick.Click += (s, e) =>
            {
                using (var d = new WF.FolderBrowserDialog { Description = "Where should Hallow Vault keep a second copy of every backup? A NAS or network folder, a OneDrive, Google Drive or Dropbox folder, or a USB drive.", ShowNewFolderButton = true })
                {
                    if (!string.IsNullOrEmpty(S.CopyDir)) d.SelectedPath = S.CopyDir;
                    if (d.ShowDialog() != WF.DialogResult.OK) return;
                    string clean; var err = Backups.CheckCopyDir(d.SelectedPath, Root, out clean);
                    if (err != null) { Status(err); return; }
                    S.CopyDir = clean; S.CopyVerified = ""; S.Save(); show(); CopyNow();
                }
            };
            off.Click += (s, e) => { S.CopyDir = ""; S.Save(); show(); Status("No second copy from now on. Copies already made stay where they are."); };
            DockPanel.SetDock(off, Dock.Right); row.Children.Add(off); DockPanel.SetDock(pick, Dock.Right); row.Children.Add(pick); row.Children.Add(path); sp.Children.Add(row);
            // v1.13: or type one - a NAS share (\\NAS\share\Backups) the folder picker can't always browse to
            var typed = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var typedBox = new TextBox { Style = (Style)FindResource("Field"), ToolTip = "A full path: D:\\Backups, or a network share like \\\\NAS\\share\\Hallow Vault" };
            var use = new Button { Content = "Use this folder", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            use.Click += (s, e) =>
            {
                string clean; var err = Backups.CheckCopyDir(typedBox.Text, Root, out clean);
                if (err != null) { Status(err); return; }
                S.CopyDir = clean; S.CopyVerified = ""; S.Save(); typedBox.Text = ""; show(); CopyNow();
            };
            DockPanel.SetDock(use, Dock.Right); typed.Children.Add(use);
            var tl = new TextBlock { Text = "or type a path", Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(tl, Dock.Left); typed.Children.Add(tl); typed.Children.Add(typedBox); sp.Children.Add(typed);
            // v1.13: its own retention
            var kp = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var kbox = new ComboBox { Style = (Style)FindResource("Combo"), Width = 190 };
            foreach (var n in new[] { 0, 5, 10, 20, 30, 50, 100 }) kbox.Items.Add(new ComboBoxItem { Content = n == 0 ? "Same as the vault" : "Newest " + n + " per game", Tag = n });
            kbox.SelectedItem = kbox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.CopyKeep) ?? kbox.Items[0];
            kbox.SelectionChanged += (s, e) => { var it = kbox.SelectedItem as ComboBoxItem; if (it == null) return; S.CopyKeep = (int)it.Tag; S.Save(); if (!string.IsNullOrEmpty(S.CopyDir)) CopyNow(); };
            DockPanel.SetDock(kbox, Dock.Right); kp.Children.Add(kbox);
            kp.Children.Add(new TextBlock { Text = "Versions kept in the second copy", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Same as the vault: old copies go when the vault prunes them. Or keep more (or fewer) there - pinned versions always stay." });
            sp.Children.Add(kp);
            // v1.13: verify every copy by its SHA-256
            verifyLine = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            verifyBtn = new Button { Content = "Verify copies", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Read every copy back and compare its SHA-256 with the one recorded when it was made (a damaged copy is copied again from the vault)" };
            verifyBtn.Click += (s, e) => VerifyCopyNow(false);
            verifyText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(verifyBtn, Dock.Right); verifyLine.Children.Add(verifyBtn); verifyLine.Children.Add(verifyText); sp.Children.Add(verifyLine);
            show();
            // v1.10: is it keeping up?
            copyLine = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
            copyShow = new Button { Content = "Show", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed, ToolTip = "Open the folder with the sync conflict copies" };
            copyShow.Click += (s, e) => { var f = copyState == null ? null : copyState.Conflicts.FirstOrDefault(); try { if (f != null) Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch { } };
            copyText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(copyShow, Dock.Right); copyLine.Children.Add(copyShow); copyLine.Children.Add(copyText); sp.Children.Add(copyLine);
            ShowCopyState();
            sp.Children.Add(new TextBlock { Text = "A NAS or network share, a OneDrive, Google Drive or Dropbox folder, or a USB drive. Every backup is copied there too and read back to check it matches (SHA-256) before it counts.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            Func<string, int[], Func<int>, Action<int>, string, DockPanel> limit = (label, opts, get, set, unit) =>
            {
                var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var box = new ComboBox { Style = (Style)FindResource("Combo"), Width = 150 };
                foreach (var mb in opts) box.Items.Add(new ComboBoxItem { Content = mb == 0 ? "No limit" : VaultSettings.Size(mb * (1L << 20)) + unit, Tag = mb });
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == get()) ?? box.Items[0];
                box.SelectionChanged += (s, e) => { var it = box.SelectedItem as ComboBoxItem; if (it != null) { set((int)it.Tag); S.Save(); if ((int)it.Tag > 0) Status("Size limit set - from the next backup, the oldest versions go first (never a game's newest)."); } };
                DockPanel.SetDock(box, Dock.Right); dp.Children.Add(box);
                dp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(dp); return dp;
            };
            limit("Size limit per game", new[] { 0, 100, 250, 500, 1024, 2048, 5120 }, () => S.MaxGameMB, v => S.MaxGameMB = v, "");
            limit("Size limit for the whole vault", new[] { 0, 1024, 5120, 10240, 25600, 51200, 102400 }, () => S.MaxTotalMB, v => S.MaxTotalMB = v, "");
            // v1.7: move to a new PC
            var mv = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var imp = new Button { Content = "Import...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Put back the saves you exported on your old PC" };
            var exp = new Button { Content = "Export...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Every game's newest version (and pinned ones) in one folder, for a new PC" };
            exp.Click += (s, e) => ExportForNewPc(); imp.Click += (s, e) => ImportFromOldPc();
            DockPanel.SetDock(imp, Dock.Right); mv.Children.Add(imp); DockPanel.SetDock(exp, Dock.Right); mv.Children.Add(exp);
            mv.Children.Add(new TextBlock { Text = "Move to a new PC", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(mv);
            sp.Children.Add(new TextBlock { Text = "Export puts every game's newest version (and pinned ones) in one folder - a USB stick works. Import on the new PC puts them back, each after a safety backup.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        }
        // ---- v1.10: second copy status (checked on start, every 10 minutes and after backups; catches up by itself)
        DockPanel copyLine; TextBlock copyText; Button copyShow; Backups.CopyInfo copyState; bool copyChecking; DispatcherTimer copyWatch;
        void CheckCopy()
        {
            if (string.IsNullOrEmpty(S.CopyDir)) { copyState = null; ShowCopyState(); return; }
            if (copyChecking) return; copyChecking = true;
            var root = Root; var copy = S.CopyDir; var snap = Snap.On; var ck = S.CopyKeep;
            new Thread(() =>
            {
                var st = Backups.CopyState(root, copy, ck);
                if (!snap && st.Reachable && st.Behind > 0)   // catch up
                {
                    try { var n = Backups.Mirror(root, copy, ck); Log.Add("Second copy", "caught up: " + n + " backup(s) copied to " + copy); } catch (Exception ex) { st.Error = ex.Message; Log.Add("Second copy", "couldn't catch up: " + ex.Message); }
                    var again = Backups.CopyState(root, copy, ck); again.Error = again.Error ?? st.Error; st = again;
                }
                Dispatcher.BeginInvoke(new Action(() => { copyChecking = false; copyState = st; ShowCopyState(); if (!snap) { Fill(); } }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }
        void ShowCopyState()
        {
            if (copyLine == null) return;
            var c = copyState; var on = !string.IsNullOrEmpty(S.CopyDir);
            copyLine.Visibility = on && c != null ? Visibility.Visible : Visibility.Collapsed; if (!on || c == null) return;
            string text, col;
            if (!c.Reachable) { text = "Can't reach the second copy" + (c.Error != null ? ": " + c.Error : "") + " - it catches up when it's back."; col = "#F07070"; }
            else if (c.Behind > 0) { text = c.Behind + (c.Behind == 1 ? " backup isn't" : " backups aren't") + " in the second copy yet" + (c.Error != null ? " (" + c.Error + ")" : "") + " - it keeps trying."; col = "#F0BE5A"; }
            else { text = "Up to date" + (c.Last != null ? " - last copy " + VaultSettings.Ago(c.Last) : "") + "."; col = "#6ED28C"; }
            if (c.Conflicts.Count > 0) { text += " " + c.Conflicts.Count + (c.Conflicts.Count == 1 ? " sync conflict copy (made by your cloud app) is" : " sync conflict copies (made by your cloud app) are") + " in it - your backups are fine; you can delete those."; if (col == "#6ED28C") col = "#F0BE5A"; }
            copyText.Text = text; copyText.Foreground = T(col); copyShow.Visibility = c.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        string CopySummary()
        {
            var c = copyState; if (string.IsNullOrEmpty(S.CopyDir) || c == null) return "";
            if (!c.Reachable) return "   \u00B7   second copy unreachable";
            if (c.Behind > 0) return "   \u00B7   second copy " + c.Behind + " behind";
            return c.Conflicts.Count > 0 ? "   \u00B7   second copy has sync conflicts" : "   \u00B7   second copy up to date";
        }

        // copies every backup the second copy doesn't have yet (in the background)
        void CopyNow()
        {
            var root = Root; var copy = S.CopyDir; var ck = S.CopyKeep; if (string.IsNullOrEmpty(copy)) return;
            Status("Copying your backups to " + copy + "...");
            new Thread(() =>
            {
                string m; try { var n = Backups.Mirror(root, copy, ck); m = n == 0 ? "The second copy in " + copy + " is up to date." : n + (n == 1 ? " backup" : " backups") + " copied to " + copy + ". New backups are copied there too."; }
                catch (Exception ex) { m = "Couldn't copy to " + copy + ": " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { Status(m); CheckCopy(); }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        // ---- v1.13: verify the second copy (SHA-256 of every copy against the recorded one; damaged copies re-copied from the vault)
        DockPanel verifyLine; TextBlock verifyText; Button verifyBtn; bool verifying;
        void VerifyCopyNow(bool quiet)
        {
            var root = Root; var copy = S.CopyDir; if (string.IsNullOrEmpty(copy) || verifying) return;
            verifying = true; ShowVerifyState(); if (!quiet) Status("Verifying the second copy in " + copy + "...");
            new Thread(() =>
            {
                var v = Backups.VerifyCopy(root, copy);
                Log.Add("Second copy", v.Error != null ? "verify failed: " + v.Error : "verified " + v.Checked + " copies: " + v.Ok + " match" + (v.Repaired > 0 ? ", " + v.Repaired + " damaged and copied again" : "") + (v.Bad.Count > 0 ? ", " + v.Bad.Count + " damaged: " + string.Join(", ", v.Bad.Select(System.IO.Path.GetFileName)) : "") + (v.Unverifiable > 0 ? ", " + v.Unverifiable + " too old to check" : ""));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    verifying = false;
                    if (v.Error == null) { S.CopyVerified = v.When.ToString("o") + "|" + v.Checked + "|" + v.Bad.Count + "|" + v.Repaired + "|" + v.Unverifiable; S.Save(); }
                    ShowVerifyState(); WriteStatus();
                    var m = v.Error != null ? "Couldn't verify the second copy: " + v.Error : v.Bad.Count > 0 ? v.Bad.Count + (v.Bad.Count == 1 ? " copy doesn't" : " copies don't") + " match its backup and the vault no longer has it to copy again - the backups in the vault are fine." : "All " + v.Checked + " copies match their backups" + (v.Repaired > 0 ? " (" + v.Repaired + " damaged " + (v.Repaired == 1 ? "one was" : "ones were") + " copied again)" : "") + ".";
                    if (!quiet) Status(m); else if (v.Bad.Count > 0 || v.Repaired > 0) Notify("Hallow Vault", m);
                }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }
        bool ParseVerified(out DateTime when, out int count, out int bad, out int repaired, out int old)
        {
            when = DateTime.MinValue; count = bad = repaired = old = 0;
            var p = (S.CopyVerified ?? "").Split('|');
            return p.Length >= 5 && DateTime.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out when) && int.TryParse(p[1], out count) && int.TryParse(p[2], out bad) && int.TryParse(p[3], out repaired) && int.TryParse(p[4], out old);
        }
        void ShowVerifyState()
        {
            if (verifyLine == null) return;
            verifyLine.Visibility = string.IsNullOrEmpty(S.CopyDir) ? Visibility.Collapsed : Visibility.Visible;
            verifyBtn.IsEnabled = !verifying;
            DateTime when; int count, bad, repaired, old;
            if (verifying) { verifyText.Text = "Reading every copy back..."; verifyText.Foreground = (Brush)FindResource("Muted"); return; }
            if (!ParseVerified(out when, out count, out bad, out repaired, out old)) { verifyText.Text = "New copies are checked as they're made. Verify reads every copy back."; verifyText.Foreground = (Brush)FindResource("Muted"); return; }
            when = when.ToLocalTime();
            verifyText.Text = "Verified " + VaultSettings.Ago(when) + ": " + (bad > 0 ? bad + " of " + count + (bad == 1 ? " copy doesn't" : " copies don't") + " match" : "all " + count + " copies match") + (repaired > 0 ? ", " + repaired + " copied again" : "") + (old > 0 ? " (" + old + " too old to check)" : "") + ".";
            verifyText.Foreground = T(bad > 0 ? "#F07070" : "#6ED28C");
        }

        // ================================================================ data -> screen
        IEnumerable<Game> Backable() { return games.Where(g => roots.ContainsKey(g.Id) && roots[g.Id].Count > 0 && !S.Off.Contains(g.Id)); }

        void Rescan()
        {
            steamUser = Saves.SteamUserDir();
            games = Library.Scan(new List<Game>());
            games.Add(Hallow());
            games.AddRange(S.Custom.Select(c => c.ToGame()));   // v1.12: games you added
            roots.Clear(); history.Clear(); steamCloud.Clear();
            foreach (var g in games)
            {
                roots[g.Id] = Saves.Find(g, steamUser, ExtraOf(g));
                history[g.Id] = Backups.List(Root, g);
                if (Saves.SteamCloud(g, steamUser)) steamCloud.Add(g.Id);   // v1.13
            }
            Fill();
        }
        readonly HashSet<string> steamCloud = new HashSet<string>();   // v1.13: games Steam Cloud already syncs
        // folders you added to a game (and, for a game you added, its save folders)
        List<string> ExtraOf(Game g)
        {
            List<string> ex; S.Extra.TryGetValue(g.Id, out ex);
            var c = g.Source == "custom" ? S.Custom.FirstOrDefault(x => x.Id == g.Id) : null;
            if (c == null) return ex;
            return c.Folders.Concat(ex ?? new List<string>()).ToList();
        }
        CustomGame CustomOf(Game g) { return g == null || g.Source != "custom" ? null : S.Custom.FirstOrDefault(x => x.Id == g.Id); }

        void Fill()
        {
            list.Children.Clear();
            var ordered = games.OrderByDescending(g => roots[g.Id].Count > 0 && !S.Off.Contains(g.Id)).ThenBy(g => g.Source == "hallow").ThenBy(g => g.Name).ToList();
            bool shownNone = false;
            foreach (var g in ordered)
            {
                if (roots[g.Id].Count == 0 && !shownNone) { shownNone = true; list.Children.Add(Label("NO SAVE FOLDER FOUND - THESE PROBABLY SAVE IN THE CLOUD (ADD ONE IF YOU KNOW WHERE)")); }
                list.Children.Add(Row(g));
            }
            var all = history.Values.SelectMany(x => x).ToList();
            var last = all.Count == 0 ? (DateTime?)null : all.Max(x => x.When);
            summary.Text = "Backing up " + Backable().Count() + " games to " + Root + "   ·   " + all.Count + " backups, " + VaultSettings.Size(all.Sum(x => x.Bytes)) + "   ·   last one " + VaultSettings.Ago(last) +
                (S.OnClose ? "   ·   automatic when a game closes" : "") + (S.Daily ? " and daily" : "") + CopySummary() + FamilySummary();
            WriteStatus();
        }
        FrameworkElement Label(string t)
        {
            return new TextBlock { Text = t, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = T("#A98BE0"), Margin = new Thickness(2, 14, 0, 8) };
        }

        static readonly Dictionary<string, ImageSource> imgCache = new Dictionary<string, ImageSource>();
        static ImageSource Img(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null; ImageSource s;
            if (imgCache.TryGetValue(path, out s)) return s;
            try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(path); bi.DecodePixelWidth = 240; bi.EndInit(); bi.Freeze(); s = bi; } catch { s = null; }
            imgCache[path] = s; return s;
        }

        Border Row(Game g)
        {
            var rs = roots[g.Id]; var hs = history[g.Id]; var off = S.Off.Contains(g.Id);
            var cm = new ContextMenu(); var mi = new MenuItem { Header = "Files to back up (include / leave out)..." }; mi.Click += (s, e) => BuildFilters(g, this).ShowDialog(); cm.Items.Add(mi);   // v1.9
            List<string> fi0, fe0; S.Include.TryGetValue(g.Id, out fi0); S.Exclude.TryGetValue(g.Id, out fe0);
            var row = new Border { ContextMenu = cm, ToolTip = fi0 != null || fe0 != null ? "Your file rules apply (right-click to change)" : null, Background = (Brush)FindResource("Panel"), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Opacity = rs.Count == 0 || off ? 0.6 : 1 };
            var dp = new DockPanel();
            var art = Img(g.Header);
            var thumb = new Border { Width = 116, Height = 54, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = art != null ? (Brush)new ImageBrush(art) { Stretch = Stretch.UniformToFill } : B("#2E2639") };
            if (art == null) thumb.Child = Icons.Make(g.Source == "hallow" ? "shield-halved" : "gamepad", 20, B("#8F88A0"));
            DockPanel.SetDock(thumb, Dock.Left); dp.Children.Add(thumb);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (rs.Count > 0)
            {
                var bk = new Button { Content = "Back up", Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !off };
                bk.Click += (s, e) => Enqueue(g, "you asked", true);
                var rst = new Button { Content = "Restore", Style = (Style)FindResource(S.Alarms.ContainsKey(g.Id) ? "Accent" : "Btn"), Margin = new Thickness(6, 0, 0, 0), IsEnabled = hs.Count > 0, ToolTip = hs.Count > 0 ? "Put an older version back" : "No backups yet" };
                rst.Click += (s, e) => RestoreWindow(g);
                var op = new Button { Content = "Open folder", Tag = "icon", Width = 32, Height = 30, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Open the save folder" };
                op.Click += (s, e) => { var p = rs[0].Path; Process.Start("explorer.exe", File.Exists(p) ? "/select,\"" + p + "\"" : "\"" + p + "\""); };
                btns.Children.Add(bk); btns.Children.Add(rst); btns.Children.Add(op);
            }
            var cg = CustomOf(g);
            if (cg != null)   // v1.12: a game you added - edit its name, folders and .exe (or remove it)
            {
                var ed = new Button { Content = "Edit game", Tag = rs.Count > 0 ? "icon" : null, Width = rs.Count > 0 ? 32 : double.NaN, Height = 30, Padding = rs.Count > 0 ? new Thickness(0) : new Thickness(12, 5, 12, 5), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Change this game's name, save folders or program - or remove it (its backups stay)" };
                ed.Click += (s, e) => BuildCustom(cg, this).ShowDialog();
                btns.Children.Add(ed);
            }
            else
            {
                var addF = new Button { Content = "Add folder", Tag = rs.Count > 0 ? "icon" : null, Width = rs.Count > 0 ? 32 : double.NaN, Height = 30, Padding = rs.Count > 0 ? new Thickness(0) : new Thickness(12, 5, 12, 5), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Add a folder to back up for this game" };
                addF.Click += (s, e) => AddFolder(g);
                btns.Children.Add(addF);
            }
            var mc = new MenuItem { Header = "Check backups" }; mc.Click += (s, e) => RunCheck(g); mc.IsEnabled = hs.Count > 0; cm.Items.Add(mc);   // v1.12
            if (cg != null) { var me = new MenuItem { Header = "Edit game..." }; me.Click += (s, e) => BuildCustom(cg, this).ShowDialog(); cm.Items.Add(me); }
            if (rs.Count > 0 && g.Source != "hallow")
            {
                var tg = new Button { Content = off ? "Turn on" : "Turn off", Tag = "icon", Width = 32, Height = 30, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = off ? "Back this game up again" : "Stop backing this game up" };
                tg.Click += (s, e) => { if (off) S.Off.Remove(g.Id); else S.Off.Add(g.Id); S.Save(); Fill(); };
                btns.Children.Add(tg);
            }
            DockPanel.SetDock(btns, Dock.Right); dp.Children.Add(btns);
            var last = hs.Count == 0 ? (DateTime?)null : hs[0].When;
            var pinned = hs.Count(x => x.Pinned); string alarm; S.Alarms.TryGetValue(g.Id, out alarm);
            var when = new TextBlock { Width = 150, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0),
                Text = alarm != null ? "saves look damaged" : rs.Count == 0 ? "" : off ? "turned off" : hs.Count == 0 ? "not backed up yet" : "backed up " + VaultSettings.Ago(last),
                Foreground = alarm != null ? T("#F07070") : hs.Count == 0 && rs.Count > 0 && !off ? T("#F0BE5A") : (Brush)FindResource("Muted"), FontWeight = alarm != null ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = alarm != null ? AlarmText(g, alarm) : hs.Count == 0 ? null : hs.Count + (hs.Count == 1 ? " backup" : " backups") + (pinned > 0 ? " (" + pinned + " pinned)" : "") + ", " + VaultSettings.Size(hs.Sum(x => x.Bytes)) };
            if (alarm != null && rs.Count == 0)   // the folder is gone: still offer Restore
            {
                var rst2 = new Button { Content = "Restore", Style = (Style)FindResource("Accent"), Margin = new Thickness(6, 0, 0, 0) }; rst2.Click += (s, e) => RestoreWindow(g); btns.Children.Insert(0, rst2);
            }
            DockPanel.SetDock(when, Dock.Right); dp.Children.Add(when);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock { Text = g.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (cg != null) nameRow.Children.Add(new TextBlock { Text = "added by you", FontSize = 10.5, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) });
            if (steamCloud.Contains(g.Id))   // v1.13: Steam Cloud syncs it too
            {
                var sc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
                    ToolTip = "Steam Cloud also syncs this game's saves. Vault keeps its own versions as well: Steam Cloud keeps only the latest one, and it syncs a broken save just like a good one." };
                var ci = Icons.Make("steam", 11, (Brush)FindResource("Muted")); ci.VerticalAlignment = VerticalAlignment.Center; sc.Children.Add(ci);
                sc.Children.Add(new TextBlock { Text = "Steam Cloud", FontSize = 10.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                nameRow.Children.Add(sc);
            }
            var dot = CheckDot(Backups.GameDir(Root, g)); if (dot != null) nameRow.Children.Add(dot);   // v1.12: the last backup check
            names.Children.Add(nameRow);
            var where = rs.Count == 0 ? "No save folder found" : string.Join("   ·   ", rs.Select(r => r.Kind + " (" + VaultSettings.Size(r.Bytes) + ")"));
            names.Children.Add(new TextBlock { Text = where, Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = rs.Count == 0 ? null : string.Join("\n", rs.Select(r => r.Path + "  (" + r.Files + " files)")) });
            dp.Children.Add(names);
            row.Child = dp;
            return row;
        }

        // ================================================================ actions
        void AddFolder(Game g)
        {
            using (var d = new WF.FolderBrowserDialog { Description = "Pick a folder to back up with " + g.Name, ShowNewFolderButton = false })
            {
                if (d.ShowDialog() != WF.DialogResult.OK) return;
                List<string> ex; if (!S.Extra.TryGetValue(g.Id, out ex)) { ex = new List<string>(); S.Extra[g.Id] = ex; }
                if (!ex.Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) ex.Add(d.SelectedPath);
                S.Save(); roots[g.Id] = Saves.Find(g, steamUser, ExtraOf(g)); Fill();
                Status(d.SelectedPath + " is backed up with " + g.Name + " from now on.");
            }
        }

        // backups run one at a time on a background thread; "force" also backs up when nothing changed
        void Enqueue(Game g, string reason, bool force = false)
        {
            lock (busy) { if (queue.Any(q => q.Item1.Id == g.Id)) return; queue.Enqueue(Tuple.Create(g, force ? "!" + reason : reason)); if (working) return; working = true; }
            allBtn.IsEnabled = false; WriteStatus();
            var root = Root; var keep = S.Keep; var maxGame = S.MaxGameMB * (1L << 20); var maxTotal = S.MaxTotalMB * (1L << 20); var copy = S.CopyDir; var copyKeep = S.CopyKeep;
            new Thread(() =>
            {
                while (true)
                {
                    Tuple<Game, string> job; lock (busy) { if (queue.Count == 0) { working = false; break; } job = queue.Dequeue(); }
                    var g2 = job.Item1; var force2 = job.Item2.StartsWith("!"); var why = job.Item2.TrimStart('!');
                    string msg; bool wait = false; string alarm = null; string logWhat = null;
                    try
                    {
                        var rs = Saves.Find(g2, steamUser, ExtraOf(g2));
                        var sig = Saves.Fingerprint(rs); string old; S.LastSig.TryGetValue(g2.Id, out old);
                        // v1.7: saves that vanished, emptied or shrank a lot are not backed up over (unless you pressed Back up yourself)
                        var hsNow = Backups.List(root, g2);
                        if (!(force2 && why == "you asked")) alarm = Backups.Alarm(rs, hsNow.Count > 0 ? hsNow[0] : null);
                        if (alarm != null) { msg = null; logWhat = "refused (" + why + "): " + alarm + " - kept the old versions"; }
                        else if (rs.Count == 0) { msg = null; logWhat = "skipped (" + why + "): no save folder"; }
                        else if (!force2 && sig == old && Backups.List(root, g2).Count > 0) { msg = null; logWhat = "skipped (" + why + "): no changes"; }   // nothing changed
                        else if (Settling(rs)) { msg = null; wait = true; logWhat = "waiting (" + why + "): the game is still writing its saves"; }   // the game is still writing: try again in a bit
                        else
                        {
                            int skipped; var b = Backups.Make(root, g2, rs, why, out skipped);
                            logWhat = "backed up (" + why + "): " + VaultSettings.Size(b.Bytes) + ", " + rs.Sum(x => x.Files) + " files" + (skipped > 0 ? ", " + skipped + " in use and skipped" : "");
                            S.LastSig[g2.Id] = sig; S.Save(); Backups.Prune(root, g2, keep);
                            var cut = Backups.PruneSize(root, maxGame, maxTotal);   // v1.3: size limits
                            msg = g2.Name + " backed up (" + VaultSettings.Size(b.Bytes) + ")" + (skipped > 0 ? " - " + skipped + " files were in use and skipped" : "") + "." +
                                (cut > 0 ? " " + cut + (cut == 1 ? " old backup was" : " old backups were") + " removed to stay under the size limit." : "");
                            if (!string.IsNullOrEmpty(copy))   // v1.3: second copy, pruned in step
                                try { Backups.Mirror(root, copy, copyKeep); } catch (Exception ex) { msg += " The second copy couldn't be made (" + ex.Message + ") - it will catch up next time."; logWhat += "; second copy failed: " + ex.Message; }
                            if (cut > 0) logWhat += "; " + cut + " old backup(s) removed for the size limit";
                        }
                    }
                    catch (Exception ex) { msg = "Couldn't back up " + g2.Name + ": " + ex.Message; logWhat = "failed (" + why + "): " + ex.Message; }
                    Log.Add(g2.Name, logWhat);
                    var m = msg; var gg = g2; var w2 = wait; var why2 = why; var al = alarm; var made = msg != null && msg.Contains(" backed up (");
                    var res = logWhat == null ? null : logWhat.StartsWith("backed up") ? "ok" : logWhat.StartsWith("failed") ? "failed" : logWhat.StartsWith("refused") ? "refused" : null;   // v1.13: skips and waits don't count
                    var resWhat = logWhat;
                    Dispatcher.BeginInvoke(new Action(() => { if (res != null) { S.Results[gg.Id] = res + "|" + DateTime.Now.ToString("o") + "|" + (resWhat ?? "").Replace("|", "/"); S.Save(); } if (w2) { Later(gg, why2, force2); return; } settleTries.Remove(gg.Id); if (al != null) RaiseAlarm(gg, al); else if (made && S.Alarms.Remove(gg.Id)) S.Save(); history[gg.Id] = Backups.List(Root, gg); roots[gg.Id] = Saves.Find(gg, steamUser, ExtraOf(gg)); Fill(); if (m != null) Status(m); }));
                }
                Dispatcher.BeginInvoke(new Action(() => { allBtn.IsEnabled = true; WriteStatus(); CheckCopy(); }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            Status("Backing up " + g.Name + "...");
        }

        // v1.2: never back up while the game is writing - a save file written in the last 10 seconds means "not yet"
        const int SettleSeconds = 10;
        static bool Settling(List<SaveRoot> rs)
        {
            var age = (DateTime.UtcNow - Saves.NewestWrite(rs)).TotalSeconds;
            return age < SettleSeconds && age > -60;   // (a clock far in the future is not "writing")
        }
        void Later(Game g, string why, bool force)
        {
            int n; settleTries.TryGetValue(g.Id, out n); n++;
            if (n > 40) { settleTries.Remove(g.Id); Status(g.Name + "'s saves kept changing for 10 minutes, so it wasn't backed up this time - it will be next time."); return; }
            settleTries[g.Id] = n;
            Status("Waiting for " + g.Name + " to finish writing its saves...");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            t.Tick += (o, e) => { t.Stop(); Enqueue(g, why, force); };
            t.Start();
        }

        // v1.2: back up a Steam game right before it updates (its manifest says an update is waiting or starting)
        void CheckSteamUpdates()
        {
            Dictionary<string, bool> st; try { st = Library.SteamUpdates(); } catch { return; }
            foreach (var g in games)
                foreach (var sg in new[] { g }.Concat(g.Alts).Where(x => x.Source == "steam"))
                {
                    var id = sg.Id.Substring(6); bool now, was;
                    if (!st.TryGetValue(id, out now)) continue;
                    var known = updState.TryGetValue(id, out was); updState[id] = now;
                    if (!now || (known && was) || !S.BeforeUpdate || S.Off.Contains(g.Id) || !roots.ContainsKey(g.Id) || roots[g.Id].Count == 0) continue;
                    Enqueue(g, "before a Steam update");   // only if something changed since the last backup
                }
        }

        void CheckDaily()
        {
            if (!S.Daily) return;
            DateTime last; if (DateTime.TryParse(S.LastDaily, out last) && (DateTime.Now - last).TotalHours < 20) return;
            S.LastDaily = DateTime.Now.ToString("o"); S.Save();
            foreach (var g in games.Where(x => !S.Off.Contains(x.Id) && ((roots.ContainsKey(x.Id) && roots[x.Id].Count > 0) || (history.ContainsKey(x.Id) && history[x.Id].Count > 0)))) Enqueue(g, "daily");   // v1.7: also notices a save folder that vanished
        }

        bool Running(Game g) { return tracker.Now.ContainsKey(g.Id) || g.Alts.Any(a => tracker.Now.ContainsKey(a.Id)); }

        void RestoreWindow(Game g)
        {
            BuildRestore(g).ShowDialog();
        }
        Window Dialog(string title, double width, Window owner)
        {
            var w = new Window { Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = Snap.On ? null : owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, ShowInTaskbar = false };
            Look.Follow(w);
            return w;
        }
        // v1.13: the safety backup before any restore (whole version or one file) - the saves as they are now, always, even if nothing changed.
        // Throws if it can't be made, so the restore doesn't happen.
        BackupInfo SafetyBackup(Game g, string reason)
        {
            var rs = Saves.Find(g, steamUser, ExtraOf(g)); if (rs.Count == 0) return null;   // nothing there to keep
            int sk; var b = Backups.Make(Root, g, rs, reason, out sk);
            Log.Add(g.Name, "safety backup (" + reason + "): " + VaultSettings.Size(b.Bytes) + ", " + rs.Sum(x => x.Files) + " files");
            return b;
        }
        Window BuildRestore(Game g)
        {
            var hs = Backups.List(Root, g);
            var w = Dialog("Restore " + g.Name, 600, this);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Restore " + g.Name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Pick a version. Your current saves are backed up first, so you can always come back. Preview shows what would change, and can put back just one file.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var lst = new StackPanel();
            foreach (var b in hs)
            {
                var bb = b; var r = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var go = new Button { Content = "Restore this", Style = (Style)FindResource("Btn") };
                go.Click += (s, e) =>
                {
                    if (Running(g)) { MessageBox.Show(w, "Close " + g.Name + " first - a running game would overwrite the restored files.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                    if (MessageBox.Show(w, "Put back " + g.Name + "'s saves from " + bb.When.ToString("dddd d MMM, HH:mm") + "?\n\nYour current saves are backed up first.", "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    try
                    {
                        SafetyBackup(g, "before restoring");   // safety copy (not pruned right away); if it fails, nothing is restored
                        var n = Backups.Restore(bb);
                        Log.Add(g.Name, "restored the version from " + bb.When.ToString("yyyy-MM-dd HH:mm") + " (" + n + " files; the saves from just before were backed up)");
                        S.LastSig.Remove(g.Id); S.Alarms.Remove(g.Id); S.Save();
                        w.Close(); history[g.Id] = Backups.List(Root, g); Fill();
                        Status(n + " files put back for " + g.Name + " (from " + bb.When.ToString("d MMM HH:mm") + "). Your saves from just before are kept as a backup too.");
                    }
                    catch (Exception ex) { MessageBox.Show(w, "Couldn't restore: " + ex.Message, "Restore"); }
                };
                DockPanel.SetDock(go, Dock.Right); r.Children.Add(go);
                var cmp = new Button { Content = "Preview", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "See what restoring this would change - files added, removed or changed since, with sizes and times - and put back just one file if you like" };
                cmp.Click += (s, e) => BuildCompare(g, hs, bb, w).ShowDialog();
                DockPanel.SetDock(cmp, Dock.Right); r.Children.Add(cmp);
                var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                // v1.7: pin - never pruned, never removed by size limits
                var pinRow = new StackPanel { Orientation = Orientation.Horizontal };
                var pinMark = Icons.Make("thumbtack", 12, Accent.Brush()); pinMark.Margin = new Thickness(0, 0, 6, 0); pinMark.VerticalAlignment = VerticalAlignment.Center; pinMark.Visibility = bb.Pinned ? Visibility.Visible : Visibility.Collapsed;
                pinRow.Children.Add(pinMark); pinRow.Children.Add(new TextBlock { Text = bb.When.ToString("dddd d MMM yyyy, HH:mm"), FontWeight = FontWeights.SemiBold });
                t.Children.Add(pinRow);
                var pin = new Button { Content = bb.Pinned ? "Unpin" : "Pin", Tag = "icon", Width = 32, Height = 30, Padding = new Thickness(0), Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0), ToolTip = bb.Pinned ? "Unpin - this version can be pruned again" : "Pin - this version is never pruned (before a big boss, a finished run)" };
                pin.Click += (s, e) =>
                {
                    try { Backups.SetPin(bb.File, !bb.Pinned); bb.Pinned = !bb.Pinned; } catch (Exception ex) { Status("Couldn't pin that: " + ex.Message); return; }
                    pin.Content = bb.Pinned ? "Unpin" : "Pin"; pin.ToolTip = bb.Pinned ? "Unpin - this version can be pruned again" : "Pin - this version is never pruned (before a big boss, a finished run)";
                    pinMark.Visibility = bb.Pinned ? Visibility.Visible : Visibility.Collapsed;
                    history[g.Id] = Backups.List(Root, g); if (!Snap.On) Fill();
                };
                DockPanel.SetDock(pin, Dock.Right); r.Children.Add(pin);
                t.Children.Add(new TextBlock { Text = VaultSettings.Ago(bb.When) + "   ·   " + VaultSettings.Size(bb.Bytes) + "   ·   " + (bb.Reason ?? ""), Foreground = (Brush)FindResource("Muted"), FontSize = 11.5 });
                ZipCheck zc; if (zipChecks.TryGetValue(bb.File, out zc) && zc.Level > 0)   // v1.12: what the last check found
                    t.Children.Add(new TextBlock { Text = "Check: " + Checker.Words[zc.Level] + " - " + zc.Note, Foreground = T(Checker.Colors[zc.Level]), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
                r.Children.Add(t); lst.Children.Add(r);
            }
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var close = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            sp.Children.Add(close); w.Content = sp; return w;
        }

        // v1.2: what changed between two versions (or a version and your saves now) - nothing is changed here
        Window BuildCompare(Game g, List<BackupInfo> hs, BackupInfo pick, Window owner)
        {
            var w = Dialog("Preview - " + g.Name, 820, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Preview " + g.Name + " versions", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Files added, removed, bigger, smaller or saved again since, with their sizes and times. Nothing changes here unless you press Restore file - and your saves are backed up first.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var from = new ComboBox { Style = (Style)FindResource("Combo"), Width = 260 }; var to = new ComboBox { Style = (Style)FindResource("Combo"), Width = 260 };
            Func<BackupInfo, ComboBoxItem> item = b => new ComboBoxItem { Content = b == null ? "Your saves now" : b.When.ToString("ddd d MMM, HH:mm") + (string.IsNullOrEmpty(b.Reason) ? "" : "  (" + b.Reason + ")"), Tag = b };
            foreach (var b in hs) from.Items.Add(item(b));
            to.Items.Add(item(null)); foreach (var b in hs) to.Items.Add(item(b));
            pickRow.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); pickRow.Children.Add(from);
            pickRow.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) }); pickRow.Children.Add(to);
            sp.Children.Add(pickRow);
            var sum = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(sum);
            var lst = new StackPanel();
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var rs = Saves.Find(g, steamUser, ExtraOf(g));
            var rootPaths = rs.Select(r => r.Path).Concat(hs.SelectMany(b => b.Roots ?? new List<string>())).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList();
            Func<string, string> shortName = p =>
            {
                var r0 = rootPaths.FirstOrDefault(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase));
                return r0 == null ? p : System.IO.Path.GetFileName(r0) + (p.Length > r0.Length ? p.Substring(r0.Length) : "");
            };
            var colors = new Dictionary<string, string> { { "added", "#6ED28C" }, { "removed", "#F07070" }, { "bigger", "#F0BE5A" }, { "smaller", "#F0BE5A" }, { "changed", "#A98BE0" } };
            Action refresh = null; refresh = () =>
            {
                lst.Children.Clear();
                var a = from.SelectedItem as ComboBoxItem; var b = to.SelectedItem as ComboBoxItem; if (a == null || b == null) return;
                try
                {
                    var older = Backups.Contents((BackupInfo)a.Tag);
                    var newer = b.Tag == null ? Saves.Contents(rs) : Backups.Contents((BackupInfo)b.Tag);
                    int same; var ch = Backups.Diff(older, newer, out same);
                    Func<string, int> cnt = k => ch.Count(c => c.Kind == k);
                    var parts = new List<string>();
                    if (cnt("added") > 0) parts.Add(cnt("added") + " added"); if (cnt("removed") > 0) parts.Add(cnt("removed") + " removed");
                    if (cnt("bigger") > 0) parts.Add(cnt("bigger") + " bigger"); if (cnt("smaller") > 0) parts.Add(cnt("smaller") + " smaller");
                    if (cnt("changed") > 0) parts.Add(cnt("changed") + " saved again (same size)");
                    sum.Text = ch.Count == 0 ? "No differences - " + same + (same == 1 ? " file is" : " files are") + " the same." : string.Join(", ", parts) + "   \u00B7   " + same + " the same";
                    var src = (BackupInfo)a.Tag; var vsNow = b.Tag == null;
                    if (vsNow && ch.Count > 0)   // v1.13: what restoring this version would do
                    {
                        int back = cnt("removed"), over = cnt("bigger") + cnt("smaller") + cnt("changed"), stay = cnt("added");
                        var what = new List<string>();
                        if (back > 0) what.Add("bring back " + back + (back == 1 ? " file" : " files"));
                        if (over > 0) what.Add("overwrite " + over + (over == 1 ? " file" : " files") + " with the backup's version");
                        if (stay > 0) what.Add("leave " + stay + " newer " + (stay == 1 ? "file" : "files") + " alone (a restore never deletes)");
                        if (what.Count > 0) sum.Text += "\nRestoring the whole version would " + string.Join(", ", what) + ".";
                    }
                    Func<DateTime?, string> tm = x => x == null || x.Value == Saves.RegTime ? "" : x.Value.ToString(x.Value.Year == DateTime.Now.Year ? "d MMM HH:mm" : "d MMM yyyy");
                    foreach (var c in ch.Take(400))
                    {
                        var cc = c;
                        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                        var tag = new TextBlock { Text = c.Kind, Width = 70, Foreground = T(colors[c.Kind]), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(tag, Dock.Left); d.Children.Add(tag);
                        var canPut = vsNow && c.Kind != "added" && !Reg.Is(System.IO.Path.GetDirectoryName(c.Path) ?? "") && !Reg.Is(c.Path);
                        var one = new Button { Content = "Restore file", Style = (Style)FindResource("Btn"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0), Width = 118, Visibility = canPut ? Visibility.Visible : Visibility.Hidden,
                            ToolTip = "Put back just this file from " + src.When.ToString("d MMM HH:mm") + " (your saves are backed up first)" };
                        one.Click += (s2, e2) =>
                        {
                            if (Running(g)) { MessageBox.Show(w, "Close " + g.Name + " first - a running game would overwrite the restored file.", "Restore file", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                            if (MessageBox.Show(w, "Put back " + System.IO.Path.GetFileName(cc.Path) + " from " + src.When.ToString("dddd d MMM, HH:mm") + "?\n\n" + cc.Path + "\n\nYour current saves are backed up first.", "Restore file", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                            try
                            {
                                SafetyBackup(g, "before restoring a file");
                                if (!Backups.RestoreFile(src, cc.Path)) { MessageBox.Show(w, "That backup doesn't have this file.", "Restore file"); return; }
                                Log.Add(g.Name, "restored one file from the version of " + src.When.ToString("yyyy-MM-dd HH:mm") + ": " + cc.Path + " (the saves from just before were backed up)");
                                S.LastSig.Remove(g.Id); S.Save();
                                rs = Saves.Find(g, steamUser, ExtraOf(g)); history[g.Id] = Backups.List(Root, g); if (!Snap.On) Fill();
                                Status(System.IO.Path.GetFileName(cc.Path) + " put back for " + g.Name + " (from " + src.When.ToString("d MMM HH:mm") + "). Your saves from just before are kept as a backup too.");
                                refresh();
                            }
                            catch (Exception ex) { MessageBox.Show(w, "Couldn't restore that file: " + ex.Message, "Restore file"); }
                        };
                        DockPanel.SetDock(one, Dock.Right); d.Children.Add(one);
                        var size = new TextBlock { Width = 130, TextAlignment = TextAlignment.Right, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center,
                            Text = c.Old < 0 ? VaultSettings.Size(c.New) : c.New < 0 ? VaultSettings.Size(c.Old) : c.Old == c.New ? VaultSettings.Size(c.New) : VaultSettings.Size(c.Old) + " \u2192 " + VaultSettings.Size(c.New) };
                        DockPanel.SetDock(size, Dock.Right); d.Children.Add(size);
                        var t1 = tm(c.OldWhen); var t2 = tm(c.NewWhen);
                        if (t1.Length > 0 && t1 == t2) { t1 = c.OldWhen.Value.ToString("d MMM HH:mm:ss"); t2 = c.NewWhen.Value.ToString("HH:mm:ss"); }   // saved again the same minute
                        var times = new TextBlock { Width = 200, TextAlignment = TextAlignment.Right, Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                            Text = t1.Length > 0 && t2.Length > 0 && t1 != t2 ? t1 + " \u2192 " + t2 : t1.Length > 0 ? t1 : t2, ToolTip = "When the file was last saved" + (t1.Length > 0 && t2.Length > 0 ? " (in the older version \u2192 in the newer one)" : "") };
                        DockPanel.SetDock(times, Dock.Right); d.Children.Add(times);
                        d.Children.Add(new TextBlock { Text = shortName(c.Path), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.Path, VerticalAlignment = VerticalAlignment.Center });
                        lst.Children.Add(d);
                    }
                    if (ch.Count > 400) lst.Children.Add(new TextBlock { Text = "... and " + (ch.Count - 400) + " more", Foreground = (Brush)FindResource("Muted") });
                }
                catch (Exception ex) { sum.Text = "Couldn't compare: " + ex.Message; }
            };
            from.SelectedItem = from.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag == pick) ?? (from.Items.Count > 0 ? from.Items[0] : null);
            to.SelectedIndex = 0;
            from.SelectionChanged += (s, e) => refresh(); to.SelectionChanged += (s, e) => refresh();
            refresh();
            var close = new Button { Content = "Close", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
            sp.Children.Add(close); w.Content = sp; return w;
        }

        void Status(string m) { status.Text = m; }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool StartsWithWindows() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HallowVault") != null; }
        void SetStartWithWindows(bool on)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) { if (on) k.SetValue("HallowVault", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue("HallowVault", false); } }
            catch (Exception ex) { Status("Couldn't change that: " + ex.Message); }
        }

        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Vault", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Open Hallow Vault"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowFromTray(); menu.Items.Add(open);
            var all = new WF.ToolStripMenuItem("Back up everything now"); all.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => { foreach (var g in Backable()) Enqueue(g, "you asked"); })); menu.Items.Add(all);
            var fam = new WF.ToolStripMenuItem("Back up the Hallow family now"); fam.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => { var r = FamilyBackupNow(); if (r != "ok") { ShowFromTray(); ShowFamily(); } })); menu.Items.Add(fam);   // v1.16
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += (s, e) => { var g = alarmGame; alarmGame = null; ShowFromTray(); if (g != null) RestoreWindow(g); };   // v1.7: "restore the last good one?"
        }
        public void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); Topmost = true; Topmost = false; }
        void Quit() { exiting = true; S.Save(); WriteStatus(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Application.Current.Shutdown(); }

        public void SnapAll(string dir)
        {
            if (!string.IsNullOrEmpty(S.CopyDir)) { copyState = Backups.CopyState(Root, S.CopyDir, S.CopyKeep); ShowCopyState(); Fill(); }
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), 1060, 740);
            settingsPanel.Visibility = Visibility.Visible; Snap.Save(this, System.IO.Path.Combine(dir, "settings.png"), 1060, 740);
            var ssv = settingsPanel.Child as ScrollViewer; if (ssv != null) { ssv.ScrollToEnd(); ssv.UpdateLayout(); Snap.Save(this, System.IO.Path.Combine(dir, "settings-end.png"), 1060, 740); ssv.ScrollToEnd(); ssv.UpdateLayout(); Snap.Save(this, System.IO.Path.Combine(dir, "settings-end.png"), 1060, 740); }
            var cr = Crash.Reports().FirstOrDefault(); if (cr != null) { var cw0 = BuildCrash(cr, this); Snap.Save(cw0, System.IO.Path.Combine(dir, "crash.png"), cw0.Width, 0); }
            var fg = games.FirstOrDefault(x => roots.ContainsKey(x.Id) && roots[x.Id].Count > 0 && x.Source == "hallow") ?? games.FirstOrDefault(x => roots.ContainsKey(x.Id) && roots[x.Id].Count > 0);
            if (fg != null) { var fw = BuildFilters(fg, this); Snap.Save(fw, System.IO.Path.Combine(dir, "filters.png"), fw.Width, 0); }
            var lw = BuildLog(this); Snap.Save(lw, System.IO.Path.Combine(dir, "log.png"), lw.Width, 0);
            // v1.12: add / edit a game, and a backup check of the whole vault (read-only)
            var aw = BuildCustom(null, this); Snap.Save(aw, System.IO.Path.Combine(dir, "addgame.png"), aw.Width, 0);
            var cg0 = S.Custom.FirstOrDefault(); if (cg0 != null) { var ew = BuildCustom(cg0, this); Snap.Save(ew, System.IO.Path.Combine(dir, "editgame.png"), ew.Width, 0); }
            ApplyCheck(null, Checker.All(Root, FolderNames(Root)));
            var kw = BuildCheck(this); Snap.Save(kw, System.IO.Path.Combine(dir, "check.png"), kw.Width, 0); checkBody = null;
            Fill(); ShowCheckState(); settingsPanel.Visibility = Visibility.Collapsed; Snap.Save(this, System.IO.Path.Combine(dir, "main-checked.png"), 1060, 740);
            settingsPanel.Visibility = Visibility.Visible; if (ssv != null) { ssv.ScrollToVerticalOffset(260); ssv.UpdateLayout(); } Snap.Save(this, System.IO.Path.Combine(dir, "settings-check.png"), 1060, 740);
            // test copies only: <test folder>\import holds an export to render the import window with
            var impDir = Link.TestDir == null ? null : System.IO.Path.Combine(Link.TestDir, "import"); var im = impDir == null ? null : Mover.Load(impDir);
            if (im != null) { var iw = BuildImport(impDir, im, this); Snap.Save(iw, System.IO.Path.Combine(dir, "import.png"), iw.Width, 0); }
            // the restore and compare windows for the first game with two or more backups (read-only)
            var g = games.FirstOrDefault(x => history.ContainsKey(x.Id) && history[x.Id].Count >= 2);
            if (g != null)
            {
                var rw = BuildRestore(g); Snap.Save(rw, System.IO.Path.Combine(dir, "restore.png"), rw.Width, 0);
                var hs = history[g.Id]; var cw = BuildCompare(g, hs, hs[hs.Count - 1], this); Snap.Save(cw, System.IO.Path.Combine(dir, "compare.png"), cw.Width, 0);
            }
            var wn = new WhatsNewWindow(WhatsNew.Since("1.12")); Snap.Save(wn, System.IO.Path.Combine(dir, "whatsnew.png"), wn.Width, 0);   // v1.14
            SnapFamily(dir);   // v1.16
            SnapLive(dir);
        }
        // v1.14, test copies only (HALLOW_SNAP_LIVE=light|dark): rewrite the TEST theme.json's mode while "running" and let the real
        // watcher (Accent.Watch -> ApplyAccent) switch the look, then snap the main window again as main-live.png
        void SnapLive(string dir)
        {
            var to = Environment.GetEnvironmentVariable("HALLOW_SNAP_LIVE");
            if (Link.TestDir == null || string.IsNullOrEmpty(to)) return;
            var before = Accent.CurrentLight; var beforeAccent = Accent.Current; int changes = 0;
            Accent.Watch(Dispatcher, () => { ApplyAccent(); changes++; });
            Directory.CreateDirectory(Accent.Folder);
            var file = System.IO.Path.Combine(Accent.Folder, "theme.json"); var old = Accent.ReadTheme() ?? "{}";
            var t = System.Text.RegularExpressions.Regex.Replace(old, "\"mode\"\\s*:\\s*\"[^\"]*\"", "\"mode\": \"" + to + "\"");
            if (t == old) t = old.TrimEnd().TrimEnd('}') + (old.Contains(":") ? ", " : "") + "\"mode\": \"" + to + "\" }";
            var until = DateTime.Now.AddSeconds(1);
            while (DateTime.Now < until) { DoEvents(); Thread.Sleep(20); }
            // v1.15: dialogs that are open while the look changes (one with something typed in it)
            const string typed = "Typed before the switch";
            Func<Dictionary<string, Window>> open = () =>
            {
                var d = new Dictionary<string, Window>();
                d["addgame"] = BuildCustom(null, this); d["log"] = BuildLog(this); d["whatsnew"] = new WhatsNewWindow(WhatsNew.Since("1.12"));
                ApplyCheck(null, Checker.All(Root, FolderNames(Root))); d["check"] = BuildCheck(this); checkBody = null;
                var g = games.FirstOrDefault(x => history.ContainsKey(x.Id) && history[x.Id].Count >= 2);
                if (g != null) { d["restore"] = BuildRestore(g); var hs = history[g.Id]; d["compare"] = BuildCompare(g, hs, hs[hs.Count - 1], this); }
                var tb = FirstTextBox(d["addgame"]); if (tb != null) tb.Text = typed;
                return d;
            };
            var live = open();
            var report = new List<string>();
            Action<string> compare = phase =>
            {
                var fresh = open();
                foreach (var k in live.Keys)
                {
                    string a = System.IO.Path.Combine(dir, k + "-" + phase + "-live.png"), b = System.IO.Path.Combine(dir, k + "-" + phase + "-fresh.png");
                    Snap.Save(live[k], a, live[k].Width, 0); Snap.Save(fresh[k], b, fresh[k].Width, 0);
                    int diff = PixelDiff(a, b);
                    report.Add((diff == 0 ? "SAME " : "DIFFERENT ") + phase + " " + k + ": the open window vs one opened after the change" + (diff == 0 ? "" : " (" + diff + " pixels differ)"));
                    if (diff == 0) { File.Delete(b); }
                }
                foreach (var w in fresh.Values) w.Close();
                var tb = FirstTextBox(live["addgame"]); report.Add((tb != null && tb.Text == typed ? "KEPT " : "LOST ") + phase + " addgame: text typed before the change");
            };
            File.WriteAllText(file + ".tmp", t); if (File.Exists(file)) File.Replace(file + ".tmp", file, null); else File.Move(file + ".tmp", file);
            until = DateTime.Now.AddSeconds(15);
            while (Accent.CurrentLight == before && DateTime.Now < until) { DoEvents(); Thread.Sleep(20); }
            var done = Accent.CurrentLight != before;
            report.Insert(0, (done ? "switched " : "NOT switched ") + (before ? "light" : "dark") + " -> " + (Accent.CurrentLight ? "light" : "dark"));
            Snap.Save(this, System.IO.Path.Combine(dir, "main-live.png"), 1060, 740);
            compare("mode");
            // then the accent changes while they are still open
            var acc = string.Equals(beforeAccent, "#EAB308", StringComparison.OrdinalIgnoreCase) ? "#22C55E" : "#EAB308";
            var t2 = System.Text.RegularExpressions.Regex.Replace(t, "\"accent\"\\s*:\\s*\"[^\"]*\"", "\"accent\": \"" + acc + "\"");
            if (t2 == t) t2 = t.TrimEnd().TrimEnd('}') + ", \"accent\": \"" + acc + "\" }";
            File.WriteAllText(file + ".tmp", t2); File.Replace(file + ".tmp", file, null);
            until = DateTime.Now.AddSeconds(15);
            while (!string.Equals(Accent.Current, acc, StringComparison.OrdinalIgnoreCase) && DateTime.Now < until) { DoEvents(); Thread.Sleep(20); }
            report.Add((string.Equals(Accent.Current, acc, StringComparison.OrdinalIgnoreCase) ? "accent changed " : "accent NOT changed ") + beforeAccent + " -> " + Accent.Current);
            compare("accent");
            File.WriteAllLines(System.IO.Path.Combine(dir, "live.txt"), report);
        }
        static TextBox FirstTextBox(DependencyObject o)
        {
            if (o == null) return null; var t = o as TextBox; if (t != null) return t;
            foreach (var c in LogicalTreeHelper.GetChildren(o)) { var r = FirstTextBox(c as DependencyObject); if (r != null) return r; }
            return null;
        }
        static int PixelDiff(string a, string b)
        {
            Func<string, BitmapSource> load = p => { using (var s = File.OpenRead(p)) return BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); };
            var x = load(a); var y = load(b);
            if (x.PixelWidth != y.PixelWidth || x.PixelHeight != y.PixelHeight) return int.MaxValue;
            int stride = x.PixelWidth * 4; var px = new byte[stride * x.PixelHeight]; var py = new byte[px.Length];
            new FormatConvertedBitmap(x, PixelFormats.Bgra32, null, 0).CopyPixels(px, stride, 0); new FormatConvertedBitmap(y, PixelFormats.Bgra32, null, 0).CopyPixels(py, stride, 0);
            int n = 0; for (int i = 0; i < px.Length; i += 4) if (px[i] != py[i] || px[i + 1] != py[i + 1] || px[i + 2] != py[i + 2] || px[i + 3] != py[i + 3]) n++;
            return n;
        }
        static void DoEvents()
        {
            var fr = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => fr.Continue = false));
            Dispatcher.PushFrame(fr);
        }
    }
}
