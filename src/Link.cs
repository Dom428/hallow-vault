// Hallow Vault - the Hallow family link: a named pipe other Hallow apps (Hub, Remote) send one-line commands to,
// "HallowVault.exe --cmd" for scripts, and %APPDATA%\HallowVault\status.json. Current user only, never on the network.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowVault
{
    public static class Link
    {
        public static string PipeName { get { return "HallowFamily.Vault." + Environment.UserName; } }
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // Runs the server on a background thread. handler gets one command line and returns the reply line.
        public static void Serve(Func<string, string> handler)
        {
            new Thread(() =>
            {
                while (true)
                {
                    NamedPipeServerStream pipe = null;
                    try
                    {
                        var sec = new PipeSecurity();
                        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec);
                        pipe.WaitForConnection();
                    }
                    catch { if (pipe != null) pipe.Dispose(); Thread.Sleep(2000); continue; }
                    var p = pipe;
                    ThreadPool.QueueUserWorkItem(_ => Answer(p, handler));   // the next client can connect straight away
                }
            }) { IsBackground = true, Name = "HallowFamily link" }.Start();
        }

        static void Answer(NamedPipeServerStream pipe, Func<string, string> handler)
        {
            try
            {
                using (pipe)
                {
                    var line = ReadLine(pipe, 3000);
                    string reply;
                    if (line == null) return;
                    try { reply = handler(line.Trim()) ?? "error no reply"; } catch (Exception ex) { reply = "error " + ex.Message; }
                    var b = Utf8.GetBytes(reply.Replace("\r", " ").Replace("\n", " ") + "\n");
                    pipe.Write(b, 0, b.Length); pipe.Flush();
                    try { pipe.WaitForPipeDrain(); } catch { }
                }
            }
            catch { }
        }

        // reads one line (up to \n), giving up after `ms` so a silent client can't hold a thread
        static string ReadLine(Stream s, int ms)
        {
            var buf = new MemoryStream(); var one = new byte[256]; var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (buf.Length < 65536)
            {
                var left = (int)(until - DateTime.UtcNow).TotalMilliseconds; if (left <= 0) return null;
                var ar = s.BeginRead(one, 0, one.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(left)) return null;
                int n = s.EndRead(ar); if (n <= 0) break;
                int nl = Array.IndexOf(one, (byte)'\n', 0, n);
                if (nl >= 0) { buf.Write(one, 0, nl); break; }
                buf.Write(one, 0, n);
            }
            return Utf8.GetString(buf.ToArray()).TrimEnd('\r');
        }

        // Sends one line to the running Hallow Vault. 0 = ok, 1 = error reply, 2 = not running.
        public static int Send(string line, out string reply)
        {
            reply = null;
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    try { c.Connect(1500); } catch (TimeoutException) { return 2; } catch (IOException) { return 2; }
                    var b = Utf8.GetBytes(line.Replace("\r", " ").Replace("\n", " ") + "\n");
                    c.Write(b, 0, b.Length); c.Flush();
                    reply = ReadLine(c, 10000);
                    if (reply == null) { reply = "error no reply"; return 1; }
                    return reply == "ok" || reply.StartsWith("ok ") ? 0 : 1;
                }
            }
            catch (Exception ex) { reply = "error " + ex.Message; return 1; }
        }

        // "HallowVault.exe --cmd <line>": prints the reply (the exe is a window app, so attach to the caller's console if it has one)
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
        public static int Command(string line)
        {
            var h = GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) AttachConsole(-1);
            string reply; var code = Send(line, out reply);
            try
            {
                var o = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true };
                o.WriteLine(code == 2 ? "error Hallow Vault isn't running" : reply);
            }
            catch { }
            return code;
        }

        // ---- status.json, written atomically (status.json.tmp, then replace)
        static readonly object fileLock = new object();
        public static void WriteStatus(Dictionary<string, object> status)
        {
            if (Snap.On) return;
            lock (fileLock)
                try
                {
                    Directory.CreateDirectory(VaultSettings.Dir);
                    var file = Path.Combine(VaultSettings.Dir, "status.json"); var tmp = file + ".tmp";
                    File.WriteAllText(tmp, Json(status), Utf8);
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                }
                catch { }
        }
        public static string Json(object o) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o); }
        public static string Time(DateTime? t) { return t == null ? null : t.Value.ToString("yyyy-MM-dd'T'HH:mm:ss"); }
    }
}
