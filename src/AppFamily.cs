// Hallow Vault 1.16 - the Hallow family backup in the window: the Family backup window (what's backed up, destinations with free
// space, last success and last error, Back up now), Restore (pick a date, then per app; Test restore checks a version without
// restoring), the schedule (daily, and after changes), status.json's "family", the pipe's "backup-now" and Hub's menu item.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace HallowVault
{
    public partial class VaultWindow
    {
        FamilySettings F { get { return S.Family; } }
        DispatcherTimer famWatch; bool famBusy; string famNow;   // famNow: what the running backup is doing
        Window famWin; Action famRefresh;
        // destination facts (free space, reachable, versions there) are looked up in the background - an offline NAS can take a while to answer
        readonly Dictionary<string, Tuple<Family.DestInfo, int>> famCache = new Dictionary<string, Tuple<Family.DestInfo, int>>(StringComparer.OrdinalIgnoreCase);
        bool famCaching;
        void RefreshDestCache()
        {
            if (famCaching) return; famCaching = true; var paths = F.Dests.Select(d => d.Path).ToList();
            new Thread(() =>
            {
                var o = new Dictionary<string, Tuple<Family.DestInfo, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in paths) { var i = Family.Info(p); o[p] = Tuple.Create(i, i.Reachable ? Family.Versions(p).Count(x => x.ThisPc) : 0); }
                Dispatcher.BeginInvoke(new Action(() => { famCaching = false; lock (famCache) { famCache.Clear(); foreach (var kv in o) famCache[kv.Key] = kv.Value; } RefreshFamily(); WriteStatus(); }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }
        Tuple<Family.DestInfo, int> Cached(string p) { lock (famCache) { Tuple<Family.DestInfo, int> v; return famCache.TryGetValue(p, out v) ? v : null; } }
        string FamStaging { get { return System.IO.Path.Combine(VaultSettings.Dir, "family-backup"); } }

        void FamilyStart()
        {
            famWatch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };   // after changes (at most one new version every 3 hours) and daily
            famWatch.Tick += (s, e) => { FamilyTick(false, null); RefreshDestCache(); };
            famWatch.Start();
            RefreshDestCache();
            var first = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) }; first.Tick += (s, e) => { first.Stop(); FamilyTick(false, null); }; first.Start();
        }

        // one run: what's there now -> a new version when something changed (or you asked) -> every destination gets the newest
        void FamilyTick(bool force, Action<string> done)
        {
            if (Snap.On) return;
            FamilyNote();
            if (famBusy || !F.On || F.Dests.Count == 0) { if (done != null) done(famBusy ? "busy" : "off"); return; }
            famBusy = true; famNow = "Looking at the Hallow apps' folders..."; RefreshFamily();
            var clips = F.Clips; var keep = F.Keep; var lastSig = F.LastSig; var dests = F.Dests.Select(d => d.Path).ToList(); var staging = FamStaging;
            DateTime lb; var lastBackup = DateTime.TryParse(F.LastBackup, out lb) ? lb : (DateTime?)null;
            new Thread(() =>
            {
                var notes = new List<string>(); string err = null, made = null; FamilyManifest man = null; var results = new List<Family.DestResult>(); List<Family.Item> items = null; string sig = null;
                try
                {
                    items = Family.Collect(clips, notes); sig = Family.Signature(items);
                    Directory.CreateDirectory(staging);
                    var latest = Directory.GetFiles(staging, Family.Prefix + "*.zip").Where(f => Family.WhenOf(f) != null).OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
                    var now = DateTime.Now; var age = lastBackup == null ? double.MaxValue : (now - lastBackup.Value).TotalHours;
                    var changed = sig != lastSig || latest == null;
                    if (changed && (force || latest == null || age >= Family.MinGapHours))
                    {
                        Dispatcher.BeginInvoke(new Action(() => { famNow = "Making a new version..."; RefreshFamily(); }));
                        var zip = System.IO.Path.Combine(staging, Family.VersionName(now));
                        man = Family.MakeZip(items, zip, notes); made = zip;
                        foreach (var old in Directory.GetFiles(staging, "*.zip").Where(f => !string.Equals(f, zip, StringComparison.OrdinalIgnoreCase))) try { File.Delete(old); } catch { }   // Vault's own working copy (the versions are on the destinations)
                        latest = zip;
                    }
                    var clipItems = items.Where(x => x.Kind == "clips").ToList();
                    foreach (var d in dests)
                    {
                        Dispatcher.BeginInvoke(new Action(() => { famNow = "Copying to " + d + "..."; RefreshFamily(); }));
                        results.Add(Family.Deliver(d, latest, keep, clipItems));
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    famBusy = false; famNow = null; var now = DateTime.Now.ToString("o");
                    foreach (var r in results)
                    {
                        var d = F.Dests.FirstOrDefault(x => string.Equals(x.Path, r.Path, StringComparison.OrdinalIgnoreCase)); if (d == null) continue;
                        d.LastTry = now;
                        if (r.Ok) { d.LastSuccess = now; d.LastError = ""; d.LastErrorKind = ""; } else { d.LastError = r.Error ?? "failed"; d.LastErrorKind = r.Kind ?? "error"; }
                        if (r.Wrote || !r.Ok || r.Pruned > 0 || r.ClipsCopied > 0) Log.Add("Hallow family", r.Ok ? (r.Wrote ? "copied to " + r.Path : "up to date in " + r.Path) + (r.Pruned > 0 ? "; " + r.Pruned + " old version(s) to the Recycle Bin / Removed versions" : "") + (r.ClipsCopied > 0 ? "; " + r.ClipsCopied + " Clips favourite(s) copied" : "") : "couldn't copy to " + r.Path + ": " + r.Error);
                    }
                    if (made != null && man != null)
                    {
                        F.LastSig = sig; F.LastBackup = now; F.LastVersion = System.IO.Path.GetFileName(made);
                        F.LastFiles = man.Items.Count; F.LastBytes = man.Items.Sum(x => x.Bytes); F.LastApps = man.Items.Select(x => x.Kind + ":" + x.App).Distinct().Count();
                        Log.Add("Hallow family", "new version " + F.LastVersion + ": " + F.LastFiles + " files from " + F.LastApps + " apps (" + VaultSettings.Size(F.LastBytes) + ")" + (notes.Count > 0 ? "; " + string.Join("; ", notes.Take(5)) : ""));
                    }
                    else if (sig != null && items != null && F.LastSig != sig && results.Any(r => r.Ok)) { /* changed, but the last version is under 3 hours old: the next run makes it */ }
                    if (results.Any(r => r.Ok)) F.LastRun = now;
                    F.LastError = err ?? (results.Count > 0 && results.All(r => !r.Ok) ? "no destination could be reached or written" : "");
                    if (err != null) Log.Add("Hallow family", "failed: " + err);
                    S.Save(); WriteStatus(); RefreshFamily(); Fill(); RefreshDestCache();
                    var msg = err != null ? "Hallow family backup failed: " + err
                        : results.Count(r => r.Ok) + " of " + results.Count + (results.Count == 1 ? " destination" : " destinations") + " up to date" + (made != null ? " - new version with " + F.LastFiles + " files" : "") + (results.Any(r => !r.Ok) ? ". " + results.First(r => !r.Ok).Error : ".");
                    if (done != null) done(msg);
                }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Hallow family backup" }.Start();
        }

        // a quiet note (at most once a day, never while a game runs) when the family backup hasn't worked for 3 days
        void FamilyNote()
        {
            if (!Family.Stale(F, DateTime.Now) || tracker.Now.Count > 0) return;
            var today = DateTime.Now.ToString("yyyy-MM-dd"); if (F.LastNote == today) return;
            F.LastNote = today; S.Save();
            var bad = F.Dests.FirstOrDefault(d => d.LastError.Length > 0);
            Notify("Hallow family backup", "It hasn't run for " + Family.StaleDays + " days" + (bad != null ? ": " + bad.LastError : ".") + " Open Hallow Vault > Family backup.");
        }
        string FamilyAgo() { DateTime t; return DateTime.TryParse(F.LastBackup, out t) ? VaultSettings.Ago(t) : "never"; }
        string FamilySummary()
        {
            if (!F.On || F.Dests.Count == 0) return "   \u00B7   Hallow family backup: not set up";
            if (Family.Stale(F, DateTime.Now)) return "   \u00B7   Hallow family backup hasn't run for " + Family.StaleDays + "+ days";
            return "   \u00B7   Hallow family backed up " + FamilyAgo();
        }

        // ---- the family link
        string FamilyBackupNow()
        {
            if (F.Dests.Count == 0) return "error the Hallow family backup isn't set up yet (Hallow Vault > Family backup: add a destination)";
            if (!F.On) return "error the Hallow family backup is turned off (Hallow Vault > Family backup)";
            if (famBusy) return "error a family backup is already running";
            FamilyTick(true, null); return "ok";
        }
        Dictionary<string, object> FamilyStatus()
        {
            DateTime t;
            var ds = F.Dests.Select(d =>
            {
                var c = Cached(d.Path); var i = c == null ? null : c.Item1;
                return (object)new Dictionary<string, object> { { "path", d.Path }, { "kind", i == null ? null : i.Kind }, { "reachable", i == null ? (object)null : i.Reachable }, { "freeBytes", i != null && i.Free >= 0 ? (object)i.Free : null }, { "versions", c == null ? 0 : c.Item2 },
                    { "lastSuccess", DateTime.TryParse(d.LastSuccess, out t) ? Link.Time(t) : null }, { "lastError", d.LastError.Length > 0 ? d.LastError : null },
                    { "blocked", d.LastErrorKind == "blocked" } };
            }).ToList();
            var errors = F.Dests.Where(d => d.LastError.Length > 0).Select(d => (object)(d.Path + ": " + d.LastError)).ToList();
            if (F.LastError.Length > 0 && errors.Count == 0) errors.Add(F.LastError);
            int versions = 0; foreach (var d in F.Dests) { var c = Cached(d.Path); if (c != null && c.Item2 > versions) versions = c.Item2; }
            return new Dictionary<string, object> { { "on", F.On && F.Dests.Count > 0 }, { "busy", famBusy },
                { "lastBackup", DateTime.TryParse(F.LastBackup, out t) ? Link.Time(t) : null }, { "lastRun", DateTime.TryParse(F.LastRun, out t) ? Link.Time(t) : null },
                { "count", versions }, { "files", F.LastFiles }, { "apps", F.LastApps }, { "sizeBytes", F.LastBytes }, { "keep", F.Keep }, { "clips", F.Clips },
                { "stale", Family.Stale(F, DateTime.Now) }, { "errors", errors }, { "destinations", ds } };
        }

        // --snap (test copies only): the Family backup window and Restore with a test restore, from the test folder's fake family
        void SnapFamily(string dir)
        {
            if (Link.TestDir == null || F.Dests.Count == 0) return;
            lock (famCache) foreach (var d in F.Dests) { var i = Family.Info(d.Path); famCache[d.Path] = Tuple.Create(i, i.Reachable ? Family.Versions(d.Path).Count(x => x.ThisPc) : 0); }
            Fill(); Snap.Save(this, System.IO.Path.Combine(dir, "main-family.png"), 1060, 740);
            var fw = BuildFamily(this); Snap.Save(fw, System.IO.Path.Combine(dir, "family.png"), fw.Width, 0);
            var rw = BuildFamilyRestore(this); Snap.Save(rw, System.IO.Path.Combine(dir, "family-restore.png"), rw.Width, 0);
        }
        // ================================================================ the Family backup window
        void ShowFamily()
        {
            if (famWin != null && famWin.IsVisible) { famWin.Activate(); return; }
            RefreshDestCache(); famWin = BuildFamily(this); famWin.Closed += (s, e) => { famWin = null; famRefresh = null; }; famWin.Show();
        }
        void RefreshFamily() { if (famRefresh != null) try { famRefresh(); } catch { } }
        TextBlock Muted(string text, double size = 11.5, double top = 0)
        {
            return new TextBlock { Text = text, Foreground = (Brush)FindResource("Muted"), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 4) };
        }
        static string Pl(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }
        TextBlock Head(string text) { return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 12, 0, 4) }; }

        Window BuildFamily(Window owner)
        {
            var w = Dialog("Hallow family backup", 760, owner);
            var outer = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var ti = Icons.Make("vault", 20, Accent.Brush()); ti.Margin = new Thickness(0, 0, 10, 0); ti.VerticalAlignment = VerticalAlignment.Center; title.Children.Add(ti);
            title.Children.Add(new TextBlock { Text = "Hallow family backup", FontSize = 17, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            outer.Children.Add(title);
            outer.Children.Add(Muted("One place for the whole Hallow family: every Hallow app's settings, Hallow Keys' and Hallow Authenticator's encrypted backups (copied exactly as they are - Vault never opens them), and Clips favourites if you like. Daily, and soon after something changes, to every destination below.", 12, 2));
            var body = new StackPanel();
            var sv = new ScrollViewer { Content = body, MaxHeight = Snap.On ? double.PositiveInfinity : 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            outer.Children.Add(sv);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var restore = new Button { Content = "Restore...", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Pick a date, then which apps to put back - or Test restore to check a version without restoring" };
            restore.Click += (s, e) => BuildFamilyRestore(w).ShowDialog();
            var now = new Button { Content = "Back up now", Style = (Style)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0) };
            now.Click += (s, e) =>
            {
                var r = FamilyBackupNow(); if (r != "ok") { Status(r.Substring(6)); MessageBox.Show(w, char.ToUpper(r[6]) + r.Substring(7) + ".", "Hallow family backup", MessageBoxButton.OK, MessageBoxImage.Information); }
            };
            var close = new Button { Content = "Close", Style = (Style)FindResource("Btn"), IsCancel = true };
            close.Click += (s, e) => w.Close();
            buttons.Children.Add(restore); buttons.Children.Add(now); buttons.Children.Add(close); outer.Children.Add(buttons);
            Action fill = null;
            fill = () =>
            {
                body.Children.Clear();
                // ---- state
                DateTime lr; var hasRun = DateTime.TryParse(F.LastRun, out lr);
                string line, col;
                if (F.Dests.Count == 0) { line = "Not set up yet - add a destination below (another drive, a USB drive or your NAS)."; col = "#F0BE5A"; }
                else if (!F.On) { line = "Turned off - nothing is backed up until you turn it on."; col = "#F0BE5A"; }
                else if (famBusy) { line = famNow ?? "Backing up..."; col = "#A98BE0"; }
                else if (Family.Stale(F, DateTime.Now)) { line = "Hasn't run for " + Family.StaleDays + " days or more" + (F.Dests.Any(d => d.LastError.Length > 0) ? " - see the destinations below." : "."); col = "#F07070"; }
                else if (F.LastBackup.Length == 0) { line = "No version yet - Back up now makes the first one."; col = "#F0BE5A"; }
                else { line = "Last new version " + FamilyAgo() + " (" + F.LastFiles + " files from " + F.LastApps + " apps, " + VaultSettings.Size(F.LastBytes) + ")" + (hasRun ? "   \u00B7   checked " + VaultSettings.Ago(lr) : "") + (F.Dests.Any(d => d.LastError.Length > 0) ? "   \u00B7   a destination needs a look" : ""); col = F.Dests.Any(d => d.LastError.Length > 0) ? "#F0BE5A" : "#6ED28C"; }
                var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
                var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = B(col), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                st.Children.Add(dot); st.Children.Add(new TextBlock { Text = line, Foreground = T(col), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
                body.Children.Add(st);
                var on = new CheckBox { Content = new TextBlock { Text = "Back up the Hallow family (daily, and after changes - at most one new version every 3 hours)", TextWrapping = TextWrapping.Wrap }, IsChecked = F.On, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 6, 0, 0), IsEnabled = F.Dests.Count > 0 };
                on.Click += (s, e) => { F.On = on.IsChecked == true; if (F.On && F.OnSince.Length == 0) F.OnSince = DateTime.Now.ToString("o"); S.Save(); WriteStatus(); Fill(); fill(); if (F.On) FamilyTick(false, null); };
                body.Children.Add(on);

                // ---- what's backed up (names, sizes and times only - nothing is opened)
                body.Children.Add(Head("What's backed up"));
                var notes = new List<string>(); var items = Family.Collect(true, notes);
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                int row = 0;
                Action<string, string, string> addRow = (label, right, rcol) =>
                {
                    grid.RowDefinitions.Add(new RowDefinition());
                    var a = new TextBlock { Text = label, Margin = new Thickness(0, 1, 12, 1), TextWrapping = TextWrapping.Wrap }; Grid.SetRow(a, row); grid.Children.Add(a);
                    var b = new TextBlock { Text = right, Foreground = rcol == null ? (Brush)FindResource("Muted") : T(rcol), Margin = new Thickness(0, 1, 0, 1), HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(b, row); Grid.SetColumn(b, 1); grid.Children.Add(b);
                    row++;
                };
                foreach (var g in items.Where(x => x.Kind == "settings").GroupBy(x => x.App).OrderBy(x => x.Key == "Hallow" ? "" : x.Key))
                    addRow(Family.Label("settings", g.Key), g.Count() + (g.Count() == 1 ? " file, " : " files, ") + VaultSettings.Size(g.Sum(x => x.Bytes)), null);
                var keys = items.Where(x => x.Kind == "keys").ToList(); var auth = items.Where(x => x.Kind == "authenticator").ToList(); var clips = items.Where(x => x.Kind == "clips").ToList();
                addRow(Family.Label("keys", "Keys") + "  (from " + Family.KeysBackupFolder() + ")", keys.Count == 0 ? "none yet" : Pl(keys.Count, "file") + ", " + VaultSettings.Size(keys.Sum(x => x.Bytes)) + " - never opened", keys.Count == 0 ? "#F0BE5A" : null);
                addRow(Family.Label("authenticator", "Authenticator"), auth.Count == 0 ? "none yet" : Pl(auth.Count, "file") + ", " + VaultSettings.Size(auth.Sum(x => x.Bytes)) + " - never opened", null);
                body.Children.Add(grid);
                if (keys.Count == 0) body.Children.Add(Muted("Hallow Keys hasn't made an automatic encrypted backup yet (Keys > Don't lose access > Automatic encrypted backups). Vault copies them from there as they are.", 11));
                var cl = new CheckBox { Content = new TextBlock { Text = "Also copy Hallow Clips favourites (" + (clips.Count == 0 ? "none starred now" : Pl(clips.Count, "clip") + ", " + VaultSettings.Size(clips.Sum(x => x.Bytes))) + ") - copied once to each destination, not versioned", TextWrapping = TextWrapping.Wrap }, IsChecked = F.Clips, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 4, 0, 0) };
                cl.Click += (s, e) => { F.Clips = cl.IsChecked == true; S.Save(); };
                body.Children.Add(cl);
                body.Children.Add(Muted("Left out: caches, WebView2 browser data, logs, crash dumps, temp files, programs and secrets (app keys, Hallow Keys' vault file and Secret Key file, VPN sign-in). Settings files over 16 MB are left out too." + (notes.Count > 0 ? " " + string.Join("; ", notes.Take(3)) + "." : ""), 11, 4));
                var keepRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var kb = new ComboBox { Style = (Style)FindResource("Combo"), Width = 170 };
                foreach (var n in new[] { 7, 14, 30, 60 }) kb.Items.Add(new ComboBoxItem { Content = "Newest " + n, Tag = n });
                kb.SelectedItem = kb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == F.Keep) ?? kb.Items[1];
                kb.SelectionChanged += (s, e) => { var it = kb.SelectedItem as ComboBoxItem; if (it != null) { F.Keep = (int)it.Tag; S.Save(); } };
                DockPanel.SetDock(kb, Dock.Right); keepRow.Children.Add(kb);
                keepRow.Children.Add(new TextBlock { Text = "Versions to keep on each destination (older ones go to the Recycle Bin)", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(keepRow);

                // ---- destinations
                body.Children.Add(Head("Destinations"));
                if (F.Dests.Count == 0) body.Children.Add(Muted("None yet. Another drive, a USB drive or a NAS share on your own network (\\\\NAS\\share). Not Documents, Desktop or Pictures - Windows' Ransomware protection guards those.", 11.5));
                foreach (var d in F.Dests.ToList())
                {
                    var dd = d; var cached = Cached(d.Path); var info = cached == null ? new Family.DestInfo { Reachable = true, Kind = "" } : cached.Item1;
                    var card = new Border { Background = (Brush)FindResource("Panel"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6) };
                    var dp = new DockPanel();
                    var rm = new Button { Content = "Remove", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Stop backing up here (the versions already there stay)" };
                    rm.Click += (s, e) =>
                    {
                        if (!Snap.On && MessageBox.Show(w, "Stop backing up to " + dd.Path + "?\n\nThe versions already there stay where they are.", "Hallow family backup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        F.Dests.Remove(dd); if (F.Dests.Count == 0) F.On = false; S.Save(); WriteStatus(); Fill(); fill(); RefreshDestCache();
                    };
                    DockPanel.SetDock(rm, Dock.Right); dp.Children.Add(rm);
                    var ic = Icons.Make("hard-drive", 16, B(info.Reachable ? "#8F88A0" : "#F07070")); ic.Margin = new Thickness(0, 2, 10, 0); ic.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(ic, Dock.Left); dp.Children.Add(ic);
                    var tx = new StackPanel();
                    tx.Children.Add(new TextBlock { Text = d.Path, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    DateTime ls; var vers = cached == null ? -1 : cached.Item2;
                    tx.Children.Add(new TextBlock { Text = (info.Kind.Length > 0 ? char.ToUpper(info.Kind[0]) + info.Kind.Substring(1) : "Folder") + "   \u00B7   " + (cached == null ? "checking..." : info.Reachable ? (info.Free >= 0 ? VaultSettings.Size(info.Free) + " free" : "free space unknown") : "not reachable now") + "   \u00B7   last success " + (DateTime.TryParse(d.LastSuccess, out ls) ? VaultSettings.Ago(ls) : "never") + (vers < 0 ? "" : "   \u00B7   " + vers + (vers == 1 ? " version" : " versions")), Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                    if (d.LastError.Length > 0)
                    {
                        tx.Children.Add(new TextBlock { Text = (d.LastErrorKind == "unreachable" ? "Last try: " : "Last error: ") + d.LastError, Foreground = T(d.LastErrorKind == "unreachable" ? "#F0BE5A" : "#F07070"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                        if (d.LastErrorKind == "blocked") tx.Children.Add(BlockedHelp(w));
                    }
                    else if (info.Guarded) tx.Children.Add(new TextBlock { Text = "This folder is guarded by Windows' Ransomware protection (Controlled folder access), which can block Hallow Vault from writing here. A folder outside Documents, Desktop and Pictures is safer.", Foreground = T("#F0BE5A"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                    if (info.Reachable && info.Free >= 0 && info.Free < 200L << 20) tx.Children.Add(new TextBlock { Text = "Almost full.", Foreground = T("#F0BE5A"), FontSize = 11.5 });
                    dp.Children.Add(tx); card.Child = dp; body.Children.Add(card);
                }
                // ---- add one
                var addRowP = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var pick = new Button { Content = "Pick a folder...", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
                pick.Click += (s, e) =>
                {
                    using (var dlg = new WF.FolderBrowserDialog { Description = "Where should the Hallow family backup go? Another drive, a USB drive or your NAS - not Documents, Desktop or Pictures (Windows' Ransomware protection guards those).", ShowNewFolderButton = true })
                        if (dlg.ShowDialog() == WF.DialogResult.OK) AddDest(dlg.SelectedPath, w, fill);
                };
                var typed = new TextBox { Style = (Style)FindResource("Field"), ToolTip = "A full path: E:\\Backups, a USB drive (F:\\) or a NAS share like \\\\NAS\\share\\Backups" };
                var use = new Button { Content = "Use this path", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
                use.Click += (s, e) => { if (AddDest(typed.Text, w, fill)) typed.Text = ""; };
                DockPanel.SetDock(pick, Dock.Right); addRowP.Children.Add(pick); DockPanel.SetDock(use, Dock.Right); addRowP.Children.Add(use);
                var tl = new TextBlock { Text = "Add", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }; DockPanel.SetDock(tl, Dock.Left); addRowP.Children.Add(tl);
                addRowP.Children.Add(typed); body.Children.Add(addRowP);
                var sug = Snap.On && Link.TestDir != null ? null : Family.Suggest();
                if (sug != null && !F.Dests.Any(x => x.Path.StartsWith(sug, StringComparison.OrdinalIgnoreCase)))
                {
                    var sb = new Button { Content = "Use " + sug + " (another drive)", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
                    sb.Click += (s, e) => AddDest(sug, w, fill); body.Children.Add(sb);
                }
                body.Children.Add(Muted("Each destination gets a folder \"Hallow family backup\\" + Family.PcName + "\" with the versions (zip files you can open anywhere; family.json lists every file with its SHA-256). Every copy is read back and checked before it counts. A NAS share works once it's set up - only addresses on your own network. On a USB drive or a NAS (no Recycle Bin there) old versions go to a \"Removed versions\" folder instead - delete it yourself when you like.", 11, 2));
            };
            famRefresh = () => { if (w.IsVisible || Snap.On) fill(); };
            fill(); w.Content = outer; return w;
        }
        // "Windows' Ransomware protection blocked this folder": allow Hallow Vault in Windows Security (Hallow Guard 1.10's page shows how)
        FrameworkElement BlockedHelp(Window owner)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var allow = new Button { Content = "Allow Hallow Vault...", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Hallow Guard's Ransomware page (Recently blocked, with the steps), or Windows Security's Allow an app through Controlled folder access" };
            allow.Click += (s, e) => OpenAllowHelper();
            row.Children.Add(allow);
            row.Children.Add(new TextBlock { Text = "Windows asks for permission itself; Vault never changes it.", Foreground = (Brush)FindResource("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            return row;
        }
        void OpenAllowHelper()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r; var ok = false;
                try { ok = Link.SendTo("Guard", "open ransomware", 300, 2000, out r) == 0; } catch { }
                if (ok) return;
                try { Process.Start(new ProcessStartInfo("windowsdefender://allowappthroughfolder/") { UseShellExecute = true }); }
                catch { Dispatcher.BeginInvoke(new Action(() => Status("Open Windows Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled folder access, and add Hallow Vault."))); }
            });
        }
        bool AddDest(string path, Window owner, Action fill)
        {
            string clean; var err = Family.CheckDest(path, F.Dests.Select(d => d.Path), out clean);
            if (err != null)
            {
                if (err == Family.BlockedText)
                {
                    if (MessageBox.Show(owner, err + "\n\nOpen the page where you can allow Hallow Vault?", "Hallow family backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) OpenAllowHelper();
                }
                else MessageBox.Show(owner, err, "Hallow family backup", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (Family.Guarded(clean) && MessageBox.Show(owner, clean + " is guarded by Windows' Ransomware protection (it's on this PC), which can block Hallow Vault from writing there later.\n\nA folder outside Documents, Desktop and Pictures is safer. Use it anyway?", "Hallow family backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            F.Dests.Add(new FamilyDest { Path = clean });
            if (!F.On) { F.On = true; if (F.OnSince.Length == 0) F.OnSince = DateTime.Now.ToString("o"); }
            S.Save(); WriteStatus(); Fill(); fill(); RefreshDestCache();
            FamilyTick(false, m => Status(m));
            return true;
        }

        // ================================================================ restore: a date, then per app (Test restore checks without restoring)
        Window BuildFamilyRestore(Window owner)
        {
            var w = Dialog("Restore the Hallow family", 780, owner);
            var sp = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            sp.Children.Add(new TextBlock { Text = "Restore from the Hallow family backup", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(Muted("Pick a date, then the apps to put back. Settings go back only once that app is closed - Vault asks it to quit first - and the files they replace are kept. Hallow Keys and Authenticator backups go back as files into the folder that app restores from; you finish in that app. Test restore checks a version without restoring anything.", 12, 0));
            var dests = F.Dests.Where(d => { var c = Cached(d.Path); return c == null || c.Item1.Reachable; }).ToList();
            var top = new DockPanel { Margin = new Thickness(0, 8, 0, 6) };
            var from = new ComboBox { Style = (Style)FindResource("Combo"), MinWidth = 300 };
            foreach (var d in dests) from.Items.Add(new ComboBoxItem { Content = d.Path, Tag = d.Path });
            var fl = new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }; DockPanel.SetDock(fl, Dock.Left); top.Children.Add(fl);
            top.Children.Add(from); sp.Children.Add(top);
            var cols = new Grid(); cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var vlist = new StackPanel(); var vsv = new ScrollViewer { Content = vlist, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 12, 0) };
            var apps = new StackPanel(); var asv = new ScrollViewer { Content = apps, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(asv, 1); cols.Children.Add(vsv); cols.Children.Add(asv); sp.Children.Add(cols);
            var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) }; sp.Children.Add(result);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var test = new Button { Content = "Test restore", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Open the version and check every file in it against its SHA-256 - nothing is restored or changed" };
            var go = new Button { Content = "Restore ticked", Style = (Style)FindResource("Accent"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            var cancel = new Button { Content = "Close", Style = (Style)FindResource("Btn"), IsCancel = true };
            row.Children.Add(test); row.Children.Add(go); row.Children.Add(cancel); sp.Children.Add(row);
            Family.Snapshot pickV = null; string pickDest = null; var boxes = new List<Tuple<CheckBox, Family.Group>>();
            Action showApps = () =>
            {
                apps.Children.Clear(); boxes.Clear(); go.IsEnabled = false;
                if (pickV == null) { apps.Children.Add(Muted("Pick a date on the left.")); return; }
                var man = Family.ReadManifest(pickV.File);
                if (man == null) { apps.Children.Add(new TextBlock { Text = "This version can't be read - try Test restore, or another date.", Foreground = T("#F07070"), TextWrapping = TextWrapping.Wrap }); return; }
                apps.Children.Add(new TextBlock { Text = pickV.When.ToString("dddd d MMM yyyy, HH:mm") + (pickV.ThisPc ? "" : "  (from " + pickV.Pc + ")"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                foreach (var g in Family.Groups(man))
                {
                    var cb = new CheckBox { Content = new TextBlock { Text = g.Label + "   \u00B7   " + g.Files + (g.Files == 1 ? " file" : " files") + ", " + VaultSettings.Size(g.Bytes) + (g.Kind == "settings" && g.App == "Vault" ? "   \u00B7   not while Vault runs" : ""), TextWrapping = TextWrapping.Wrap }, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 0, 0, 2), IsEnabled = !(g.Kind == "settings" && g.App == "Vault") };
                    cb.Click += (s, e) => go.IsEnabled = boxes.Any(b => b.Item1.IsChecked == true);
                    boxes.Add(Tuple.Create(cb, g)); apps.Children.Add(cb);
                }
            };
            Action showVersions = () =>
            {
                vlist.Children.Clear(); pickV = null; var it = from.SelectedItem as ComboBoxItem; pickDest = it == null ? null : (string)it.Tag;
                if (pickDest == null) { vlist.Children.Add(Muted(F.Dests.Count == 0 ? "No destinations yet." : "No destination can be reached right now (USB unplugged, NAS off?).")); showApps(); return; }
                var vs = Family.Versions(pickDest);
                if (vs.Count == 0) vlist.Children.Add(Muted("No versions there yet."));
                RadioButton first = null;
                foreach (var v in vs)
                {
                    var vv = v; var rb = new RadioButton { GroupName = "famver", Margin = new Thickness(0, 0, 0, 4), Foreground = (Brush)FindResource("Text"), Content = new TextBlock { Text = v.When.ToString("ddd d MMM yyyy, HH:mm") + (v.ThisPc ? "" : "  \u00B7  " + v.Pc) + "\n" + VaultSettings.Ago(v.When) + "  \u00B7  " + VaultSettings.Size(v.Bytes), TextWrapping = TextWrapping.Wrap } };
                    rb.Checked += (s, e) => { pickV = vv; result.Text = ""; showApps(); };
                    vlist.Children.Add(rb); if (first == null) first = rb;
                }
                if (first != null) first.IsChecked = true; else showApps();
            };
            from.SelectionChanged += (s, e) => showVersions();
            test.Click += (s, e) =>
            {
                if (pickV == null) return;
                var c = Family.Verify(pickV.File);
                result.Text = "Test restore of " + pickV.When.ToString("d MMM yyyy, HH:mm") + ": " + c.Summary + " Nothing was restored or changed.";
                result.Foreground = T(c.Ok ? "#6ED28C" : "#F07070");
                Log.Add("Hallow family", "test restore of " + System.IO.Path.GetFileName(pickV.File) + ": " + (c.Ok ? "fine, " + c.Files + " files" : c.Summary));
            };
            go.Click += (s, e) =>
            {
                var pick = boxes.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2).ToList(); if (pick.Count == 0 || pickV == null) return;
                var closing = pick.Where(g => g.Kind == "settings" && g.App != "Hallow").Select(g => "Hallow " + g.App).ToList();
                if (MessageBox.Show(w, "Put back from " + pickV.When.ToString("dddd d MMM, HH:mm") + ":\n\n" + string.Join("\n", pick.Select(g => "  \u2022 " + g.Label)) + (closing.Count > 0 ? "\n\nIf they're running, " + string.Join(", ", closing) + (closing.Count == 1 ? " is" : " are") + " asked to quit first. The files they replace are kept." : ""), "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var zip = pickV.File; var keys = pick.Select(g => g.Key).ToList(); var dest = pickDest; var safety = VaultSettings.Dir;
                go.IsEnabled = test.IsEnabled = false; result.Foreground = (Brush)FindResource("Text"); result.Text = "Restoring... (closing apps first can take a few seconds)";
                new Thread(() =>
                {
                    Family.RestoreResult r = null; string err = null;
                    try { r = Family.Restore(zip, keys, safety, dest); } catch (Exception ex) { err = ex.Message; }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        test.IsEnabled = true; go.IsEnabled = true;
                        if (err != null) { result.Text = "Couldn't restore: " + err; result.Foreground = T("#F07070"); return; }
                        var lines = r.Done.Select(x => "\u2713 " + x).Concat(r.Skipped.Select(x => "\u2715 " + x)).Concat(r.Notes.Select(x => "! " + x)).ToList();
                        if (r.SafetyDir != null) lines.Add("The files that were replaced are kept in " + r.SafetyDir + ".");
                        result.Text = string.Join("\n", lines); result.Foreground = T(r.Skipped.Count > 0 || r.Notes.Count > 0 ? "#F0BE5A" : "#6ED28C");
                        Log.Add("Hallow family", "restored from " + System.IO.Path.GetFileName(zip) + ": " + string.Join("; ", r.Done.Concat(r.Skipped).Concat(r.Notes)));
                        FamilyTick(false, null);
                    }));
                }) { IsBackground = true }.Start();
            };
            if (from.Items.Count > 0) from.SelectedIndex = 0; else showVersions();
            if (Snap.On && boxes.Count > 0) { foreach (var bx in boxes.Where(x => x.Item1.IsEnabled).Take(3)) bx.Item1.IsChecked = true; go.IsEnabled = true; test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }   // --snap: ticked apps and a test restore
            w.Content = sp; return w;
        }
    }
}
