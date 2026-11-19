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
[assembly: AssemblyDescription("Backs up your game saves and settings, and puts any version back")]
[assembly: AssemblyVersion("1.9.0.0")]
[assembly: AssemblyFileVersion("1.9.0.0")]

namespace HallowVault
{
    public static class Program
    {
        public const string Version = "1.9";
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
        static void LoadTheme(Application app) { Accent.Apply(app, Accent.Want(VaultSettings.Load().UseHallowAccent)); }   // v1.6: with the Hallow accent
    }

    public class VaultWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static SolidColorBrush B(string hex) { return new SolidColorBrush(C(hex)); }

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
            Title = "Hallow Vault"; Width = 1060; Height = 740; MinWidth = 880; MinHeight = 560;
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text"); FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = AppIcon(); } catch { }
            Content = Build();
            SourceInitialized += (s, e) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); };
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
            Dispatcher.BeginInvoke(new Action(CheckDaily), DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(new Action(CheckSteamUpdates), DispatcherPriority.ApplicationIdle);
            DateTime lastCheck; if (!DateTime.TryParse(S.LastUpdateCheck, out lastCheck) || (DateTime.Now - lastCheck).TotalHours > 20) Dispatcher.BeginInvoke(new Action(() => CheckForUpdate(false)), DispatcherPriority.ApplicationIdle);
            hourly.Tick += (s, e) => { DateTime lc; if (!DateTime.TryParse(S.LastUpdateCheck, out lc) || (DateTime.Now - lc).TotalHours > 20) CheckForUpdate(false); };
            Link.Serve(LinkCommand);
            // What's new after an update (1.0 didn't remember the version, so someone with backups already is coming from 1.0)
            if (string.IsNullOrEmpty(S.LastSeenVersion)) S.LastSeenVersion = S.LastSig.Count > 0 ? "1.0" : Program.Version;
            S.Save();
            IsVisibleChanged += (s, e) => { if (IsVisible) { Dispatcher.BeginInvoke(new Action(ShowWhatsNew), DispatcherPriority.ApplicationIdle); Dispatcher.BeginInvoke(new Action(ShowCrash), DispatcherPriority.ApplicationIdle); } };
            Accent.Watch(Dispatcher, ApplyAccent);
            if (!startHidden) Show();
        }

        // ================================================================ v1.6: Hallow accent and crash reports
        void ApplyAccent()
        {
            var want = Accent.Want(S.UseHallowAccent); if (string.Equals(want, Accent.Current, StringComparison.OrdinalIgnoreCase)) return;
            Accent.Apply(Application.Current, want);
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
                d.Children.Add(new TextBlock { Text = what, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = what, Foreground = col == null ? (Brush)FindResource("Muted") : B(col) });
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
            var rs0 = Saves.Find(g, steamUser, S.Extra.ContainsKey(g.Id) ? S.Extra[g.Id] : null);
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
                roots[g.Id] = Saves.Find(g, steamUser, S.Extra.ContainsKey(g.Id) ? S.Extra[g.Id] : null); Fill();
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
            if (cmd != "status" && cmd != "backup" && cmd != "restore" && cmd != "open" && cmd != "menu" && cmd != "invoke" && cmd != "quit" && cmd != "pin" && cmd != "unpin") return "error unknown command";
            var r = Dispatcher.Invoke(new Func<string>(() => LinkOnUi(cmd, arg)), TimeSpan.FromSeconds(5));
            return r as string ?? "error Hallow Vault is busy, try again";
        }
        string LinkOnUi(string cmd, string arg)
        {
            if (cmd == "status") return "ok " + Link.Json(StatusData());
            if (cmd == "open") { ShowFromTray(); return "ok"; }
            if (cmd == "quit") { var qt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; qt.Tick += (s, e) => { qt.Stop(); Quit(); }; qt.Start(); return "ok"; }   // after the reply is sent
            if (cmd == "menu") return "ok " + Link.Json(new Dictionary<string, object> { { "app", "Vault" }, { "items", MenuItems() } });
            if (cmd == "invoke") return InvokeItem(arg);
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
                    { "lastBackup", Link.Time(hs.Count == 0 ? (DateTime?)null : hs[0].When) }, { "versions", hs.Count }, { "sizeBytes", hs.Sum(x => x.Bytes) }, { "off", S.Off.Contains(g.Id) }, { "pinned", hs.Count(x => x.Pinned) }, { "alarm", S.Alarms.ContainsKey(g.Id) } });
            }
            bool isBusy; lock (busy) isBusy = working;
            return new Dictionary<string, object> { { "app", "Vault" }, { "version", Program.Version }, { "updated", Link.Time(DateTime.Now) }, { "running", !exiting }, { "busy", isBusy }, { "lastCrash", Link.Time(Crash.Last()) }, { "games", gl } };
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
                        var cur = Saves.Find(local, steamUser, S.Extra.ContainsKey(local.Id) ? S.Extra[local.Id] : null);
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
            right.Children.Add(allBtn);
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
                Margin = new Thickness(0, 62, 22, 0), Visibility = Visibility.Collapsed, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 4 } };
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
            sw("Back up a game's saves when it closes", S.OnClose, v => S.OnClose = v);
            sw("Back up a game right before Steam updates it", S.BeforeUpdate, v => S.BeforeUpdate = v);
            sw("Back up everything once a day", S.Daily, v => S.Daily = v);
            sw("Keep running in the tray when I close the window", S.TrayOnClose, v => S.TrayOnClose = v);
            sw("Start with Windows (in the tray)", StartsWithWindows(), v => SetStartWithWindows(v));
            sw("Use the Hallow icon (Hallow Hub's tray menu, instead of a Vault icon)", S.UseHallowIcon, v => { S.UseHallowIcon = v; CheckTrayHost(); });
            sw("Use the Hallow accent colour (picked in Hallow Toolkit)", S.UseHallowAccent, v => { S.UseHallowAccent = v; Dispatcher.BeginInvoke(new Action(ApplyAccent)); });
            sp.Children.Add(new TextBlock { Text = "Backups are ordinary .zip files you can open anywhere. Logs, caches and crash dumps are left out, and so are secrets like app keys.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
                        var verRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var chk = new Button { Content = "Check for updates", Style = (Style)FindResource("Btn") }; chk.Click += (s, e) => CheckForUpdate(true);
            DockPanel.SetDock(chk, Dock.Right); verRow.Children.Add(chk);
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
            Action show = () => { path.Text = string.IsNullOrEmpty(S.CopyDir) ? "Off" : S.CopyDir; off.Visibility = string.IsNullOrEmpty(S.CopyDir) ? Visibility.Collapsed : Visibility.Visible; };
            pick.Click += (s, e) =>
            {
                using (var d = new WF.FolderBrowserDialog { Description = "Where should Hallow Vault keep a second copy of every backup? A OneDrive, Google Drive or Dropbox folder, or a USB drive.", ShowNewFolderButton = true })
                {
                    if (!string.IsNullOrEmpty(S.CopyDir)) d.SelectedPath = S.CopyDir;
                    if (d.ShowDialog() != WF.DialogResult.OK) return;
                    if (Backups.Overlaps(d.SelectedPath, Root)) { Status("Pick a folder outside " + Root + " for the second copy."); return; }
                    S.CopyDir = d.SelectedPath; S.Save(); show(); CopyNow();
                }
            };
            off.Click += (s, e) => { S.CopyDir = ""; S.Save(); show(); Status("No second copy from now on. Copies already made stay where they are."); };
            show();
            DockPanel.SetDock(off, Dock.Right); row.Children.Add(off); DockPanel.SetDock(pick, Dock.Right); row.Children.Add(pick); row.Children.Add(path); sp.Children.Add(row);
            sp.Children.Add(new TextBlock { Text = "A OneDrive, Google Drive or Dropbox folder, or a USB drive. Every backup is copied there too, and old ones go in step with the vault.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
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
            var logBtn = new Button { Content = "Backup log", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "What was backed up, skipped, refused or failed, and when" };
            logBtn.Click += (s, e) => BuildLog(this).ShowDialog();
            DockPanel.SetDock(logBtn, Dock.Right); mv.Children.Add(logBtn); DockPanel.SetDock(imp, Dock.Right); mv.Children.Add(imp); DockPanel.SetDock(exp, Dock.Right); mv.Children.Add(exp);
            mv.Children.Add(new TextBlock { Text = "Move to a new PC", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(mv);
            sp.Children.Add(new TextBlock { Text = "Export puts every game's newest version (and pinned ones) in one folder - a USB stick works. Import on the new PC puts them back, each after a safety backup.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        }
        // copies every backup the second copy doesn't have yet (in the background)
        void CopyNow()
        {
            var root = Root; var copy = S.CopyDir; if (string.IsNullOrEmpty(copy)) return;
            Status("Copying your backups to " + copy + "...");
            new Thread(() =>
            {
                string m; try { var n = Backups.Mirror(root, copy); m = n == 0 ? "The second copy in " + copy + " is up to date." : n + (n == 1 ? " backup" : " backups") + " copied to " + copy + ". New backups are copied there too."; }
                catch (Exception ex) { m = "Couldn't copy to " + copy + ": " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => Status(m)));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        // ================================================================ data -> screen
        IEnumerable<Game> Backable() { return games.Where(g => roots.ContainsKey(g.Id) && roots[g.Id].Count > 0 && !S.Off.Contains(g.Id)); }

        void Rescan()
        {
            steamUser = Saves.SteamUserDir();
            games = Library.Scan(new List<Game>());
            games.Add(Hallow());
            roots.Clear(); history.Clear();
            foreach (var g in games)
            {
                List<string> extra; S.Extra.TryGetValue(g.Id, out extra);
                roots[g.Id] = Saves.Find(g, steamUser, extra);
                history[g.Id] = Backups.List(Root, g);
            }
            Fill();
        }

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
                (S.OnClose ? "   ·   automatic when a game closes" : "") + (S.Daily ? " and daily" : "");
            WriteStatus();
        }
        FrameworkElement Label(string t)
        {
            return new TextBlock { Text = t, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = B("#A98BE0"), Margin = new Thickness(2, 14, 0, 8) };
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
            var addF = new Button { Content = "Add folder", Tag = rs.Count > 0 ? "icon" : null, Width = rs.Count > 0 ? 32 : double.NaN, Height = 30, Padding = rs.Count > 0 ? new Thickness(0) : new Thickness(12, 5, 12, 5), Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Add a folder to back up for this game" };
            addF.Click += (s, e) => AddFolder(g);
            btns.Children.Add(addF);
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
                Foreground = alarm != null ? B("#F07070") : hs.Count == 0 && rs.Count > 0 && !off ? B("#F0BE5A") : (Brush)FindResource("Muted"), FontWeight = alarm != null ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = alarm != null ? AlarmText(g, alarm) : hs.Count == 0 ? null : hs.Count + (hs.Count == 1 ? " backup" : " backups") + (pinned > 0 ? " (" + pinned + " pinned)" : "") + ", " + VaultSettings.Size(hs.Sum(x => x.Bytes)) };
            if (alarm != null && rs.Count == 0)   // the folder is gone: still offer Restore
            {
                var rst2 = new Button { Content = "Restore", Style = (Style)FindResource("Accent"), Margin = new Thickness(6, 0, 0, 0) }; rst2.Click += (s, e) => RestoreWindow(g); btns.Children.Insert(0, rst2);
            }
            DockPanel.SetDock(when, Dock.Right); dp.Children.Add(when);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = g.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
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
                S.Save(); roots[g.Id] = Saves.Find(g, steamUser, ex); Fill();
                Status(d.SelectedPath + " is backed up with " + g.Name + " from now on.");
            }
        }

        // backups run one at a time on a background thread; "force" also backs up when nothing changed
        void Enqueue(Game g, string reason, bool force = false)
        {
            lock (busy) { if (queue.Any(q => q.Item1.Id == g.Id)) return; queue.Enqueue(Tuple.Create(g, force ? "!" + reason : reason)); if (working) return; working = true; }
            allBtn.IsEnabled = false; WriteStatus();
            var root = Root; var keep = S.Keep; var maxGame = S.MaxGameMB * (1L << 20); var maxTotal = S.MaxTotalMB * (1L << 20); var copy = S.CopyDir;
            new Thread(() =>
            {
                while (true)
                {
                    Tuple<Game, string> job; lock (busy) { if (queue.Count == 0) { working = false; break; } job = queue.Dequeue(); }
                    var g2 = job.Item1; var force2 = job.Item2.StartsWith("!"); var why = job.Item2.TrimStart('!');
                    string msg; bool wait = false; string alarm = null; string logWhat = null;
                    try
                    {
                        var rs = Saves.Find(g2, steamUser, S.Extra.ContainsKey(g2.Id) ? S.Extra[g2.Id] : null);
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
                                try { Backups.Mirror(root, copy); } catch (Exception ex) { msg += " The second copy couldn't be made (" + ex.Message + ") - it will catch up next time."; logWhat += "; second copy failed: " + ex.Message; }
                            if (cut > 0) logWhat += "; " + cut + " old backup(s) removed for the size limit";
                        }
                    }
                    catch (Exception ex) { msg = "Couldn't back up " + g2.Name + ": " + ex.Message; logWhat = "failed (" + why + "): " + ex.Message; }
                    Log.Add(g2.Name, logWhat);
                    var m = msg; var gg = g2; var w2 = wait; var why2 = why; var al = alarm; var made = msg != null && msg.Contains(" backed up (");
                    Dispatcher.BeginInvoke(new Action(() => { if (w2) { Later(gg, why2, force2); return; } settleTries.Remove(gg.Id); if (al != null) RaiseAlarm(gg, al); else if (made && S.Alarms.Remove(gg.Id)) S.Save(); history[gg.Id] = Backups.List(Root, gg); roots[gg.Id] = Saves.Find(gg, steamUser, S.Extra.ContainsKey(gg.Id) ? S.Extra[gg.Id] : null); Fill(); if (m != null) Status(m); }));
                }
                Dispatcher.BeginInvoke(new Action(() => { allBtn.IsEnabled = true; WriteStatus(); }));
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
            w.SourceInitialized += (s, e) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref dark, 4); };
            return w;
        }
        Window BuildRestore(Game g)
        {
            var hs = Backups.List(Root, g);
            var w = Dialog("Restore " + g.Name, 600, this);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Restore " + g.Name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Pick a version. Your current saves are backed up first, so you can always come back. Compare shows what would change.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
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
                        var rs = Saves.Find(g, steamUser, S.Extra.ContainsKey(g.Id) ? S.Extra[g.Id] : null); int sk;
                        if (rs.Count > 0) Backups.Make(Root, g, rs, "before restoring", out sk);   // safety copy (not pruned right away)
                        var n = Backups.Restore(bb);
                        Log.Add(g.Name, "restored the version from " + bb.When.ToString("yyyy-MM-dd HH:mm") + " (" + n + " files; the saves from just before were backed up)");
                        S.LastSig.Remove(g.Id); S.Alarms.Remove(g.Id); S.Save();
                        w.Close(); history[g.Id] = Backups.List(Root, g); Fill();
                        Status(n + " files put back for " + g.Name + " (from " + bb.When.ToString("d MMM HH:mm") + "). Your saves from just before are kept as a backup too.");
                    }
                    catch (Exception ex) { MessageBox.Show(w, "Couldn't restore: " + ex.Message, "Restore"); }
                };
                DockPanel.SetDock(go, Dock.Right); r.Children.Add(go);
                var cmp = new Button { Content = "Compare", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "See which files are different from your saves now (or another version)" };
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
                r.Children.Add(t); lst.Children.Add(r);
            }
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var close = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            sp.Children.Add(close); w.Content = sp; return w;
        }

        // v1.2: what changed between two versions (or a version and your saves now) - nothing is changed here
        Window BuildCompare(Game g, List<BackupInfo> hs, BackupInfo pick, Window owner)
        {
            var w = Dialog("Compare - " + g.Name, 680, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Compare " + g.Name + " versions", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Files added, removed, bigger or smaller. Nothing is changed here.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
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
            var rs = Saves.Find(g, steamUser, S.Extra.ContainsKey(g.Id) ? S.Extra[g.Id] : null);
            var rootPaths = rs.Select(r => r.Path).Concat(hs.SelectMany(b => b.Roots ?? new List<string>())).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList();
            Func<string, string> shortName = p =>
            {
                var r0 = rootPaths.FirstOrDefault(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase));
                return r0 == null ? p : System.IO.Path.GetFileName(r0) + (p.Length > r0.Length ? p.Substring(r0.Length) : "");
            };
            var colors = new Dictionary<string, string> { { "added", "#6ED28C" }, { "removed", "#F07070" }, { "bigger", "#F0BE5A" }, { "smaller", "#F0BE5A" }, { "changed", "#A98BE0" } };
            Action refresh = () =>
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
                    foreach (var c in ch.Take(400))
                    {
                        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                        var tag = new TextBlock { Text = c.Kind, Width = 70, Foreground = B(colors[c.Kind]), FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(tag, Dock.Left); d.Children.Add(tag);
                        var size = new TextBlock { Width = 150, TextAlignment = TextAlignment.Right, Foreground = (Brush)FindResource("Muted"),
                            Text = c.Old < 0 ? VaultSettings.Size(c.New) : c.New < 0 ? VaultSettings.Size(c.Old) : c.Old == c.New ? VaultSettings.Size(c.New) : VaultSettings.Size(c.Old) + " \u2192 " + VaultSettings.Size(c.New) };
                        DockPanel.SetDock(size, Dock.Right); d.Children.Add(size);
                        d.Children.Add(new TextBlock { Text = shortName(c.Path), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.Path });
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
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), 1060, 740);
            settingsPanel.Visibility = Visibility.Visible; Snap.Save(this, System.IO.Path.Combine(dir, "settings.png"), 1060, 740);
            var cr = Crash.Reports().FirstOrDefault(); if (cr != null) { var cw0 = BuildCrash(cr, this); Snap.Save(cw0, System.IO.Path.Combine(dir, "crash.png"), cw0.Width, 0); }
            var fg = games.FirstOrDefault(x => roots.ContainsKey(x.Id) && roots[x.Id].Count > 0 && x.Source == "hallow") ?? games.FirstOrDefault(x => roots.ContainsKey(x.Id) && roots[x.Id].Count > 0);
            if (fg != null) { var fw = BuildFilters(fg, this); Snap.Save(fw, System.IO.Path.Combine(dir, "filters.png"), fw.Width, 0); }
            var lw = BuildLog(this); Snap.Save(lw, System.IO.Path.Combine(dir, "log.png"), lw.Width, 0);
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
        }
    }
}
