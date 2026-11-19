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
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HallowVault
{
    public static class Program
    {
        public const string Version = "1.0";
        [STAThread]
        public static void Main(string[] args)
        {
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; VaultSettings.Frozen = true;
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
                new VaultWindow(true).SnapAll(args[si + 1]); return;
            }
            bool created;
            var mutex = new Mutex(true, @"Local\HallowVault", out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowVaultShow");
            if (!created) { showEvent.Set(); return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            var w = new VaultWindow(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) w.Dispatcher.BeginInvoke(new Action(w.ShowFromTray)); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        static void LoadTheme(Application app)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowVault.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));
        }
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
        WF.NotifyIcon tray; DispatcherTimer poll, hourly;
        readonly object busy = new object(); bool working; readonly Queue<Tuple<Game, string>> queue = new Queue<Tuple<Game, string>>();

        string Root { get { return !string.IsNullOrEmpty(S.VaultDir) ? S.VaultDir : Backups.DefaultRoot(); } }
        static Game Hallow() { return new Game { Id = "hallow", Name = "Hallow apps", Source = "hallow" }; }

        public VaultWindow(bool hidden)
        {
            startHidden = hidden;
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
                    if (!toldTray && tray != null) { toldTray = true; tray.ShowBalloonTip(4000, "Hallow Vault", "Still running in the tray - it backs up your saves when a game closes. Right-click the icon to quit.", WF.ToolTipIcon.None); }
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
            Dispatcher.BeginInvoke(new Action(CheckDaily), DispatcherPriority.ApplicationIdle);
            if (!startHidden) Show();
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
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = B("#F07820"), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " VAULT", FontSize = 22, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(title, Dock.Left); head.Children.Add(title);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            allBtn = new Button { Content = "Back up everything", Style = (Style)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Back up every game whose saves changed since its last backup" };
            allBtn.Click += (s, e) => { foreach (var g in Backable()) Enqueue(g, "you asked"); };
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
            var gi = Icons.Make("gear", 16, B("#F07820")); gi.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(gi, Dock.Left); sh.Children.Add(gi);
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
            Func<string, bool, Action<bool>, CheckBox> sw = (text, on, set) =>
            {
                var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Style = (Style)FindResource("Switch") };
                c.Click += (s, e) => { set(c.IsChecked == true); S.Save(); }; sp.Children.Add(c); return c;
            };
            sw("Back up a game's saves when it closes", S.OnClose, v => S.OnClose = v);
            sw("Back up everything once a day", S.Daily, v => S.Daily = v);
            sw("Keep running in the tray when I close the window", S.TrayOnClose, v => S.TrayOnClose = v);
            sw("Start with Windows (in the tray)", StartsWithWindows(), v => SetStartWithWindows(v));
            sp.Children.Add(new TextBlock { Text = "Backups are ordinary .zip files you can open anywhere. Logs, caches and crash dumps are left out, and so are secrets like app keys.",
                Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "Hallow Vault " + Program.Version + "  -  icons: Font Awesome Free (CC BY 4.0)", Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0) });
            b.Child = sp; return b;
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
            var row = new Border { Background = (Brush)FindResource("Panel"), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Opacity = rs.Count == 0 || off ? 0.6 : 1 };
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
                var rst = new Button { Content = "Restore", Style = (Style)FindResource("Btn"), Margin = new Thickness(6, 0, 0, 0), IsEnabled = hs.Count > 0, ToolTip = hs.Count > 0 ? "Put an older version back" : "No backups yet" };
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
            var when = new TextBlock { Width = 150, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0),
                Text = rs.Count == 0 ? "" : off ? "turned off" : hs.Count == 0 ? "not backed up yet" : "backed up " + VaultSettings.Ago(last), Foreground = hs.Count == 0 && rs.Count > 0 && !off ? B("#F0BE5A") : (Brush)FindResource("Muted"),
                ToolTip = hs.Count == 0 ? null : hs.Count + (hs.Count == 1 ? " backup, " : " backups, ") + VaultSettings.Size(hs.Sum(x => x.Bytes)) };
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
            allBtn.IsEnabled = false;
            var root = Root; var keep = S.Keep;
            new Thread(() =>
            {
                while (true)
                {
                    Tuple<Game, string> job; lock (busy) { if (queue.Count == 0) { working = false; break; } job = queue.Dequeue(); }
                    var g2 = job.Item1; var force2 = job.Item2.StartsWith("!"); var why = job.Item2.TrimStart('!');
                    string msg;
                    try
                    {
                        var rs = Saves.Find(g2, steamUser, S.Extra.ContainsKey(g2.Id) ? S.Extra[g2.Id] : null);
                        var sig = Saves.Fingerprint(rs); string old; S.LastSig.TryGetValue(g2.Id, out old);
                        if (rs.Count == 0) msg = null;
                        else if (!force2 && sig == old && Backups.List(root, g2).Count > 0) msg = null;   // nothing changed
                        else
                        {
                            int skipped; var b = Backups.Make(root, g2, rs, why, out skipped);
                            S.LastSig[g2.Id] = sig; S.Save(); Backups.Prune(root, g2, keep);
                            msg = g2.Name + " backed up (" + VaultSettings.Size(b.Bytes) + ")" + (skipped > 0 ? " - " + skipped + " files were in use and skipped" : "") + ".";
                        }
                    }
                    catch (Exception ex) { msg = "Couldn't back up " + g2.Name + ": " + ex.Message; }
                    var m = msg; var gg = g2;
                    Dispatcher.BeginInvoke(new Action(() => { history[gg.Id] = Backups.List(Root, gg); roots[gg.Id] = Saves.Find(gg, steamUser, S.Extra.ContainsKey(gg.Id) ? S.Extra[gg.Id] : null); Fill(); if (m != null) Status(m); }));
                }
                Dispatcher.BeginInvoke(new Action(() => { allBtn.IsEnabled = true; }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            Status("Backing up " + g.Name + "...");
        }

        void CheckDaily()
        {
            if (!S.Daily) return;
            DateTime last; if (DateTime.TryParse(S.LastDaily, out last) && (DateTime.Now - last).TotalHours < 20) return;
            S.LastDaily = DateTime.Now.ToString("o"); S.Save();
            foreach (var g in Backable()) Enqueue(g, "daily");
        }

        bool Running(Game g) { return tracker.Now.ContainsKey(g.Id) || g.Alts.Any(a => tracker.Now.ContainsKey(a.Id)); }

        void RestoreWindow(Game g)
        {
            var hs = Backups.List(Root, g);
            var w = new Window { Title = "Restore " + g.Name, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("Bg"), Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, ShowInTaskbar = false };
            w.SourceInitialized += (s, e) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref dark, 4); };
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Restore " + g.Name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Pick a version. Your current saves are backed up first, so you can always come back.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
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
                        S.LastSig.Remove(g.Id); S.Save();
                        w.Close(); history[g.Id] = Backups.List(Root, g); Fill();
                        Status(n + " files put back for " + g.Name + " (from " + bb.When.ToString("d MMM HH:mm") + "). Your saves from just before are kept as a backup too.");
                    }
                    catch (Exception ex) { MessageBox.Show(w, "Couldn't restore: " + ex.Message, "Restore"); }
                };
                DockPanel.SetDock(go, Dock.Right); r.Children.Add(go);
                var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = bb.When.ToString("dddd d MMM yyyy, HH:mm"), FontWeight = FontWeights.SemiBold });
                t.Children.Add(new TextBlock { Text = VaultSettings.Ago(bb.When) + "   ·   " + VaultSettings.Size(bb.Bytes) + "   ·   " + (bb.Reason ?? ""), Foreground = (Brush)FindResource("Muted"), FontSize = 11.5 });
                r.Children.Add(t); lst.Children.Add(r);
            }
            sp.Children.Add(new ScrollViewer { Content = lst, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var close = new Button { Content = "Cancel", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            sp.Children.Add(close); w.Content = sp; w.ShowDialog();
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
        }
        public void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); Topmost = true; Topmost = false; }
        void Quit() { exiting = true; S.Save(); if (tray != null) { tray.Visible = false; tray.Dispose(); } Application.Current.Shutdown(); }

        public void SnapAll(string dir)
        {
            Snap.Save(this, System.IO.Path.Combine(dir, "main.png"), 1060, 740);
            settingsPanel.Visibility = Visibility.Visible; Snap.Save(this, System.IO.Path.Combine(dir, "settings.png"), 1060, 740);
        }
    }
}
