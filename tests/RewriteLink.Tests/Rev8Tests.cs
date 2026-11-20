using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using static Patodois.RewriteLink.Tests.T;
using Hdr = Patodois.RewriteLink.Wire.Header;

namespace Patodois.RewriteLink.Tests
{
    /// <summary>
    /// Revision 8 of the contract (section 12 lists its nine rules), the link's part: candidates, bounds per poll, the connected UDP socket, pose
    /// order, a side that was not running, error texts and peer text. The shared vectors cover what a raw peer can see; these cover the bounds and
    /// the state behind them and the sender's side of the pose order. The numbers are in ControlTests, the Windows paths and the pixel file's mode
    /// in PixelTests, the endpoint file's owner in TokenTests.
    /// </summary>
    internal static class Rev8Tests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IPEndPoint Lo0 = new IPEndPoint(IPAddress.Loopback, 0);

        private static int Count(Link link, string field) { return ((ICollection)typeof(Link).GetField(field, Private).GetValue(link)).Count; }
        private static long NowOf(Link link) { return (long)typeof(Link).GetMethod("Now", Private).Invoke(link, null); }
        private static void SetLong(Link link, string field, long v) { typeof(Link).GetField(field, Private).SetValue(link, v); }
        private static string StateOf(Link link) { return typeof(Link).GetField("_state", Private).GetValue(link).ToString(); }
        private static bool UdpConnected(Link link) { return (bool)typeof(Link).GetField("_udpConnected", Private).GetValue(link); }

        /// <summary>Moves the candidates' wait back to <paramref name="since"/>.</summary>
        private static void AgeCandidates(Link link, long since)
        {
            var since_ = typeof(Link).GetNestedType("Candidate", BindingFlags.NonPublic).GetField("Since");
            foreach (object c in (IEnumerable)typeof(Link).GetField("_candidates", Private).GetValue(link)) since_.SetValue(c, since);
        }

        private static Socket Connect(int port)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            s.Connect(new IPEndPoint(IPAddress.Loopback, port));
            s.Blocking = false;
            return s;
        }

        /// <summary>Everything the socket has right now, appended to <paramref name="got"/>; true at end of stream (or a reset).</summary>
        private static bool Drain(Socket s, List<byte> got)
        {
            var buf = new byte[1024];
            while (true)
            {
                SocketError err;
                int n = s.Receive(buf, 0, buf.Length, SocketFlags.None, out err);
                if (n > 0) { got.AddRange(buf.Take(n)); continue; }
                if (err == SocketError.WouldBlock || err == SocketError.Interrupted) return false;
                return true;
            }
        }

        private static Link NewHost(Action<LinkConfig> cfg = null)
        {
            var c = new LinkConfig("H") { ControlPort = 0, TimeoutMs = 30000 };
            cfg?.Invoke(c);
            return Link.Host(c);
        }

        /// <summary>A raw guest that has opened a session with <paramref name="host"/>; returns the host's hello.</summary>
        private static Control.Hello Session(Link host, Raw r)
        {
            r.Send(Raw.HelloLine("guest", 1, 1, r.UdpPort, 250));
            string line = r.WaitLine(host, 2000);
            Check(line != null && host.IsConnected, "the session opens: " + line);
            return (Control.Hello)Control.Parse(line);
        }

        private static byte[] Beat(uint session) { return Wire.Encode(new Hdr(1, 0, session, 0, 0), new Wire.HeartbeatMsg()); }

        public static void Run()
        {
            // ------------------------------------------------------------------ candidates and bounds (sections 2, 6.1)

            Test("revision 8: a poll accepts at most eight connections, holds four candidates and closes the ones they displace", () =>
            {
                using (var host = NewHost())
                {
                    var conns = new List<Socket>();
                    try
                    {
                        // All twenty are in the backlog before the poll: a loopback connect returns once the system has completed the handshake.
                        for (int i = 0; i < 20; i++) conns.Add(Connect(host.ControlPort));
                        host.Poll();
                        Eq(Count(host, "_candidates"), Link.MaxCandidates, "four held");
                        Eq(Count(host, "_candidates") + Count(host, "_closing"), Link.MaxAcceptsPerPoll, "eight accepted, four of them displaced");
                        host.Poll();
                        Eq(Count(host, "_candidates") + Count(host, "_closing"), 2 * Link.MaxAcceptsPerPoll, "eight more in the next poll");
                        // the oldest were told why, with a bye of their own reason, not busy
                        var got = new List<byte>();
                        Check(Until(2000, () => { host.Poll(); return Drain(conns[0], got); }), "the oldest is closed");
                        string text = Encoding.UTF8.GetString(got.ToArray());
                        Check(text.Contains("\"bye\"") && !text.Contains("busy"), text);
                    }
                    finally { foreach (Socket s in conns) s.Close(); }
                }
            });

            Test("revision 8: the first candidate with an acceptable hello becomes the session and the others are told busy", () =>
            {
                using (var host = NewHost())
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    udp.Bind(Lo0);
                    var conns = new List<Socket>();
                    try
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            conns.Add(Connect(host.ControlPort));
                            int want = Math.Min(i + 1, Link.MaxCandidates);
                            Check(Until(2000, () => { host.Poll(); return Count(host, "_candidates") == want; }), want + " held");
                        }
                        // the two oldest were displaced; the second oldest held now says hello
                        conns[3].Send(Encoding.UTF8.GetBytes(Raw.HelloLine("guest", 1, 1, ((IPEndPoint)udp.LocalEndPoint).Port, 250)));
                        Check(Until(2000, () => { host.Poll(); return host.IsConnected; }), "it becomes the session");
                        Eq(Count(host, "_candidates"), 0, "none held during the session");
                        for (int i = 0; i < 6; i++)
                        {
                            if (i == 3) continue;
                            var got = new List<byte>();
                            Check(Until(2000, () => { host.Poll(); return Drain(conns[i], got); }), "connection " + i + " is closed");
                            string text = Encoding.UTF8.GetString(got.ToArray());
                            if (i < 2) Check(text.Contains("\"bye\"") && !text.Contains("busy"), "connection " + i + " was displaced with a reason of its own: " + text);
                            else Check(text.Contains("\"busy\""), "connection " + i + " was told busy: " + text);
                        }
                        Check(host.IsConnected, "the session goes on");
                    }
                    finally { foreach (Socket s in conns) s.Close(); }
                }
            });

            Test("revision 8: a host holds at most sixteen closing sockets, and every connection during a session is told busy", () =>
            {
                using (var host = NewHost())
                using (var r = new Raw(host.ControlPort))
                {
                    Session(host, r);
                    const int Flood = 3 * Link.MaxClosing;
                    var flood = Enumerable.Range(0, Flood).Select(_ => Connect(host.ControlPort)).ToList();
                    var got = flood.Select(_ => new List<byte>()).ToList();
                    var ended = new bool[Flood];
                    try
                    {
                        long end = Environment.TickCount64 + 5000;
                        while (ended.Any(e => !e))
                        {
                            host.Poll();
                            Check(Count(host, "_closing") <= Link.MaxClosing, Count(host, "_closing") + " closing");
                            for (int i = 0; i < Flood; i++) if (!ended[i]) ended[i] = Drain(flood[i], got[i]);
                            Check(Environment.TickCount64 < end, ended.Count(e => !e) + " of " + Flood + " connections were never closed");
                            Thread.Sleep(1);
                        }
                        Check(host.IsConnected, "the session survives the flood");
                        // the bye is written before any drain begins, so even the ones closed at once got it
                        Check(got.All(g => Encoding.UTF8.GetString(g.ToArray()).Contains("\"busy\"")), "every one was told busy");
                    }
                    finally { foreach (Socket s in flood) s.Close(); }
                }
            });

            Test("revision 8: the datagrams one poll reads are bounded, 64 without a session and 1024 with one", () =>
            {
                using (var host = NewHost())
                using (var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    stranger.Bind(Lo0);
                    var to = new IPEndPoint(IPAddress.Loopback, host.UdpPort);
                    for (int i = 0; i < 100; i++) stranger.SendTo(new byte[24], to);
                    Thread.Sleep(200); // loopback delivers at once; a margin for a loaded machine
                    host.Poll();
                    Eq(host.Foreign, (long)Link.MaxDatagramsWithoutSession, "one poll without a session");
                    host.Poll();
                    Eq(host.Foreign, 100L, "the rest in the next poll");

                    using (var r = new Raw(host.ControlPort))
                    {
                        Session(host, r);
                        byte[] beat = Beat(host.Peer.Session);
                        var hostUdp = new IPEndPoint(IPAddress.Loopback, host.UdpPort);
                        int sent = 0;
                        try { while (sent < 1100) { r.Udp.SendTo(beat, hostUdp); sent++; } } catch (SocketException) { } // a full buffer may refuse
                        Thread.Sleep(200);
                        long before = host.DatagramsReceived;
                        host.Poll();
                        long first = host.DatagramsReceived - before;
                        for (int i = 0; i < 4; i++) host.Poll();
                        long all = host.DatagramsReceived - before;
                        // How many the system kept depends on its buffer (Linux grants less than asked for): the bound shows where more were waiting.
                        if (all > Link.MaxDatagramsPerPoll) Eq(first, (long)Link.MaxDatagramsPerPoll, all + " were waiting");
                        else Eq(first, all, "fewer than the bound were waiting, and one poll read them all");
                    }
                }
            });

            // ------------------------------------------------------------------ the connected UDP socket (section 2)

            Test("revision 8: the UDP socket is connected to the peer, and an automatic port is renewed for the next session", () =>
            {
                using (var host = NewHost())
                using (var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    stranger.Bind(Lo0);
                    int firstPort = host.UdpPort;
                    using (var r = new Raw(host.ControlPort))
                    {
                        Session(host, r);
                        Check(UdpConnected(host), "connected during the session");
                        byte[] beat = Beat(host.Peer.Session);
                        stranger.SendTo(beat, new IPEndPoint(IPAddress.Loopback, host.UdpPort));
                        r.Udp.SendTo(beat, new IPEndPoint(IPAddress.Loopback, host.UdpPort));
                        Check(Until(2000, () => { host.Poll(); return host.DatagramsReceived == 1; }), "the peer's datagram arrives");
                        Thread.Sleep(50);
                        host.Poll();
                        Eq(host.Foreign, 0L, "the stranger's datagram never reached the host: the system dropped it");
                        r.Send("{\"type\":\"bye\"}\n");
                        Check(Until(2000, () => { host.Poll(); return !host.IsConnected; }), "the session ends");
                    }
                    Check(!UdpConnected(host), "the next session's socket is not connected yet");
                    int secondPort = host.UdpPort;
                    Check(secondPort != firstPort, "a new automatic port: " + firstPort + " and " + secondPort);
                    using (var r = new Raw(host.ControlPort))
                    {
                        Eq((int)Session(host, r).UdpPort, secondPort, "the next hello announces the new port");
                        r.Udp.ReceiveTimeout = 2000;
                        var buf = new byte[64];
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        r.Udp.ReceiveFrom(buf, ref from);
                        Eq(((IPEndPoint)from).Port, secondPort, "and its heartbeats come from it");
                    }
                }
            });

            Test("revision 8: a fixed UDP port is the port of every session", () =>
            {
                int port;
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Bind(Lo0);
                    port = ((IPEndPoint)probe.LocalEndPoint).Port;
                }
                using (var host = NewHost(c => c.UdpPort = port))
                {
                    for (int round = 0; round < 3; round++)
                        using (var r = new Raw(host.ControlPort))
                        {
                            Eq((int)Session(host, r).UdpPort, port, "session " + round + " announces the fixed port");
                            Check(UdpConnected(host), "connected");
                            r.Udp.ReceiveTimeout = 2000;
                            var buf = new byte[64];
                            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            r.Udp.ReceiveFrom(buf, ref from);
                            Eq(((IPEndPoint)from).Port, port, "heartbeats come from it");
                            r.Send("{\"type\":\"bye\"}\n");
                            Check(Until(2000, () => { host.Poll(); return !host.IsConnected; }), "the session ends");
                            Eq(host.UdpPort, port, "the port stays");
                            Eq(host.LastError, null, "the port could be bound again");
                        }
                }
            });

            Test("revision 8: an unreachable peer port is a lost datagram, not the end of the session", () =>
            {
                int gone;
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { s.Bind(Lo0); gone = ((IPEndPoint)s.LocalEndPoint).Port; }
                using (var host = NewHost(c => c.HeartbeatMs = 10))
                using (var r = new Raw(host.ControlPort))
                {
                    r.Send(Raw.HelloLine("guest", 1, 1, gone, 250));
                    Check(Until(2000, () => { host.Poll(); return host.IsConnected; }), "a session whose peer port is closed");
                    var events = new List<LinkEvent>();
                    long end = Environment.TickCount64 + 500;
                    while (Environment.TickCount64 < end) { host.Poll(events); Thread.Sleep(2); } // fifty heartbeats into a closed port
                    Check(host.IsConnected && !events.OfType<LinkEvent.Lost>().Any(), "still live: " + string.Join(", ", events.Select(e => e.Kind)));
                    Check(host.DatagramsSent + host.SendDropped > 20, "heartbeats were sent: " + host.DatagramsSent + " sent, " + host.SendDropped + " dropped");
                }
            });

            // ------------------------------------------------------------------ pose order (sections 5, 7, 8, 9.4)

            Test("revision 8: a sender's pose times never go back; a teleport and a respawn start the order again on both sides", () =>
            {
                using (var p = new Pair())
                {
                    p.Guest.Spawn(3, "P", "a", null);
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() == 1; }), "spawn");
                    Func<Wire.Pose, ulong> send = pose =>
                    {
                        int before = p.H<LinkEvent.Poses>();
                        p.Guest.SendPoses(new[] { pose });
                        Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Poses>() > before; }), "the pose at " + pose.TimeUs + " arrives");
                        return p.HE.OfType<LinkEvent.Poses>().Last().Values.Single().TimeUs;
                    };
                    const ulong t = 1000000;
                    Eq(send(Wire.Pose.Identity(3, t)), t, "the first pose");
                    var older = Wire.Pose.Identity(3, t - 10);
                    Eq(send(older), t + 1, "an older one is sent with the last time plus one microsecond");
                    Eq(older.TimeUs, t - 10, "the caller's pose is not changed");
                    Eq(p.Guest.PoseTimeRaised, 1L, "and counted");
                    var teleport = Wire.Pose.Identity(3, t - 500);
                    teleport.Flags = Wire.PoseTeleport;
                    Eq(send(teleport), t - 500, "a teleport keeps its time, and the receiver uses it");
                    Eq(send(Wire.Pose.Identity(3, t - 500)), t - 500, "the order starts again at the teleport (an equal time is not older)");
                    Eq(p.Guest.PoseTimeRaised, 1L, "neither was raised");
                    p.Guest.Spawn(3, "P", "a", null);
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() == 2; }), "respawn");
                    Eq(send(Wire.Pose.Identity(3, 5)), 5UL, "after a respawn any time, on both sides");
                    Eq(send(Wire.Pose.Identity(3, ulong.MaxValue)), ulong.MaxValue, "the largest time");
                    Eq(send(Wire.Pose.Identity(3, 7)), ulong.MaxValue, "the largest u64 stays itself");
                    Eq(p.Guest.PoseTimeRaised, 2L, "raised again");
                    p.Guest.Despawn(3);
                    p.Guest.Spawn(3, "P", "a", null);
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() == 3; }), "despawn and spawn");
                    Eq(send(Wire.Pose.Identity(3, 1)), 1UL, "a despawn forgets the time too");
                }
            });

            Test("revision 8: a receiver drops a plain pose older than the last one used, after a teleport too", () =>
            {
                using (var host = NewHost())
                using (var r = new Raw(host.ControlPort))
                {
                    Session(host, r);
                    uint session = host.Peer.Session;
                    r.Send("{\"type\":\"spawn\",\"entity\":5,\"provider\":\"P\",\"asset\":\"a\"}\n");
                    var events = new List<LinkEvent>();
                    Check(Until(2000, () => { host.Poll(events); return events.OfType<LinkEvent.Spawn>().Any(); }), "spawn");
                    var to = new IPEndPoint(IPAddress.Loopback, host.UdpPort);
                    Func<ulong, uint, bool> used = (time, flags) =>
                    {
                        var pose = Wire.Pose.Identity(5, time);
                        pose.Flags = flags;
                        int before = events.OfType<LinkEvent.Poses>().Count();
                        long stale = host.Stale;
                        r.Udp.SendTo(Wire.Encode(new Hdr(1, 0, session, 1, 0), new Wire.PoseMsg(new[] { pose })), to);
                        Check(Until(2000, () => { host.Poll(events); return events.OfType<LinkEvent.Poses>().Count() > before || host.Stale > stale; }), "the pose is handled");
                        return events.OfType<LinkEvent.Poses>().Count() > before;
                    };
                    Check(used(1000, 0), "the first");
                    Check(!used(999, 0), "an older plain pose is dropped");
                    Check(used(500, Wire.PoseTeleport), "an older teleport is used");
                    Check(!used(499, 0), "and the order continues from it");
                    Check(used(500, 0), "an equal time is not older");
                }
            });

            // ------------------------------------------------------------------ a side that was not running (section 7)

            Test("revision 8: a pause of this side's own is not the peer's silence: watchdog, candidate and guest", () =>
            {
                // the watchdog: two seconds without a poll and without a word; the silence counts from this poll
                using (var host = NewHost(c => c.TimeoutMs = 400))
                using (var r = new Raw(host.ControlPort))
                {
                    Session(host, r); // the guest's heartbeat of 250 ms makes the timeout 1000 ms
                    long n = NowOf(host);
                    SetLong(host, "_lastPoll", n - 2000); SetLong(host, "_lastHeard", n - 2000);
                    host.Poll();
                    Check(host.IsConnected, "after a pause longer than the timeout the peer is not lost");
                    n = NowOf(host);
                    SetLong(host, "_lastPoll", n - 100); SetLong(host, "_lastHeard", n - 2000);
                    host.Poll();
                    Check(!host.IsConnected, "a gap shorter than the timeout counts as it always did");
                }
                // a host's wait for a candidate's first line
                using (var host = NewHost(c => c.TimeoutMs = 400))
                using (var silent = Connect(host.ControlPort))
                {
                    Check(Until(2000, () => { host.Poll(); return Count(host, "_candidates") == 1; }), "held");
                    long n = NowOf(host);
                    SetLong(host, "_lastPoll", n - 2000); AgeCandidates(host, n - 2000);
                    host.Poll();
                    Eq(Count(host, "_candidates"), 1, "still held after a pause");
                    n = NowOf(host);
                    SetLong(host, "_lastPoll", n - 100); AgeCandidates(host, n - 2000);
                    var events = host.Poll();
                    Eq(Count(host, "_candidates"), 0, "refused after its timeout");
                    Check(events.OfType<LinkEvent.HandshakeFailed>().Any(), "and reported");
                }
                // a guest's wait for the host's hello
                using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    listener.Bind(Lo0);
                    listener.Listen(4);
                    using (var guest = Link.Guest(new LinkConfig("G") { ControlPort = ((IPEndPoint)listener.LocalEndPoint).Port, TimeoutMs = 400 }))
                    {
                        Check(Until(2000, () => { guest.Poll(); return StateOf(guest) == "Handshake"; }), "waiting for the host's hello");
                        long n = NowOf(guest);
                        SetLong(guest, "_lastPoll", n - 2000); SetLong(guest, "_since", n - 2000);
                        guest.Poll();
                        Eq(StateOf(guest), "Handshake", "still waiting after a pause");
                        n = NowOf(guest);
                        SetLong(guest, "_lastPoll", n - 100); SetLong(guest, "_since", n - 2000);
                        guest.Poll();
                        Check(StateOf(guest) != "Handshake", "given up after its timeout");
                    }
                }
            });

            // ------------------------------------------------------------------ error texts and peer text (sections 6.2, 8)

            Test("revision 8: a host's handshake error texts quote nothing from the line", () =>
            {
                foreach (string[] c in new[]
                {
                    new[] { "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"Zq7x\",\"protocol\":4321,\"min_protocol\":4321,\"udp_port\":9,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n", "4321" },
                    new[] { "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"Zq7x\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":9,\"heartbeat_ms\":98765,\"timeout_ms\":2000}\n", "98765" },
                    new[] { "{\"type\":\"hello\",\"role\":\"host\",\"game\":\"Zq7x\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":9,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n", "host" },
                })
                    using (var host = NewHost())
                    using (var r = new Raw(host.ControlPort))
                    {
                        var events = new List<LinkEvent>();
                        r.Send(c[0]);
                        string line = null;
                        Check(Until(2000, () => { host.Poll(events); r.Pump(); line = r.Line(); return line != null; }), "a bye");
                        string reason = events.OfType<LinkEvent.HandshakeFailed>().Single().Reason;
                        foreach (string text in new[] { line, reason })
                            Check(!text.Contains(c[1]) && !text.Contains("Zq7x"), "the text quotes the line: " + text);
                    }
            });

            Test("revision 8: text from the peer reaches the application without control characters", () =>
            {
                Eq(Link.ForPeople("a\u001b[2Jb\nforged\r\u0007\u007f\u0000\u009f\u00e9"), "a [2Jb forged    \u009f\u00e9", "cleaned");
                Eq(Link.ForPeople("plain"), "plain", "nothing to clean");
                Eq(Link.PeerSaidBye("x\ny"), "peer said bye: x y", "a bye reason");
                using (var p = new Pair(h => h.Game = "Host\u001b]0;x\u0007\n", null))
                {
                    Eq(p.Guest.Peer.Game, "Host ]0;x  ", "the game of the hello");
                    p.Guest.Spawn(new Control.Spawn(4, "P\n", "a\n", "n\u001b[0m\r\nx"));
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() == 1; }), "spawn");
                    var sp = p.HE.OfType<LinkEvent.Spawn>().Single().Value;
                    Eq(sp.Name, "n [0m  x", "a spawn name");
                    Check(sp.Provider == "P\n" && sp.Asset == "a\n", "identifiers are handed on as sent");
                    p.Host.Disconnect("a\nb\u007f");
                    Check(Until(2000, () => { p.Pump(); return p.G<LinkEvent.Lost>() == 1; }), "the bye");
                    Eq(p.GE.OfType<LinkEvent.Lost>().Single().Reason, "peer said bye: a b ", "the bye's reason");
                }
            });
        }
    }
}
