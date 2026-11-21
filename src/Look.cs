// Hallow Vault v1.14 - light and dark mode (Hallow family spec; v1.15: readability fixes, open windows recoloured live). %APPDATA%\Hallow\theme.json "mode": "dark" | "light" | "system"
// (written by Hallow Toolkit; missing / unknown / broken = dark), "system" = Windows' AppsUseLightTheme. Vault's own Settings > Look
// (Follow Hallow / Light / Dark) wins over the family mode. Dark is the look Vault always had; light swaps the palette values only.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HallowVault
{
    public static class Look
    {
        public static bool Light;   // the look in use right now

        // ---- the switch
        static string Norm(string s) { return (s ?? "").Trim().ToLowerInvariant(); }
        // "dark" / "light" / "system" from theme.json's text; anything else (no file, broken JSON, no or an unknown mode) = "dark"
        public static string ParseMode(string json)
        {
            if (string.IsNullOrEmpty(json)) return "dark";
            try
            {
                var o = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json.Trim().TrimStart('﻿')) as Dictionary<string, object>;
                object mv; if (o == null || !o.TryGetValue("mode", out mv)) return "dark";
                var v = Norm(mv as string);
                return v == "light" || v == "system" ? v : "dark";
            }
            catch { return "dark"; }
        }
        public static string FamilyMode() { return ParseMode(Accent.ReadTheme()); }
        // Windows' app mode: true = light, false = dark, null = not set
        public static bool? SystemLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 1;
                    return null;
                }
            }
            catch { return null; }
        }
        // override: "hallow" (or anything else) = follow the family; "light" / "dark" win. Returns "light" or "dark".
        public static string Resolve(string over, string familyMode, Func<bool?> systemLight)
        {
            var o = Norm(over); if (o == "light" || o == "dark") return o;
            var f = Norm(familyMode);
            if (f == "light") return "light";
            if (f == "system") { bool? v = null; try { v = systemLight == null ? null : systemLight(); } catch { } return v == true ? "light" : "dark"; }
            return "dark";
        }
        public static bool Want(string over) { return Resolve(over, FamilyMode(), SystemLight) == "light"; }

        // ---- the palettes: today's dark values -> the family light values (resource keys stay the same)
        public static readonly Dictionary<string, string> LightOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "#16121C", "#F6F4FA" },   // Bg
            { "#211B2A", "#FFFFFF" },   // Panel
            { "#2E2639", "#EEEAF5" },   // PanelHi
            { "#3A3248", "#DAD3E6" },   // Line
            { "#ECE8F4", "#1E1A26" },   // Text
            { "#8F88A0", "#6B6478" },   // Muted
            { "#A98BE0", "#7A5CC0" },   // lilac
            { "#6ED28C", "#1E8E4F" },   // green
            { "#F0BE5A", "#A66A00" },   // amber
            { "#F07070", "#C2333F" },   // red
            { "#241E2E", "#FFFFFF" },   // menus, tooltips, combo popups, the settings panel
            { "#2A2333", "#FFFFFF" },   // buttons, combo boxes
            { "#1A1522", "#FFFFFF" },   // text fields
            { "#55496A", "#B9AED0" },   // button border on hover
            { "#C8414B", "#E4DCF2" },   // a pressed toggle (the Settings button while the panel is open): a lilac tint, not red
            { "#E05A64", "#7A5CC0" },
            { "#58566A", "#C9C2D6" },   // switch off: track
            { "#CDCAD6", "#FFFFFF" },   //             knob
        };
        // a colour in the look in use (dark values pass through in dark mode, and anything not in the palette always does)
        public static string Map(string hex) { string l; return Light && hex != null && LightOf.TryGetValue(hex, out l) ? l : hex; }

        // Theme.xaml's text with the light values (before the accent goes in)
        public static string LightXaml(string xaml)
        {
            xaml = Regex.Replace(xaml, "#[0-9A-Fa-f]{6}\\b", m => { string l; return LightOf.TryGetValue(m.Value, out l) ? l : m.Value; });
            return xaml.Replace("x:Name=\"hl\" Background=\"#FFFFFF\"", "x:Name=\"hl\" Background=\"#1E1A26\"");   // hover darkens on light
        }

        // ---- colour maths (WCAG)
        public static Color Parse(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        public static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
        static double Lin(byte v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        public static double Luminance(Color c) { return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B); }
        public static double Contrast(string a, string b)
        {
            double x = Luminance(Parse(a)), y = Luminance(Parse(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t; if (t < 0.5) return q; if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6; return p;
        }
        static Color FromHsl(double h, double s, double l)
        {
            double r, g, b;
            if (s == 0) r = g = b = l;
            else { var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q; r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3); }
            Func<double, byte> to = v => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));
            return Color.FromRgb(to(r), to(g), to(b));
        }
        // v1.15: the family rule (Hallow Toolkit 5.16's Get-LightTextAccent): the colour itself if it already has `min` contrast on `on`,
        // else HSL (System.Drawing's hue / saturation / brightness) with the lightness lowered 0.01 at a time until it does
        public static string Darken(string hex, double min, string on)
        {
            var c = Parse(hex); var x = Hex(c);
            if (Contrast(x, on) >= min) return x;
            var d = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            double h = d.GetHue() / 360.0, s = (double)d.GetSaturation(), l = (double)d.GetBrightness();
            while (l > 0)
            {
                l = Math.Max(0.0, l - 0.01); x = Hex(FromHsl(h, s, l));
                if (Contrast(x, on) >= min) break;
            }
            return x;
        }
        // the accent for text, thin lines and icons on light surfaces (v1.15: against Panel 2 #EEEAF5, the darkest one it sits on,
        // so it also reads on the background #F6F4FA and on white)
        public static string TextAccent(string accent) { return Darken(accent, 4.5, "#EEEAF5"); }
        // text on a filled accent: the family's dark text or white, whichever reads better
        public static string FillText(string fill) { return Contrast(fill, "#1E1A26") >= Contrast(fill, "#FFFFFF") ? "#1E1A26" : "#FFFFFF"; }
        public static string OnAccent(string accent) { return FillText(accent); }
        // v1.15: the light fill for accent buttons, chips and selected items: the accent itself if its text reaches 4.5:1,
        // else the accent darkened until white text does (violet #8B5CF6: white text was 4.23:1)
        public static string AccentFill(string accent) { return Contrast(accent, FillText(accent)) >= 4.5 ? Hex(Parse(accent)) : Darken(accent, 4.5, "#FFFFFF"); }
        // the fill on hover: 0.9 per channel (rounded like Toolkit's [int])
        public static string FillHover(string fill)
        {
            var c = Parse(fill); Func<byte, byte> dn = v => (byte)Math.Round(v * 0.9);
            return Hex(Color.FromRgb(dn(c.R), dn(c.G), dn(c.B)));
        }

        // ---- v1.15: status colours as small TEXT on light (the icon values are just under 4.5:1 on white)
        public static readonly Dictionary<string, string> LightText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "#6ED28C", "#1B7A44" },   // green
            { "#F0BE5A", "#8F5B00" },   // amber
        };
        public static string MapText(string hex) { string l; return Light && hex != null && LightText.TryGetValue(hex, out l) ? l : Map(hex); }

        // ---- v1.15: brushes that know what they are, so windows that are already open can be recoloured when the look or accent changes
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Brush, string> tags = new System.Runtime.CompilerServices.ConditionalWeakTable<Brush, string>();
        static SolidColorBrush Tag(string tag, string hex) { var b = new SolidColorBrush(Parse(hex)); tags.Add(b, tag); return b; }
        // a colour written with its dark value (fills, icons, panels)
        public static SolidColorBrush B(string darkHex) { return Tag("b" + darkHex, Map(darkHex)); }
        // the same as small text (darker green / amber on light)
        public static SolidColorBrush T(string darkHex) { return Tag("t" + darkHex, MapText(darkHex)); }
        // the accent for text and icons
        public static SolidColorBrush AccentText() { return Tag("a", Accent.CurrentLight ? TextAccent(Accent.Current) : Accent.Current); }
        static Brush Remake(string tag)
        {
            if (tag == "a") return AccentText();
            return tag[0] == 't' ? T(tag.Substring(1)) : B(tag.Substring(1));
        }
        // the brush for the look in use now, or null if this brush isn't one of ours
        public static Brush Again(Brush b, Dictionary<object, object> oldKeys, ResourceDictionary now)
        {
            if (b == null) return null;
            string tag; if (tags.TryGetValue(b, out tag)) return Remake(tag);
            object key; if (oldKeys != null && oldKeys.TryGetValue(b, out key) && now != null && now.Contains(key)) return now[key] as Brush;
            return null;
        }
        // recolours a window that is already open: its theme styles, theme brushes (FindResource) and our brushes, then the title bar
        public static int Restyle(Window w, ResourceDictionary old, ResourceDictionary now)
        {
            var oldKeys = new Dictionary<object, object>();
            if (old != null) foreach (var k in old.Keys) { var v = old[k]; if (v != null && !oldKeys.ContainsKey(v)) oldKeys[v] = k; }
            var seen = new HashSet<DependencyObject>(); int n = 0;
            Walk(w, oldKeys, now, seen, ref n);
            TitleBar(w);
            return n;
        }
        static void Walk(DependencyObject o, Dictionary<object, object> oldKeys, ResourceDictionary now, HashSet<DependencyObject> seen, ref int n)
        {
            if (o == null || !seen.Add(o)) return;
            var fe = o as FrameworkElement;
            object key;
            if (fe != null && fe.Style != null && oldKeys.TryGetValue(fe.Style, out key) && now.Contains(key) && now[key] is Style) { fe.Style = (Style)now[key]; n++; }
            var todo = new List<Tuple<DependencyProperty, Brush>>();
            var e = o.GetLocalValueEnumerator();
            while (e.MoveNext()) { var b = e.Current.Value as Brush; if (b != null) { var nb = Again(b, oldKeys, now); if (nb != null) todo.Add(Tuple.Create(e.Current.Property, nb)); } }
            foreach (var t in todo) { if (!t.Item1.ReadOnly) { o.SetValue(t.Item1, t.Item2); n++; } }
            foreach (var c in LogicalTreeHelper.GetChildren(o)) Walk(c as DependencyObject, oldKeys, now, seen, ref n);
            if (o is Visual || o is System.Windows.Media.Media3D.Visual3D)
            {
                int cnt = 0; try { cnt = VisualTreeHelper.GetChildrenCount(o); } catch { }
                for (int i = 0; i < cnt; i++) Walk(VisualTreeHelper.GetChild(o, i), oldKeys, now, seen, ref n);
            }
            if (fe != null)
            {
                if (fe.ContextMenu != null) Walk(fe.ContextMenu, oldKeys, now, seen, ref n);
                var tip = fe.ToolTip as DependencyObject; if (tip != null) Walk(tip, oldKeys, now, seen, ref n);
            }
        }

        // ---- Windows' title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static void TitleBar(Window w)
        {
            try { var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) return; int dark = Light ? 0 : 1; DwmSetWindowAttribute(h, 20, ref dark, 4); } catch { }
        }
        // every window: now if it has a handle, and when it gets one
        public static void Follow(Window w) { w.SourceInitialized += (s, e) => TitleBar(w); TitleBar(w); }
    }
}
