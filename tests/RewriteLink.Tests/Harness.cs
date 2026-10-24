using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Patodois.RewriteLink.Tests
{
    /// <summary>A plain test runner (no dependencies): exits 1 if any test fails.</summary>
    internal static class T
    {
        public static int Passed, Failed;

        public static void Test(string name, Action body)
        {
            try
            {
                body();
                Passed++;
                Console.WriteLine("ok   " + name);
            }
            catch (Exception e)
            {
                Failed++;
                Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message);
                Console.WriteLine(e.StackTrace);
            }
            Console.Out.Flush();
        }

        public static void Check(bool cond, string what)
        {
            if (!cond) throw new Exception("check failed: " + what);
        }

        public static void Eq<TV>(TV actual, TV expected, string what)
        {
            if (!EqualityComparer<TV>.Default.Equals(actual, expected)) throw new Exception(what + ": expected " + expected + " but was " + actual);
        }

        public static void Throws<TE>(Action a, string what) where TE : Exception
        {
            try { a(); }
            catch (TE) { return; }
            throw new Exception("expected " + typeof(TE).Name + ": " + what);
        }

        public static bool Until(int ms, Func<bool> cond)
        {
            long end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                if (cond()) return true;
                Thread.Sleep(2);
            }
            return cond();
        }

        public static byte[] Hex(string s)
        {
            string h = s.Replace(" ", "");
            var o = new byte[h.Length / 2];
            for (int i = 0; i < o.Length; i++) o[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return o;
        }

        public static string TempPath(string name)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "link-cs-test-" + Environment.ProcessId);
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name);
        }

        public static void Cleanup()
        {
            try { System.IO.Directory.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "link-cs-test-" + Environment.ProcessId), true); } catch { }
        }
    }

    /// <summary>A connected host and guest in one process, with the events each has seen.</summary>
    internal sealed class Pair : IDisposable
    {
        public readonly Link Host, Guest;
        public readonly List<LinkEvent> HE = new List<LinkEvent>(), GE = new List<LinkEvent>();

        public Pair() : this(null, null) { }

        public Pair(Action<LinkConfig> hostCfg, Action<LinkConfig> guestCfg)
        {
            var hc = new LinkConfig("Test Host") { ControlPort = 0, TimeoutMs = 4000 };
            hostCfg?.Invoke(hc);
            Host = Link.Host(hc);
            var gc = new LinkConfig("Test Guest") { ControlPort = Host.ControlPort, TimeoutMs = 4000 };
            guestCfg?.Invoke(gc);
            Guest = Link.Guest(gc);
            T.Check(T.Until(3000, () => { Pump(); return Host.IsConnected && Guest.IsConnected; }), "both sides connect");
        }

        public void Pump() { Host.Poll(HE); Guest.Poll(GE); }
        public int H<TE>() where TE : LinkEvent { return HE.OfType<TE>().Count(); }
        public int G<TE>() where TE : LinkEvent { return GE.OfType<TE>().Count(); }
        public void Dispose() { Guest.Dispose(); Host.Dispose(); }
    }

    /// <summary>A raw peer: plain sockets, to say things a correct implementation never would.</summary>
    internal sealed class Raw : IDisposable
    {
        public readonly Socket Tcp, Udp;
        public readonly int UdpPort;
        private readonly List<byte> _buf = new List<byte>();
        public bool Eof, Reset;

        public Raw(int hostPort)
        {
            Udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            UdpPort = ((IPEndPoint)Udp.LocalEndPoint).Port;
            Tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            Tcp.Connect(new IPEndPoint(IPAddress.Loopback, hostPort));
            Tcp.Blocking = false;
        }

        public void Send(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            int off = 0;
            long end = Environment.TickCount64 + 2000;
            while (off < b.Length && Environment.TickCount64 < end)
            {
                SocketError err;
                int n = Tcp.Send(b, off, b.Length - off, SocketFlags.None, out err);
                if (n > 0) off += n;
                else if (err != SocketError.WouldBlock && err != SocketError.Success) return;
                else Thread.Sleep(1);
            }
        }

        public void Pump()
        {
            var tmp = new byte[4096];
            while (true)
            {
                SocketError err;
                int n = Tcp.Receive(tmp, 0, tmp.Length, SocketFlags.None, out err);
                if (n > 0) { for (int i = 0; i < n; i++) _buf.Add(tmp[i]); continue; }
                if (err == SocketError.WouldBlock) return;
                Eof = true;
                if (err != SocketError.Success) Reset = true;
                return;
            }
        }

        /// <summary>Pops a complete line (without the newline), or null.</summary>
        public string Line()
        {
            int nl = _buf.IndexOf((byte)'\n');
            if (nl < 0) return null;
            string s = Encoding.UTF8.GetString(_buf.GetRange(0, nl).ToArray());
            _buf.RemoveRange(0, nl + 1);
            return s;
        }

        public string AllText() { return Encoding.UTF8.GetString(_buf.ToArray()); }

        /// <summary>Polls <paramref name="host"/> until the raw peer has a line, or ms pass.</summary>
        public string WaitLine(Link host, int ms)
        {
            string line = null;
            T.Until(ms, () => { host.Poll(); Pump(); line = Line(); return line != null; });
            return line;
        }

        public bool WaitEof(Link host, int ms) { return T.Until(ms, () => { host.Poll(); Pump(); return Eof; }); }

        public static string HelloLine(string role, int protocol, int minProtocol, int udpPort, long heartbeatMs)
        {
            return "{\"type\":\"hello\",\"role\":\"" + role + "\",\"game\":\"raw\",\"protocol\":" + protocol + ",\"min_protocol\":" + minProtocol
                + ",\"udp_port\":" + udpPort + ",\"heartbeat_ms\":" + heartbeatMs + ",\"timeout_ms\":2000}\n";
        }

        public void Dispose()
        {
            Tcp.Close();
            Udp.Close();
        }
    }
}
