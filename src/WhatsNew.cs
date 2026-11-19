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
