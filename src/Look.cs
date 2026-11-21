// Hallow Vault v1.14 - light and dark mode (Hallow family spec). %APPDATA%\Hallow\theme.json "mode": "dark" | "light" | "system"
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
        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2; h = 0; s = 0;
            if (max == min) return;
            var d = max - min; s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0); else if (max == g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
            h /= 6;
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
        // the accent for text, thin lines and icons on light backgrounds: same hue and saturation, darker until >= 4.5:1 on white
        public static string TextAccent(string accent)
        {
            var c = Parse(accent); double h, s, l; ToHsl(c, out h, out s, out l);
            var hex = Hex(c);
            while (Contrast(hex, "#FFFFFF") < 4.5 && l > 0) { l = Math.Max(0, l - 0.01); hex = Hex(FromHsl(h, s, l)); }
            return hex;
        }
        // text on a filled accent button: the family's dark text or white, whichever reads better
        public static string OnAccent(string accent) { return Contrast(accent, "#1E1A26") >= Contrast(accent, "#FFFFFF") ? "#1E1A26" : "#FFFFFF"; }

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
