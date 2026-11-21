// Hallow Vault - "What's new": shown once after an update, with the versions you skipped.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HallowVault
{
    public static class WhatsNew
    {
        // Newest first. One or two short lines per version.
        public static readonly Tuple<string, string[]>[] Notes = {
            Tuple.Create("1.13", new[] { "Second copy to any folder - now also a NAS share you type in (\\\\NAS\\share). Every copy is read back and checked (SHA-256), it can keep its own number of versions, and Verify copies checks them all again", "Restore > Preview: what changed since a backup (sizes and times), and Restore file puts back just one file - your saves are backed up first", "Finds saves in more common places (My Games, Saved Games, LocalLow) and shows which games Steam Cloud already syncs" }),
            Tuple.Create("1.12", new[] { "Check backups (the heart button, or Settings): every zip must open and every file must match its checksum - green, amber or red per game. It only reads; a weekly check is in Settings (off by default)", "Add a game: back up a game no launcher knows - give it a name, its save folders and (optionally) its .exe. Edit or remove it any time; removing never deletes its backups" }),
            Tuple.Create("1.11", new[] { "Saves for 19,000+ games: Vault uses Ludusavi's save database (built from PCGamingWiki), downloaded from GitHub and checked weekly", "Settings > Online save database to turn it off; Vault's own detection still covers games it doesn't know" }),
            Tuple.Create("1.10", new[] { "The second copy shows whether it's up to date, behind or out of reach (USB unplugged) - and catches up by itself", "Spots sync conflict copies your cloud app made in the second copy" }),
            Tuple.Create("1.9", new[] { "Settings: also back up every few hours, and pick the compression (smallest or faster)", "Right-click a game > Files to back up: include or leave out files with patterns like *.sav", "Settings > Backup log: what was backed up, skipped, refused or failed" }),
            Tuple.Create("1.8", new[] { "Updates itself from GitHub (asks first) - Settings > Check for updates", "Finds more saves: games that keep settings in the registry (like many Unity games), and folders named after the game's developer" }),
            Tuple.Create("1.7", new[] { "Pin a version (Restore > pin) so it's never pruned - before a big boss, a finished run", "Save alarm: saves that suddenly shrink or vanish aren't backed up over - Vault tells you and offers the last good one", "Moving PC? Settings > Export, then Import on the new PC puts every game's newest saves back" }),
            Tuple.Create("1.6", new[] { "Uses the Hallow accent colour you pick in Hallow Toolkit (Settings to turn it off)", "If Vault ever closes unexpectedly, it keeps a crash report on this PC and tells you next time - nothing is sent anywhere" }),
            Tuple.Create("1.5", new[] { "One Hallow icon: with Hallow Hub running, Vault lives in Hub's tray menu (Back up everything, Restore, Open the vault folder) instead of its own icon", "Settings: Use the Hallow icon (on by default)" }),
            Tuple.Create("1.4", new[] { "Hallow Hub's game pages show each game's Saves: last backup, Back up now, Restore", "Hallow Remote: Back up now from your phone" }),
            Tuple.Create("1.3", new[] { "Second copy (Settings): every backup is copied to a OneDrive, Google Drive or Dropbox folder, or a USB drive", "Size limits per game and for the whole vault - the oldest versions go first, never a game's newest" }),
            Tuple.Create("1.2", new[] { "Restore > Compare: see which files were added, removed, got bigger or smaller before you restore", "Backs up a Steam game right before it updates, and never while a game is still writing its saves" }),
            Tuple.Create("1.1", new[] { "Finds Delta Force's settings and saves", "Hallow Hub and Hallow Remote can see your backups and start one (Hallow family link)" }),
        };

        static int Cmp(string a, string b) { Version x, y; return Version.TryParse(a, out x) && Version.TryParse(b, out y) ? x.CompareTo(y) : string.CompareOrdinal(a, b); }

        // The versions newer than `seen` (at most 6). Empty for a fresh install or no update.
        public static List<Tuple<string, string[]>> Since(string seen)
        {
            if (string.IsNullOrEmpty(seen)) return new List<Tuple<string, string[]>>();
            return Notes.Where(n => Cmp(n.Item1, seen) > 0 && Cmp(n.Item1, Program.Version) <= 0).Take(6).ToList();
        }
    }

    public class WhatsNewWindow : Window
    {
        static SolidColorBrush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        public WhatsNewWindow(List<Tuple<string, string[]>> items)
        {
            Title = "What's new in Hallow Vault"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            Background = (Brush)FindResource("Bg"); Foreground = (Brush)FindResource("Text");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            var sp = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
            sp.Children.Add(new TextBlock { Text = "What's new in Hallow Vault " + Program.Version, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Accent.Brush(), Margin = new Thickness(0, 0, 0, 12) });
            foreach (var v in items)
            {
                sp.Children.Add(new TextBlock { Text = v.Item1, FontWeight = FontWeights.Bold, Foreground = B("#A98BE0"), Margin = new Thickness(0, 6, 0, 3) });
                foreach (var line in v.Item2)
                {
                    var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var dot = new TextBlock { Text = "•", Foreground = Accent.Brush(), Width = 16, FontWeight = FontWeights.Bold }; DockPanel.SetDock(dot, Dock.Left);
                    d.Children.Add(dot); d.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(d);
                }
            }
            var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var all = new Button { Content = "All release notes", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
            all.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("https://github.com/Dom428/hallow-vault/releases") { UseShellExecute = true }); } catch { } };
            var ok = new Button { Content = "Got it", Style = (Style)FindResource("Accent"), MinWidth = 90 };
            ok.Click += (s, e) => Close();
            foot.Children.Add(all); foot.Children.Add(ok); sp.Children.Add(foot);
            Content = sp;
        }
    }
}
