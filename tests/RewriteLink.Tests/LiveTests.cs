using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using static Patodois.RewriteLink.Tests.T;
using Hdr = Patodois.RewriteLink.Wire.Header;

namespace Patodois.RewriteLink.Tests
{
    internal static class LiveTests
    {
        private static float[] Tri(int i) { return new float[] { i, 0, 0, i + 1, 0, 0, i, 0, -1 }; }

        private static float[] Tris(int n)
        {
            var t = new float[9 * n];
            for (int i = 0; i < n; i++) Array.Copy(Tri(i), 0, t, 9 * i, 9);
            return t;
        }

        public static void Run()
        {
            Test("handshake, state flow and a clean bye with half-close", () =>
            {
                using (var p = new Pair())
                {
                    PeerInfo gp = p.Guest.Peer, hp = p.Host.Peer;
                    Eq(gp.Game, "Test Host", "guest sees the host game"); Eq(hp.Game, "Test Guest", "host sees the guest game");
                    Eq(gp.Session, hp.Session, "same session"); Check(gp.Session != 0, "session is nonzero"); Eq(gp.Protocol, 1, "protocol 1");
                    Eq(gp.Role, Role.Host, "role"); Eq(hp.UdpPort, p.Guest.UdpPort, "udp port"); Eq(gp.UdpPort, p.Host.UdpPort, "udp port");
                    uint oldSession = hp.Session;

                    var cam = Wire.Camera.LookAt(new float[] { 1, 2, 3 }, new float[] { 1, 2, 0 }, 1f, .1f, 50f, 640, 360);
                    uint camSeq = p.Host.SendCamera(cam);
                    p.Guest.Spawn(9, "Test", "demo/cube", "Cube");
                    ulong t = Wire.NowUs();
                    var pose = Wire.Pose.Identity(9, t);
                    pose.Translation[0] = 42;
                    p.Guest.SendPoses(new[] { pose });
                    Check(Until(2000, () => { p.Pump(); return p.G<LinkEvent.Camera>() > 0 && p.H<LinkEvent.Poses>() > 0 && p.H<LinkEvent.Spawn>() > 0; }), "camera, spawn and pose arrive");
                    Eq(p.GE.OfType<LinkEvent.Camera>().First().Header.Seq, camSeq, "the camera's seq");
                    Eq(p.HE.OfType<LinkEvent.Poses>().First().Values[0].Translation[0], 42f, "pose x");
                    var sp = p.HE.OfType<LinkEvent.Spawn>().First().Value;
                    Check(sp.Entity == 9 && sp.Provider == "Test" && sp.Asset == "demo/cube" && sp.Name == "Cube", "spawn fields");

                    // a stale pose (older time) is dropped
                    int before = p.H<LinkEvent.Poses>();
                    long staleBefore = p.Host.Stale;
                    p.Guest.SendPoses(new[] { Wire.Pose.Identity(9, t - 1000) });
                    Check(Until(300, () => { p.Pump(); return p.Host.Stale > staleBefore; }), "stale counted");
                    Eq(p.H<LinkEvent.Poses>(), before, "stale pose dropped");

                    // focus: the guest asks, the host grants
                    p.Guest.SetFocus(Role.Guest);
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Focus>() > 0; }), "host sees the focus request");
                    Check(p.HE.OfType<LinkEvent.Focus>().First().Requested, "a request");
                    Eq(p.Host.Focus, Role.Host, "a request changes nothing by itself");
                    p.Host.SetFocus(Role.Guest);
                    Check(Until(2000, () => { p.Pump(); return p.G<LinkEvent.Focus>() > 0; }), "guest sees the grant");
                    Check(!p.GE.OfType<LinkEvent.Focus>().First().Requested, "a grant");
                    Eq(p.Guest.Focus, Role.Guest, "guest has the focus");

                    p.Guest.Despawn(9);
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Despawn>() == 1; }), "despawn");

                    // bye: the guest disconnects; the host sees "peer said bye", not a reset
                    p.Guest.Disconnect("test over");
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Lost>() > 0; }), "host notices the end");
                    Check(p.HE.OfType<LinkEvent.Lost>().First().Reason.Contains("peer said bye: test over"), "reason is the bye");
                    Eq(p.Host.Focus, Role.Host, "focus returns to the host");
                    Eq(p.G<LinkEvent.Lost>(), 0, "no event for the caller's own disconnect");

                    // the guest reconnects on its own with a new session
                    Check(Until(4000, () => { p.Pump(); return p.Host.IsConnected && p.Guest.IsConnected; }), "guest reconnects");
                    Check(p.Host.Peer.Session != oldSession, "new session after reconnect");
                }
            });

            Test("a second connection gets bye busy", () =>
            {
                using (var p = new Pair())
                using (var r = new Raw(p.Host.ControlPort))
                {
                    Eq(r.WaitLine(p.Host, 3000), "{\"type\":\"bye\",\"reason\":\"busy\"}", "busy bye line");
                    Check(r.WaitEof(p.Host, 2000), "then end of stream");
                    Check(!r.Reset, "a clean close, not a reset");
                    Check(p.Host.IsConnected, "the first session is untouched");
                }
            });

            Test("a hello with a wrong role, an incompatible protocol, garbage or no hello gets a bye", () =>
            {
                var cases = new[]
                {
                    new[] { "{\"type\":\"hello\",\"role\":\"host\",\"game\":\"x\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":5,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n", "role" },
                    new[] { "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"x\",\"protocol\":9,\"min_protocol\":2,\"udp_port\":5,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n", "protocol" },
                    new[] { "garbage\n", "protocol error" },
                    new[] { "{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\",\"asset\":\"a\"}\n", "expected hello" },
                    new[] { "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"x\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":0,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n", "udp_port" },
                };
                foreach (var c in cases)
                {
                    using (var host = Link.Host(new LinkConfig("Strict Host") { ControlPort = 0 }))
                    using (var r = new Raw(host.ControlPort))
                    {
                        r.Send(c[0]);
                        string line = r.WaitLine(host, 3000);
                        Check(line != null && line.StartsWith("{\"type\":\"bye\"") && line.Contains(c[1]), "a bye mentioning " + c[1] + " but got " + line);
                        Check(r.WaitEof(host, 2000), "then end of stream");
                    }
                }
            });

            Test("watchdog: a silent guest is lost after the timeout", () =>
            {
                using (var host = Link.Host(new LinkConfig("Watch Host") { ControlPort = 0, TimeoutMs = 400, HeartbeatMs = 100 }))
                using (var r = new Raw(host.ControlPort))
                {
                    // the guest says hello with a heartbeat of 50 ms (effective timeout max(400, 200)) and goes silent
                    r.Send(Raw.HelloLine("guest", 1, 1, 9, 50));
                    var events = new List<LinkEvent>();
                    long t0 = Environment.TickCount64;
                    Check(Until(3000, () => { host.Poll(events); r.Pump(); return events.OfType<LinkEvent.Lost>().Any(); }), "watchdog fires");
                    long ms = Environment.TickCount64 - t0;
                    Check(ms >= 350 && ms < 1500, "fires after about the timeout, took " + ms + " ms");
                    Check(events.OfType<LinkEvent.Lost>().First().Reason.StartsWith("watchdog"), "reason");
                    string hello = r.WaitLine(host, 1000);
                    Check(hello != null && hello.Contains("\"role\":\"host\"") && hello.Contains("\"session\":"), "host hello: " + hello);
                    string bye = r.WaitLine(host, 2000);
                    Check(bye != null && bye.Contains("\"type\":\"bye\"") && bye.Contains("watchdog"), "bye line: " + bye);
                }
            });

            Test("datagrams from another address are dropped", () =>
            {
                using (var p = new Pair())
                using (var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    byte[] b = Wire.Encode(new Hdr(1, 0, p.Host.Peer.Session, 1, Wire.NowUs()), new Wire.PoseMsg(new[] { Wire.Pose.Identity(1, Wire.NowUs()) }));
                    stranger.SendTo(b, new IPEndPoint(IPAddress.Loopback, p.Host.UdpPort));
                    Check(Until(500, () => { p.Pump(); return p.Host.Foreign >= 1; }), "counted as foreign");
                    Eq(p.H<LinkEvent.Poses>(), 0, "the stranger's pose is ignored");
                }
            });

            Test("only 127.0.0.1 and the peer's port is the peer", () =>
            {
                var peer = new IPEndPoint(IPAddress.Loopback, 5000);
                Check(Link.IsPeerAddress(new IPEndPoint(IPAddress.Loopback, 5000), peer), "the peer");
                Check(!Link.IsPeerAddress(new IPEndPoint(IPAddress.Loopback, 5001), peer), "another port");
                Check(!Link.IsPeerAddress(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 5000), peer), "another loopback address");
                Check(!Link.IsPeerAddress(new IPEndPoint(IPAddress.Parse("127.1.2.3"), 5000), peer), "127.1.2.3");
                Check(!Link.IsPeerAddress(new IPEndPoint(IPAddress.IPv6Loopback, 5000), peer), "::1");
                Check(!Link.IsPeerAddress(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 5000), peer), "10.0.0.1");
            });

            Test("bye is delivered and the close is clean while the closer has unread data (CONTRACT 6.1)", () =>
            {
                using (var host = Link.Host(new LinkConfig("H") { ControlPort = 0 }))
                using (var r = new Raw(host.ControlPort))
                {
                    r.Send(Raw.HelloLine("guest", 1, 1, r.UdpPort, 250));
                    Check(r.WaitLine(host, 2000) != null, "the host's hello");
                    for (int i = 0; i < 2000; i++) r.Send("{\"type\":\"future_thing\",\"n\":1}\n"); // ~64 KB the host has not read yet
                    host.Disconnect("done");
                    long t0 = Environment.TickCount64;
                    string line = null;
                    Check(Until(2000, () => { host.Poll(); r.Pump(); line = r.Line(); return line != null; }), "the bye arrives");
                    Eq(line, "{\"type\":\"bye\",\"reason\":\"done\"}", "bye line");
                    Check(Until(2000, () => { host.Poll(); r.Pump(); return r.Eof; }), "end of stream");
                    Check(!r.Reset, "an end of stream, not a connection reset");
                    r.Send("{\"type\":\"future_thing\"}\n"); // the peer keeps talking while the host drains
                    Until(400, () => { host.Poll(); r.Pump(); return false; });
                    Check(Environment.TickCount64 - t0 < 2000, "the drain is bounded");
                }
            });

            Test("Close returns within the linger even if the peer never closes", () =>
            {
                var host = Link.Host(new LinkConfig("H") { ControlPort = 0 });
                using (var r = new Raw(host.ControlPort))
                {
                    r.Send(Raw.HelloLine("guest", 1, 1, r.UdpPort, 250));
                    Check(r.WaitLine(host, 2000) != null, "the host's hello");
                    long t0 = Environment.TickCount64;
                    host.Close(); // the raw peer never closes its end
                    Check(Environment.TickCount64 - t0 <= 400, "Close took " + (Environment.TickCount64 - t0) + " ms");
                    r.Pump();
                    Check(r.AllText().Contains("\"reason\":\"closing\""), "the bye still arrived");
                    Check(r.Eof, "and then the end of the stream");
                }
            });

            // ------------------------------------------------------------------ revision 4

            Test("review M1: frames of a new pixel file are not stale", () =>
            {
                using (var p = new Pair())
                {
                    for (uint f = 1; f <= 10; f++) p.Guest.SendFrame(new Wire.FrameReady(f, f % 3, 0, 16, 8));
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Frame>() == 10; }), "10 frames of file 1");
                    long s0 = p.Host.Stale;
                    p.Guest.SendFrame(new Wire.FrameReady(5, 2, 0, 16, 8));
                    Check(Until(300, () => { p.Pump(); return p.Host.Stale > s0; }), "the same numbers again are stale");
                    p.HE.Clear();
                    p.Guest.AnnounceFrames(new Control.FrameChannel("/tmp/second.frames", 32, 16, 3));
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.FrameChannel>() == 1; }), "the announcement");
                    var fc = p.HE.OfType<LinkEvent.FrameChannel>().First().Value;
                    Check(fc.Path == "/tmp/second.frames" && fc.Width == 32 && fc.Height == 16 && fc.Slots == 3, "channel fields");
                    for (uint f = 1; f <= 5; f++) p.Guest.SendFrame(new Wire.FrameReady(f, f % 3, 0, 32, 16));
                    Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.Frame>() >= 5; }), "frames of the new file");
                    Eq(p.H<LinkEvent.Frame>(), 5, "frames 1..5 of the new channel");
                }
            });

            Test("review M2: a 400-chunk collision snapshot completes", () =>
            {
                using (var p = new Pair())
                {
                    int n = 400 * Wire.MaxTriangles;
                    float[] tris = Tris(n);
                    uint q = p.Host.SendCollision(new float[3], 16f, tris);
                    Array.Clear(tris, 0, tris.Length); // the link keeps its own copy
                    Check(Until(15000, () => { p.Pump(); return p.G<LinkEvent.Collision>() > 0; }), "the snapshot never completed");
                    var c = p.GE.OfType<LinkEvent.Collision>().First();
                    Eq(c.Query, q, "query"); Eq(c.TriangleCount, n, "triangles"); Eq(c.Triangles.Length, 9 * n, "floats");
                    Eq(c.Triangles[9 * 12345 + 3], 12346f, "a triangle from the middle"); Eq(c.Radius, 16f, "radius");
                }
            });

            Test("review M2: collision chunks are paced and a newer snapshot replaces queued ones", () =>
            {
                using (var p = new Pair())
                {
                    float[] big = Tris(400 * Wire.MaxTriangles);
                    Thread.Sleep(100);
                    p.Host.Poll(p.HE);
                    long before = p.Host.DatagramsSent;
                    p.Host.SendCollision(new float[3], 16f, big);
                    Eq(p.Host.DatagramsSent, before, "queued, nothing on the wire yet");
                    p.Host.Poll(p.HE);
                    long sent = p.Host.DatagramsSent - before;
                    Check(sent >= 1 && sent <= 33, "one poll put " + sent + " datagrams on the wire");
                    // a second snapshot while the first still waits: the first's chunks are dropped
                    long before2 = p.Host.DatagramsSent;
                    uint q2 = p.Host.SendCollision(new float[] { 1, 1, 1 }, 8f, big, 2 * Wire.MaxTriangles);
                    Check(Until(5000, () => { p.Pump(); return p.G<LinkEvent.Collision>() > 0; }), "the replacing snapshot arrives");
                    Until(200, () => { p.Pump(); return false; });
                    Check(p.Host.DatagramsSent - before2 < 60, "the replaced snapshot's chunks were sent anyway: " + (p.Host.DatagramsSent - before2));
                    Eq(p.G<LinkEvent.Collision>(), 1, "one snapshot");
                    Eq(p.GE.OfType<LinkEvent.Collision>().First().Query, q2, "only the replacing query completes");
                }
            });

            Test("an empty collision snapshot is one chunk", () =>
            {
                using (var p = new Pair())
                {
                    uint q = p.Host.SendCollision(new float[3], 5f, new float[0]);
                    Check(Until(2000, () => { p.Pump(); return p.G<LinkEvent.Collision>() == 1; }), "arrives");
                    var c = p.GE.OfType<LinkEvent.Collision>().First();
                    Check(c.TriangleCount == 0 && c.Query == q, "empty");
                }
            });

            Test("sending without a peer is an error", () =>
            {
                using (var g = Link.Guest(new LinkConfig("lonely") { ControlPort = 1 }))
                {
                    Throws<NotConnectedException>(() => g.SendCollision(new float[3], 1f, new float[0]), "collision");
                    Throws<NotConnectedException>(() => g.SendCamera(Wire.Camera.LookAt(new float[] { 0, 0, 5 }, new float[3], 1f, .1f, 10f, 4, 4)), "camera");
                    Throws<NotConnectedException>(() => g.SendPoses(new[] { Wire.Pose.Identity(1, 1) }), "poses");
                    Throws<NotConnectedException>(() => g.SendFrame(new Wire.FrameReady(1, 0, 0, 1, 1)), "frame");
                    Throws<NotConnectedException>(() => g.Spawn(1, "p", "a", null), "spawn");
                    Throws<NotConnectedException>(() => g.SetFocus(Role.Guest), "focus");
                    Check(!g.IsConnected, "not connected");
                }
            });

            Test("review M6: poses of unspawned entities are ignored; at most 1024 live entities", () =>
            {
                using (var p = new Pair())
                {
                    p.Guest.Spawn(5, "P", "a", null);
                    ulong t = Wire.NowUs();
                    p.Guest.SendPoses(new[] { Wire.Pose.Identity(5, t), Wire.Pose.Identity(6, t) });
                    Check(Until(500, () => { p.Pump(); return PosesFor(p.HE, 5) == 1 && p.Host.UnknownEntity == 1; }), "pose and unknown entity");
                    Eq(PosesFor(p.HE, 5), 1, "the spawned entity"); Eq(PosesFor(p.HE, 6), 0, "never spawned");
                    p.HE.Clear();
                    for (uint e = 0; e < 1030; e++) if (e != 5) p.Guest.Spawn(e, "P", "a", null);
                    Check(Until(3000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() >= 1023 && p.Host.SpawnIgnored >= 6; }), "spawns");
                    Eq(p.H<LinkEvent.Spawn>(), 1023, "spawns up to the table size (entity 5 was already live)");
                    p.Guest.SendPoses(new[] { Wire.Pose.Identity(0, t + 5), Wire.Pose.Identity(1029, t + 5) });
                    Until(500, () => { p.Pump(); return PosesFor(p.HE, 0) == 1; });
                    Eq(PosesFor(p.HE, 0), 1, "a live entity"); Eq(PosesFor(p.HE, 1029), 0, "its spawn was ignored");
                    // a despawn frees a place; a live id can always be spawned again
                    p.HE.Clear();
                    p.Guest.Despawn(0); p.Guest.Spawn(1029, "P", "a", null); p.Guest.Spawn(1, "P", "b", null);
                    Check(Until(1000, () => { p.Pump(); return p.H<LinkEvent.Spawn>() >= 2; }), "respawns");
                    p.Guest.SendPoses(new[] { Wire.Pose.Identity(1029, t + 10) });
                    Until(500, () => { p.Pump(); return PosesFor(p.HE, 1029) == 1; });
                    Eq(PosesFor(p.HE, 1029), 1, "the freed place is used");
                }
            });

            Test("review M6: the unknown_entity and spawn_ignored counters", () =>
            {
                using (var p = new Pair())
                {
                    p.Guest.Spawn(5, "P", "a", null);
                    ulong t = Wire.NowUs();
                    p.Guest.SendPoses(new[] { Wire.Pose.Identity(5, t), Wire.Pose.Identity(6, t), Wire.Pose.Identity(7, t) });
                    for (uint e = 100; e < 100 + 1030; e++) p.Guest.Spawn(e, "P", "a", null);
                    Until(3000, () => { p.Pump(); return p.Host.SpawnIgnored >= 7; });
                    Eq(p.Host.UnknownEntity, 2L, "poses of 6 and 7"); Eq(p.Host.SpawnIgnored, 7L, "1 + 1030 spawns for 1024 places");
                }
            });

            Test("review L9: our own heartbeat is within what a hello may announce", () =>
            {
                using (var p = new Pair(null, g => g.HeartbeatMs = 1)) Eq(p.Host.Peer.HeartbeatMs, 10L, "the nearest legal value");
                using (var p = new Pair(null, g => g.HeartbeatMs = 3600000)) Eq(p.Host.Peer.HeartbeatMs, 10000L, "the nearest legal value");
            });

            Test("review L9: heartbeat_ms outside 10..10000 is a handshake error", () =>
            {
                foreach (long ms in new long[] { 0, 1, 9, 10001, 3600000L, 4294967295L })
                {
                    string reason = HelloWithHeartbeat(ms);
                    Check(reason != null && reason.Contains("heartbeat_ms"), "heartbeat_ms " + ms + ": " + reason);
                }
                foreach (long ms in new long[] { 10, 250, 10000 }) Eq(HelloWithHeartbeat(ms), null, "heartbeat_ms " + ms + " is fine");
            });

            Test("announce frames sends a frame_channel line, and what it names reads", () =>
            {
                using (var p = new Pair())
                {
                    string path = TempPath("announce.frames");
                    using (var w = new PixelFile.Writer(path, 32, 16, 3, true))
                    {
                        p.Guest.AnnounceFrames(w);
                        Check(Until(2000, () => { p.Pump(); return p.H<LinkEvent.FrameChannel>() == 1; }), "the host sees the announcement");
                        var c = p.HE.OfType<LinkEvent.FrameChannel>().First().Value;
                        Eq(c.Path, w.Path, "path"); Check(c.Width == 32 && c.Height == 16 && c.Slots == 3, "dimensions");
                        ulong f = w.Write(new byte[32 * 16 * 4], new float[32 * 16], 32, 16, 7, 99);
                        using (var r = PixelFile.Reader.Open(c.Path)) Check(r.Read(f, (int)(f % 3), true) != null, "the frame reads");
                    }
                }
            });

            Test("a snapshot above the receiver's limit is dropped, and a later one is not", () =>
            {
                using (var p = new Pair(null, g => g.MaxCollisionTriangles = 1000))
                {
                    p.Host.SendCollision(new float[3], 5f, Tris(2000));
                    Check(Until(500, () => { p.Pump(); return p.Guest.Malformed > 0; }), "counted as malformed");
                    Until(200, () => { p.Pump(); return false; });
                    Eq(p.G<LinkEvent.Collision>(), 0, "dropped");
                    p.Host.SendCollision(new float[3], 5f, Tris(500));
                    Check(Until(2000, () => { p.Pump(); return p.G<LinkEvent.Collision>() == 1; }), "the next one arrives");
                    Eq(p.GE.OfType<LinkEvent.Collision>().First().TriangleCount, 500, "triangles");
                }
            });

            Test("fuzz: a live host survives junk datagrams", () =>
            {
                using (var host = Link.Host(new LinkConfig("H") { ControlPort = 0, TimeoutMs = 20000 }))
                using (var r = new Raw(host.ControlPort))
                {
                    r.Send(Raw.HelloLine("guest", 1, 1, r.UdpPort, 250));
                    string line = r.WaitLine(host, 2000);
                    uint session = ((Control.Hello)Control.Parse(line)).Session.Value;
                    var hd = new Hdr(1, 0, session, 1, Wire.NowUs());
                    var valid = new[]
                    {
                        Wire.Encode(hd, new Wire.PoseMsg(new[] { Wire.Pose.Identity(1, 1), Wire.Pose.Identity(2, 2) })),
                        Wire.Encode(hd, new Wire.CameraMsg(Wire.Camera.LookAt(new float[] { 0, 0, 5 }, new float[3], 1f, .1f, 100f, 8, 8))),
                        Wire.Encode(hd, new Wire.FrameMsg(new Wire.FrameReady(3, 0, 0, 4, 4))),
                    };
                    r.Send("{\"type\":\"spawn\",\"entity\":1,\"provider\":\"P\",\"asset\":\"a\"}\n");
                    var rnd = new Random(11);
                    var ep = new IPEndPoint(IPAddress.Loopback, host.UdpPort);
                    for (int i = 0; i < 30000; i++)
                    {
                        byte[] buf;
                        if (rnd.Next(4) == 0) { buf = new byte[rnd.Next(600)]; rnd.NextBytes(buf); }
                        else
                        {
                            buf = (byte[])valid[rnd.Next(3)].Clone();
                            int flips = rnd.Next(3);
                            for (int k = 0; k < flips; k++) buf[rnd.Next(buf.Length)] = (byte)rnd.Next(256);
                            if (rnd.Next(6) == 0) Array.Resize(ref buf, rnd.Next(buf.Length + 1));
                            if (rnd.Next(10) == 0 && buf.Length >= 12) Array.Clear(buf, 8, 4); // a wrong session
                        }
                        try { r.Udp.SendTo(buf, ep); } catch (SocketException) { }
                        if (i % 40 == 0) { host.Poll(); r.Pump(); }
                    }
                    Until(500, () => { host.Poll(); return false; });
                    Console.WriteLine("     host after the fuzz: " + host.DatagramsReceived + " received, " + host.Malformed + " malformed, " + host.Foreign + " foreign, "
                        + host.Stale + " stale, " + host.UnknownEntity + " unknown entity");
                    Check(host.IsConnected, "the host is still connected");
                    Check(host.Malformed + host.Foreign > 1000, "junk was counted");
                }
            });
        }

        private static string HelloWithHeartbeat(long ms)
        {
            using (var host = Link.Host(new LinkConfig("H") { ControlPort = 0 }))
            using (var r = new Raw(host.ControlPort))
            {
                r.Send(Raw.HelloLine("guest", 1, 1, 9, ms));
                var ev = new List<LinkEvent>();
                Until(2000, () => { host.Poll(ev); return ev.Count > 0; });
                Check(ev.Count == 1, "one event: " + string.Join(",", ev.Select(e => e.Kind)));
                var hf = ev[0] as LinkEvent.HandshakeFailed;
                if (hf != null) return hf.Reason;
                Check(ev[0] is LinkEvent.Connected, "connected");
                return null;
            }
        }

        private static int PosesFor(List<LinkEvent> events, uint entity)
        {
            return events.OfType<LinkEvent.Poses>().SelectMany(e => e.Values).Count(p => p.Entity == entity);
        }
    }
}
