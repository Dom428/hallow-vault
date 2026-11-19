// Hallow Vault v1.8 - games that keep saves or settings in the registry (HKCU\Software\<developer>\<game>, like Unity's PlayerPrefs).
// Backing up only reads. A backup holds the key as a standard .reg file; restoring writes values only inside that same key.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace HallowVault
{
    public static class Reg
    {
        public const string Prefix = @"HKCU\";
        public static bool Is(string root) { return root != null && root.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase); }
        static string Sub(string root) { return root.Substring(Prefix.Length).TrimEnd('\\'); }
        const int MaxValues = 5000;

        public static bool Exists(string root)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Sub(root))) return k != null; } catch { return false; }
        }

        // the key and everything under it as .reg text ("Windows Registry Editor Version 5.00"); null if it's gone or too big
        public static string Export(string root)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Sub(root)))
                {
                    if (k == null) return null;
                    var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n"); int count = 0;
                    if (!Write(k, @"HKEY_CURRENT_USER\" + Sub(root), sb, ref count)) return null;
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
        static bool Write(RegistryKey k, string name, StringBuilder sb, ref int count)
        {
            sb.Append("\r\n[").Append(name).Append("]\r\n");
            foreach (var v in k.GetValueNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > MaxValues) return false;
                sb.Append(v.Length == 0 ? "@" : "\"" + Esc(v) + "\"").Append('=');
                var kind = k.GetValueKind(v); var o = k.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                switch (kind)
                {
                    case RegistryValueKind.String: sb.Append('"').Append(Esc((string)o)).Append('"'); break;
                    case RegistryValueKind.DWord: sb.Append("dword:").Append(unchecked((uint)(int)o).ToString("x8")); break;
                    case RegistryValueKind.QWord: sb.Append("hex(b):").Append(Hex(BitConverter.GetBytes((long)o))); break;
                    case RegistryValueKind.ExpandString: sb.Append("hex(2):").Append(Hex(Encoding.Unicode.GetBytes((string)o + "\0"))); break;
                    case RegistryValueKind.MultiString: sb.Append("hex(7):").Append(Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])o) + "\0\0"))); break;
                    default: sb.Append("hex:").Append(Hex(o as byte[] ?? new byte[0])); break;
                }
                sb.Append("\r\n");
            }
            foreach (var s in k.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                using (var c = k.OpenSubKey(s)) if (c != null && !Write(c, name + "\\" + s, sb, ref count)) return false;
            return true;
        }
        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n"); }
        static string Hex(byte[] b) { return string.Join(",", b.Select(x => x.ToString("x2"))); }

        // Writes the values of a .reg text back - only into `root` and keys under it; anything else in the text is ignored.
        // Values that exist now but aren't in the backup are left alone (like files). Returns how many values were written.
        public static int Import(string root, string text)
        {
            var allowed = @"HKEY_CURRENT_USER\" + Sub(root);
            if (!Sub(root).StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only keys under HKCU\\Software are restored");
            int n = 0; RegistryKey key = null;
            try
            {
                foreach (var line in Lines(text))
                {
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        if (key != null) { key.Dispose(); key = null; }
                        var name = line.Substring(1, line.Length - 2);
                        if (name.StartsWith("-")) continue;   // never delete keys
                        if (!(name.Equals(allowed, StringComparison.OrdinalIgnoreCase) || name.StartsWith(allowed + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                        key = Registry.CurrentUser.CreateSubKey(name.Substring(@"HKEY_CURRENT_USER\".Length));
                        continue;
                    }
                    if (key == null) continue;
                    string vname; int i;
                    if (line.StartsWith("@=")) { vname = ""; i = 2; }
                    else if (line.StartsWith("\"")) { i = 1; vname = Str(line, ref i); if (i >= line.Length || line[i] != '=') continue; i++; }
                    else continue;
                    var data = line.Substring(i);
                    if (data == "-") continue;   // never delete values
                    if (data.StartsWith("\"")) { int j = 1; key.SetValue(vname, Str(data, ref j), RegistryValueKind.String); }
                    else if (data.StartsWith("dword:")) key.SetValue(vname, unchecked((int)uint.Parse(data.Substring(6), NumberStyles.HexNumber)), RegistryValueKind.DWord);
                    else if (data.StartsWith("hex"))
                    {
                        var colon = data.IndexOf(':'); var type = data.Substring(0, colon); var bytes = Bytes(data.Substring(colon + 1));
                        if (type == "hex(b)") key.SetValue(vname, BitConverter.ToInt64(Pad(bytes, 8), 0), RegistryValueKind.QWord);
                        else if (type == "hex(2)") key.SetValue(vname, Encoding.Unicode.GetString(bytes).TrimEnd('\0'), RegistryValueKind.ExpandString);
                        else if (type == "hex(7)") key.SetValue(vname, Encoding.Unicode.GetString(bytes).TrimEnd('\0').Split('\0'), RegistryValueKind.MultiString);
                        else if (type == "hex(4)") key.SetValue(vname, BitConverter.ToInt32(Pad(bytes, 4), 0), RegistryValueKind.DWord);
                        else if (type == "hex") key.SetValue(vname, bytes, RegistryValueKind.Binary);
                        else continue;
                    }
                    else continue;
                    n++;
                }
            }
            finally { if (key != null) key.Dispose(); }
            return n;
        }
        // joins "\" line continuations (reg.exe wraps long hex lines)
        static IEnumerable<string> Lines(string text)
        {
            var cur = new StringBuilder();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var l = raw.TrimEnd();
                if (cur.Length > 0) l = l.TrimStart();
                if (l.EndsWith("\\") && !l.StartsWith("[") && !l.EndsWith("\\\\\"")) { cur.Append(l, 0, l.Length - 1); continue; }
                cur.Append(l); var done = cur.ToString(); cur.Clear();
                if (done.Length > 0) yield return done;
            }
        }
        static string Str(string s, ref int i)   // a quoted string starting after its opening quote; leaves i after the closing quote
        {
            var sb = new StringBuilder();
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] == 'n' ? '\n' : s[i]); }
                else if (c == '"') { i++; break; }
                else sb.Append(c);
            }
            return sb.ToString();
        }
        static byte[] Bytes(string s) { return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => byte.Parse(x.Trim(), NumberStyles.HexNumber)).ToArray(); }
        static byte[] Pad(byte[] b, int n) { var r = new byte[Math.Max(n, b.Length)]; Array.Copy(b, r, b.Length); return r; }
    }
}
