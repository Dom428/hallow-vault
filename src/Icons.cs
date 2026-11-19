// Hallow Vault - Font Awesome icons (path data in Icons.g.cs). Buttons get their icon from their label,
// so a label that changes ("Mute" -> "Muted") changes its icon too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HallowVault
{
    public static partial class Icons
    {
        static readonly Dictionary<string, Geometry> cache = new Dictionary<string, Geometry>();

        // exact label -> icon
        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Back up everything", "floppy-disk" }, { "Back up", "floppy-disk" }, { "Restore", "clock-rotate-left" }, { "Restore this", "clock-rotate-left" },
            { "Open folder", "folder-open" }, { "Open the vault", "folder-open" }, { "Add folder", "plus" }, { "Turn off", "power-off" }, { "Turn on", "power-off" },
            { "Refresh", "arrows-rotate" }, { "Settings", "gear" }, { "Change...", "pen" }, { "Cancel", "xmark" }, { "x", "xmark" }, { "OK", "check" },
        };
        static readonly string[][] Prefixes = { };
        public static string ForLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            string n; if (Labels.TryGetValue(label.Trim(), out n)) return n;
            foreach (var p in Prefixes) if (label.StartsWith(p[0], StringComparison.OrdinalIgnoreCase)) return p[1];
            return null;
        }

        public static Geometry Get(string name)
        {
            if (name == null) return null;
            Geometry g;
            if (!cache.TryGetValue(name, out g))
            {
                string d; if (!Data.TryGetValue(name, out d)) return null;
                g = Geometry.Parse(d); g.Freeze(); cache[name] = g;
            }
            return g;
        }

        // A standalone icon (strip headers, the header bar).
        public static FrameworkElement Make(string name, double size, Brush fill)
        {
            return new Path { Data = Get(name), Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size, SnapsToDevicePixels = true };
        }
        // Icon + text for places that aren't styled buttons.
        public static FrameworkElement WithText(string name, string text, double size, Brush fill)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Make(name, size, fill); ic.Margin = new Thickness(0, 0, 7, 0); ic.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(ic); sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
    }

    // Used by the button styles in Theme.xaml.
    public class IconConv : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return Icons.Get(Icons.ForLabel(v as string)); }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    public class IconVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return Icons.ForLabel(v as string) != null ? Visibility.Visible : Visibility.Collapsed; }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    // Space between icon and text (none when the text is hidden).
    public class IconGap : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            var s = v as string;
            return s != null && s.Length > 1 ? new Thickness(0, 0, 7, 0) : new Thickness(0);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    // Hides the text of one-character labels that have an icon ("+", "x").
    public class LabelVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            var s = v as string;
            return s != null && s.Length == 1 && Icons.ForLabel(s) != null ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
}
