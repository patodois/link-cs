// The C# peer of the cross-language interop session of the link (rewrite-bridge: tests/interop/README.md).
//
//   InteropPeer host|guest --port PORT --label LABEL
//
// Two processes, one host and one guest, run the same scripted session over real sockets and a real pixel file: handshake, spawn and
// focus, five lock-step frames (camera, poses, a collision snapshot, a frame with depth read back from the pixel file), despawn and bye.
// The peer prints log lines of its own and, as its last stdout line, "RBL-INTEROP-RESULT {json}". Exit status: 0 if every check passed, 1
// if not (or after 30 seconds, or when a step waits more than 10 seconds), 2 for a bad command line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Patodois.RewriteLink.InteropPeer
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            string role = args.Length > 0 ? args[0] : null;
            string label = null;
            int port = -1;
            bool bad = role != "host" && role != "guest";
            for (int i = 1; !bad && i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) { bad = true; break; }
                if (args[i] == "--port")
                {
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) bad = true;
                }
                else if (args[i] == "--label") label = args[i + 1];
                else bad = true;
            }
            if (bad || port < 0 || string.IsNullOrEmpty(label))
            {
                Console.Error.WriteLine("usage: InteropPeer host|guest --port PORT --label LABEL");
                return 2;
            }
            Peer peer = role == "host" ? (Peer)new HostPeer(port, label) : new GuestPeer(port, label);
            return peer.Run();
        }
    }

    /// <summary>The numbers of the session, exactly as the README of the interop tests gives them.</summary>
    internal static class Data
    {
        public const int N = 5, FrameW = 64, FrameH = 48, Slots = 3, CollisionTriangleCount = 300;
        public const ulong BaseUs = 1_700_000_000_000_000UL;
        public const uint HostEntity = 3, GuestEntity = 7;

        public static Wire.Camera Camera(int k)
        {
            var view = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, k, 0.5f, -8, 1 };
            var proj = new float[] { 1.5f, 0, 0, 0, 0, 2, 0, 0, 0, 0, -1, -1, 0, 0, -0.25f, 0 };
            return new Wire.Camera(view, proj, 1.0f, 0.25f, k % 2 == 1 ? float.PositiveInfinity : 500f, FrameW, FrameH, (Wire.DepthRange)(k % 3));
        }

        public static Wire.Pose Pose(bool host, int k)
        {
            var p = new Wire.Pose
            {
                Entity = host ? HostEntity : GuestEntity,
                Flags = k == 1 ? Wire.PoseTeleport : 0,
                TimeUs = BaseUs + 1000UL * (ulong)k + (host ? 0UL : 500UL),
                Translation = host ? new float[] { k, 2, -3 } : new float[] { -k, 1, 4 },
                Rotation = new float[] { 0, 0, 0, 1 },
                Scale = new float[] { 1, 1, 1 },
                Velocity = new float[] { 0.5f, 0, 0 },
                AngularVelocity = new float[] { 0, 0.25f, 0 },
            };
            return p;
        }

        public static float[] Center() { return new float[] { 1, 2, 3 }; }
        public const float Radius = 50f;

        public static float[] Triangles()
        {
            var t = new float[9 * CollisionTriangleCount];
            for (int i = 0; i < CollisionTriangleCount; i++)
            {
                int b = i % 64;
                float[] v = { b, 0, 0, b + 1, 0, 0, b, 0, 1 };
                Array.Copy(v, 0, t, 9 * i, 9);
            }
            return t;
        }

        public static ulong TimeUs(int k) { return BaseUs + 16_666UL * (ulong)k; }

        public static byte[] Rgba(int k)
        {
            var b = new byte[FrameW * FrameH * 4];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)((37 * i + 11 * k + 5) & 255);
            b[0] = b[1] = b[2] = b[3] = 0;
            return b;
        }

        public static float[] Depth(int k)
        {
            var d = new float[FrameW * FrameH];
            d[0] = float.PositiveInfinity;
            for (int p = 1; p < d.Length; p++) d[p] = (float)(0.5 * (p + 1) + k);
            return d;
        }

        // ------------------------------------------------------------ comparisons: null when equal, else what differs

        private static bool Same(float a, float b) { return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b); }

        private static string Floats(string name, float[] got, float[] want)
        {
            if (got == null || got.Length != want.Length) return name + ": " + (got == null ? "missing" : got.Length + " values") + ", expected " + want.Length;
            for (int i = 0; i < want.Length; i++)
                if (!Same(got[i], want[i])) return name + "[" + i + "] is " + F(got[i]) + ", expected " + F(want[i]);
            return null;
        }

        public static string F(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }

        public static string CompareCamera(Wire.Camera got, Wire.Camera want)
        {
            return Floats("view", got.View, want.View) ?? Floats("proj", got.Proj, want.Proj)
                ?? (Same(got.FovY, want.FovY) ? null : "fov_y is " + F(got.FovY) + ", expected " + F(want.FovY))
                ?? (Same(got.Near, want.Near) ? null : "near is " + F(got.Near) + ", expected " + F(want.Near))
                ?? (Same(got.Far, want.Far) ? null : "far is " + F(got.Far) + ", expected " + F(want.Far))
                ?? (got.ViewportWidth == want.ViewportWidth && got.ViewportHeight == want.ViewportHeight
                    ? null : "viewport is " + got.ViewportWidth + "x" + got.ViewportHeight + ", expected " + want.ViewportWidth + "x" + want.ViewportHeight)
                ?? (got.DepthRange == want.DepthRange ? null : "depth_range is " + got.DepthRange + ", expected " + want.DepthRange);
        }

        public static string ComparePose(Wire.Pose got, Wire.Pose want)
        {
            if (got.Entity != want.Entity) return "entity is " + got.Entity + ", expected " + want.Entity;
            if (got.Flags != want.Flags) return "flags is " + got.Flags + ", expected " + want.Flags;
            if (got.TimeUs != want.TimeUs) return "time_us is " + got.TimeUs + ", expected " + want.TimeUs;
            return Floats("translation", got.Translation, want.Translation) ?? Floats("rotation", got.Rotation, want.Rotation)
                ?? Floats("scale", got.Scale, want.Scale) ?? Floats("velocity", got.Velocity, want.Velocity)
                ?? Floats("angular_velocity", got.AngularVelocity, want.AngularVelocity);
        }

        public static string CompareCollision(LinkEvent.Collision got)
        {
            if (got.TriangleCount != CollisionTriangleCount) return got.TriangleCount + " triangles, expected " + CollisionTriangleCount;
            if (!Same(got.Radius, Radius)) return "radius is " + F(got.Radius) + ", expected " + F(Radius);
            return Floats("center", got.Center, Center()) ?? Floats("triangles", got.Triangles.Take(9 * got.TriangleCount).ToArray(), Triangles());
        }

        public static string ComparePixels(PixelFile.Frame f, int k)
        {
            if (f.Width != FrameW || f.Height != FrameH) return "size is " + f.Width + "x" + f.Height + ", expected " + FrameW + "x" + FrameH;
            if (f.TimeUs != TimeUs(k)) return "time_us is " + f.TimeUs + ", expected " + TimeUs(k);
            byte[] rgba = Rgba(k);
            if (f.Rgba == null || f.Rgba.Length != rgba.Length) return "colour plane has the wrong size";
            for (int i = 0; i < rgba.Length; i++) if (f.Rgba[i] != rgba[i]) return "colour byte " + i + " is " + f.Rgba[i] + ", expected " + rgba[i];
            if (f.Depth == null) return "no depth plane";
            return Floats("depth", f.Depth, Depth(k));
        }
    }

    /// <summary>What both sides share: the poll loop, the timeouts, the failures and the result line.</summary>
    internal abstract class Peer
    {
        private const int StepTimeoutMs = 10000, TotalTimeoutMs = 30000;

        protected readonly string RoleName, Label;
        protected readonly Link Link;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _failures = new List<string>();
        private string _phase = "the peer to connect";
        private long _phaseSince = -1; // -1: waiting for the peer to show up is limited by the 30 seconds only

        protected bool Finished;
        protected string PeerGame = "";
        protected int Protocol;
        protected uint Session;
        protected readonly List<uint> Spawns = new List<uint>(), Despawns = new List<uint>();
        protected int Cameras, Poses, CollisionTriangles, FramesOk, FramesBad;
        protected readonly List<string> Focus = new List<string>();
        protected string LostReason;
        protected string Stats = "null";

        protected Peer(bool host, int port, string label)
        {
            RoleName = host ? "host" : "guest";
            Label = label;
            var cfg = new LinkConfig("interop " + RoleName + " (" + label + ")")
            {
                ControlPort = port, UdpPort = 0, HeartbeatMs = 100, TimeoutMs = 3000, ReconnectMs = 100,
            };
            Log((host ? "listening on" : "connecting to") + " 127.0.0.1:" + port);
            Link = host ? Link.Host(cfg) : Link.Guest(cfg);
        }

        protected long NowMs { get { return _clock.ElapsedMilliseconds; } }

        protected void Log(string msg)
        {
            Console.WriteLine("[cs-" + RoleName + " " + (NowMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture).PadLeft(6) + "s] " + msg);
            Console.Out.Flush();
        }

        protected void Fail(string what)
        {
            _failures.Add(what);
            Log("FAILED: " + what);
        }

        /// <summary>Names what the peer waits for now; a wait of more than 10 seconds fails the session.</summary>
        protected void Phase(string what)
        {
            _phase = what;
            _phaseSince = NowMs;
            Log(what);
        }

        protected abstract void OnEvent(LinkEvent e);

        /// <summary>Called after every poll: the steps that do not answer an event.</summary>
        protected abstract void Tick();

        /// <summary>Called once at the end, whatever happened.</summary>
        protected virtual void Cleanup() { }

        protected void CheckPeer(PeerInfo p, string expectedGamePrefix)
        {
            PeerGame = p.Game;
            Protocol = p.Protocol;
            Session = p.Session;
            if (!p.Game.StartsWith(expectedGamePrefix, StringComparison.Ordinal))
                Fail("the peer's game is \"" + p.Game + "\", expected it to start with \"" + expectedGamePrefix + "\"");
            if (p.Protocol != 1) Fail("the negotiated protocol is " + p.Protocol + ", expected 1");
            if (p.Session == 0) Fail("the session is 0");
            Log("connected: " + p.Role + " \"" + p.Game + "\", protocol v" + p.Protocol + ", session " + p.Session);
        }

        /// <summary>Reads the counters. Done when the rounds are over, not at exit: a heartbeat of the other side still in flight after the session
        /// has ended is legitimately foreign to a link that is no longer in a session.</summary>
        protected void SnapshotStats()
        {
            Stats = "{\"malformed\":" + Link.Malformed + ",\"foreign\":" + Link.Foreign + ",\"stale\":" + Link.Stale
                + ",\"unknown_entity\":" + Link.UnknownEntity + ",\"spawn_ignored\":" + Link.SpawnIgnored + "}";
        }

        protected void RecordFocus(LinkEvent.Focus f)
        {
            Focus.Add(Control.RoleName(f.Value) + ":" + (f.Requested ? "request" : "set"));
            Log("focus event " + Focus[Focus.Count - 1]);
        }

        public int Run()
        {
            var events = new List<LinkEvent>();
            try
            {
                while (!Finished)
                {
                    events.Clear();
                    Link.Poll(events);
                    foreach (LinkEvent e in events)
                    {
                        if (e is LinkEvent.HandshakeFailed) Fail("handshake failed: " + ((LinkEvent.HandshakeFailed)e).Reason);
                        else OnEvent(e);
                        if (Finished) break;
                    }
                    if (Finished) break;
                    Tick();
                    if (Finished) break;
                    if (NowMs > TotalTimeoutMs) { Fail("gave up: the session took more than 30 seconds (waiting: " + _phase + ")"); break; }
                    if (_phaseSince >= 0 && NowMs - _phaseSince > StepTimeoutMs) { Fail("timeout: more than 10 seconds waiting for: " + _phase); break; }
                    Thread.Sleep(2);
                }
            }
            catch (Exception e)
            {
                Fail("exception " + e.GetType().Name + ": " + e.Message);
            }
            try
            {
                if (Stats == "null") SnapshotStats(); // a run that ended early (a failure) never reached the end of the rounds
                Cleanup();
                Link.Dispose();
            }
            catch (Exception e)
            {
                Fail("exception while closing " + e.GetType().Name + ": " + e.Message);
            }
            Console.WriteLine(ResultLine());
            Console.Out.Flush();
            return _failures.Count == 0 ? 0 : 1;
        }

        private static string Quote(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 0x20 || c > 0x7e) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private static string List(IEnumerable<string> items) { return "[" + string.Join(",", items) + "]"; }

        private string ResultLine()
        {
            var b = new StringBuilder("RBL-INTEROP-RESULT {");
            b.Append("\"role\":").Append(Quote(RoleName)).Append(",\"label\":").Append(Quote(Label));
            b.Append(",\"ok\":").Append(_failures.Count == 0 ? "true" : "false");
            b.Append(",\"failures\":").Append(List(_failures.Select(Quote)));
            b.Append(",\"peer_game\":").Append(Quote(PeerGame));
            b.Append(",\"protocol\":").Append(Protocol.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"session\":").Append(SessionJson());
            b.Append(",\"spawns\":").Append(List(Spawns.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            b.Append(",\"despawns\":").Append(List(Despawns.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            b.Append(",\"cameras\":").Append(Cameras).Append(",\"poses\":").Append(Poses);
            b.Append(",\"collision_triangles\":").Append(CollisionTriangles);
            b.Append(",\"frames_ok\":").Append(FramesOk).Append(",\"frames_bad\":").Append(FramesBad);
            b.Append(",\"focus\":").Append(List(Focus.Select(Quote)));
            b.Append(",\"lost_reason\":").Append(LostReason == null ? "null" : Quote(LostReason));
            b.Append(",\"stats\":").Append(Stats);
            return b.Append("}").ToString();
        }

        private string SessionJson() { return Session.ToString(CultureInfo.InvariantCulture); }
    }

    // ---------------------------------------------------------------------------------------------------------------- host

    internal sealed class HostPeer : Peer
    {
        private PixelFile.Reader _reader;
        private bool _guestSpawned, _started;
        private int _k;                    // the frame in flight, 1 ..= N; N + 1 when all are done
        private uint _cameraSeq;
        private bool _gotPose, _gotFrame;

        public HostPeer(int port, string label) : base(true, port, label) { }

        protected override void OnEvent(LinkEvent e)
        {
            switch (e.Kind)
            {
                case LinkEventKind.Connected:
                    CheckPeer(((LinkEvent.Connected)e).Peer, "interop guest (");
                    Link.Spawn(Data.HostEntity, "War Thunder", "/interop/tank", "Host Tank");
                    Link.SetFocus(Role.Guest);
                    Phase("waiting for the guest's spawn and frame_channel");
                    break;
                case LinkEventKind.Spawn:
                {
                    Control.Spawn s = ((LinkEvent.Spawn)e).Value;
                    Spawns.Add(s.Entity);
                    if (s.Entity == Data.GuestEntity && s.Provider == "Warframe" && s.Asset == "/Lotus/Characters/Excalibur" && s.Name == "Excalibur") _guestSpawned = true;
                    else Fail("unexpected spawn: entity " + s.Entity + ", provider " + s.Provider + ", asset " + s.Asset + ", name " + s.Name);
                    break;
                }
                case LinkEventKind.FrameChannel:
                {
                    Control.FrameChannel c = ((LinkEvent.FrameChannel)e).Value;
                    if (c.Width != Data.FrameW || c.Height != Data.FrameH || c.Slots != Data.Slots)
                        Fail("frame_channel announces " + c.Width + "x" + c.Height + " with " + c.Slots + " slots, expected " + Data.FrameW + "x" + Data.FrameH + " with " + Data.Slots);
                    try
                    {
                        _reader = PixelFile.Reader.Open(c.Path);
                        if (!_reader.HasDepth) Fail("the guest's pixel file has no depth plane");
                        Log("opened the guest's pixel file " + c.Path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Fail("cannot open the guest's pixel file: " + ex.Message);
                    }
                    break;
                }
                case LinkEventKind.Focus:
                {
                    var f = (LinkEvent.Focus)e;
                    RecordFocus(f);
                    if (f.Requested) Link.SetFocus(Role.Host);
                    break;
                }
                case LinkEventKind.Poses:
                {
                    var p = (LinkEvent.Poses)e;
                    if (!_started || _k > Data.N || _gotPose) { Fail("unexpected Poses event"); break; }
                    _gotPose = true;
                    if (p.Values.Count != 1) { Fail("frame " + _k + ": " + p.Values.Count + " poses, expected 1"); break; }
                    string err = Data.ComparePose(p.Values[0], Data.Pose(false, _k));
                    if (err != null) Fail("frame " + _k + ": pose of the guest: " + err);
                    else Poses++;
                    break;
                }
                case LinkEventKind.Frame:
                    OnFrame(((LinkEvent.Frame)e).Value);
                    break;
                case LinkEventKind.Despawn:
                    Despawns.Add(((LinkEvent.Despawn)e).Entity);
                    break;
                case LinkEventKind.Lost:
                    LostReason = ((LinkEvent.Lost)e).Reason;
                    Log("LOST the guest: " + LostReason);
                    if (_k <= Data.N || Despawns.Count == 0) Fail("the guest was lost before the session was over: " + LostReason);
                    else if (LostReason.IndexOf("bye", StringComparison.Ordinal) < 0) Fail("the reason of Lost is \"" + LostReason + "\", expected it to contain bye");
                    Finished = true;
                    break;
                default:
                    Fail("unexpected " + e.Kind + " event");
                    break;
            }
        }

        private void OnFrame(Wire.FrameReady fr)
        {
            if (!_started || _k > Data.N || _gotFrame) { Fail("unexpected Frame event"); return; }
            _gotFrame = true;
            string err = null;
            if (fr.Frame != (ulong)_k || fr.Slot != (uint)(_k % Data.Slots) || fr.CameraSeq != _cameraSeq || fr.Width != Data.FrameW || fr.Height != Data.FrameH)
                err = "the Frame datagram says frame " + fr.Frame + " slot " + fr.Slot + " camera_seq " + fr.CameraSeq + " " + fr.Width + "x" + fr.Height
                    + ", expected frame " + _k + " slot " + (_k % Data.Slots) + " camera_seq " + _cameraSeq + " " + Data.FrameW + "x" + Data.FrameH;
            else if (_reader == null) err = "no pixel file is open";
            else
            {
                PixelFile.Frame f = _reader.Read(fr.Frame, (int)fr.Slot, true);
                if (f == null) err = "the frame is not in the pixel file (or was skipped)";
                else if (f.CameraSeq != _cameraSeq) err = "camera_seq in the slot is " + f.CameraSeq + ", expected " + _cameraSeq;
                else err = Data.ComparePixels(f, _k);
            }
            if (err != null) { FramesBad++; Fail("frame " + _k + ": " + err); }
            else FramesOk++;
        }

        protected override void Tick()
        {
            if (!_started)
            {
                if (_guestSpawned && _reader != null) { _started = true; _k = 0; NextFrame(); }
                return;
            }
            if (_k <= Data.N)
            {
                if (_gotPose && _gotFrame) NextFrame();
                return;
            }
            // all frames verified: the host's despawn tells the guest that everything it sent has been read
            if (!_despawnSent)
            {
                _despawnSent = true;
                SnapshotStats();
                Link.Despawn(Data.HostEntity);
                Phase("waiting for the guest's despawn and bye");
            }
        }

        private bool _despawnSent;

        private void NextFrame()
        {
            _k++;
            if (_k > Data.N)
            {
                return;
            }
            _gotPose = _gotFrame = false;
            _cameraSeq = Link.SendCamera(Data.Camera(_k));
            Link.SendPoses(new[] { Data.Pose(true, _k) });
            if (_k == 1) Link.SendCollision(Data.Center(), Data.Radius, Data.Triangles());
            Phase("frame " + _k + ": waiting for the guest's pose and frame");
        }

        protected override void Cleanup()
        {
            if (_reader != null) _reader.Dispose();
            if (!Finished) return;
            if (!Spawns.SequenceEqual(new[] { Data.GuestEntity })) Fail("spawns " + string.Join(",", Spawns) + ", expected 7");
            if (!Despawns.SequenceEqual(new[] { Data.GuestEntity })) Fail("despawns " + string.Join(",", Despawns) + ", expected 7");
            if (!Focus.SequenceEqual(new[] { "host:request" })) Fail("focus events [" + string.Join(", ", Focus) + "], expected [host:request]");
            if (FramesOk != Data.N) Fail("frames_ok is " + FramesOk + ", expected " + Data.N);
            if (Poses != Data.N) Fail("poses is " + Poses + ", expected " + Data.N);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- guest

    internal sealed class GuestPeer : Peer
    {
        private PixelFile.Writer _writer;
        private bool _hostSpawned;
        private long _closeAt = -1;

        public GuestPeer(int port, string label) : base(false, port, label) { }

        protected override void OnEvent(LinkEvent e)
        {
            switch (e.Kind)
            {
                case LinkEventKind.Connected:
                    CheckPeer(((LinkEvent.Connected)e).Peer, "interop host (");
                    Link.Spawn(Data.GuestEntity, "Warframe", "/Lotus/Characters/Excalibur", "Excalibur");
                    _writer = PixelFile.Writer.CreateTemp("interop", Data.FrameW, Data.FrameH, Data.Slots, true);
                    Link.AnnounceFrames(_writer);
                    Log("announced the pixel file " + _writer.Path);
                    Phase("waiting for the host's spawn and the first camera");
                    break;
                case LinkEventKind.Spawn:
                {
                    Control.Spawn s = ((LinkEvent.Spawn)e).Value;
                    Spawns.Add(s.Entity);
                    if (s.Entity == Data.HostEntity && s.Provider == "War Thunder" && s.Asset == "/interop/tank" && s.Name == "Host Tank") _hostSpawned = true;
                    else Fail("unexpected spawn: entity " + s.Entity + ", provider " + s.Provider + ", asset " + s.Asset + ", name " + s.Name);
                    break;
                }
                case LinkEventKind.Focus:
                {
                    var f = (LinkEvent.Focus)e;
                    RecordFocus(f);
                    // the host gave us the focus: ask for the host to take it back. The host answers with its own input message.
                    if (Focus.Count == 1 && f.Value == Role.Guest && !f.Requested) Link.SetFocus(FocusRequest);
                    break;
                }
                case LinkEventKind.Camera:
                    OnCamera((LinkEvent.Camera)e);
                    break;
                case LinkEventKind.Poses:
                {
                    var p = (LinkEvent.Poses)e;
                    int k = Poses + 1;
                    if (k > Data.N) { Fail("unexpected Poses event"); break; }
                    if (p.Values.Count != 1) { Fail("host pose " + k + ": " + p.Values.Count + " poses, expected 1"); Poses++; break; }
                    string err = Data.ComparePose(p.Values[0], Data.Pose(true, k));
                    if (err != null) Fail("host pose " + k + ": " + err);
                    Poses++;
                    break;
                }
                case LinkEventKind.Collision:
                {
                    var c = (LinkEvent.Collision)e;
                    string err = Data.CompareCollision(c);
                    if (err != null) Fail("collision snapshot: " + err);
                    else CollisionTriangles = c.TriangleCount;
                    break;
                }
                case LinkEventKind.Despawn:
                    Despawns.Add(((LinkEvent.Despawn)e).Entity);
                    SnapshotStats();
                    // the host has read everything we sent: despawn, say bye, and keep polling for 200 ms so the closing drains
                    Link.Despawn(Data.GuestEntity);
                    Link.Disconnect("done");
                    _closeAt = NowMs + 200;
                    Phase("saying bye: polling for 200 ms");
                    break;
                case LinkEventKind.Lost:
                    LostReason = ((LinkEvent.Lost)e).Reason;
                    Fail("the host was lost: " + LostReason);
                    Finished = true;
                    break;
                default:
                    Fail("unexpected " + e.Kind + " event");
                    break;
            }
        }

        /// <summary>The focus the guest asks for when the host has given it the focus: the README's prose says "requests it back (input host)".</summary>
        private static readonly Role FocusRequest = Role.Host;

        private void OnCamera(LinkEvent.Camera c)
        {
            int k = Cameras + 1;
            if (k > Data.N) { Fail("unexpected Camera event"); return; }
            Cameras++;
            string err = Data.CompareCamera(c.Value, Data.Camera(k));
            if (err != null) Fail("camera " + k + ": " + err);
            Link.SendPoses(new[] { Data.Pose(false, k) });
            ulong frame = _writer.Write(Data.Rgba(k), Data.Depth(k), Data.FrameW, Data.FrameH, c.Header.Seq, Data.TimeUs(k));
            if (frame != (ulong)k) Fail("the pixel file numbered frame " + frame + ", expected " + k);
            Link.SendFrame(new Wire.FrameReady(frame, (uint)(frame % (ulong)Data.Slots), c.Header.Seq, Data.FrameW, Data.FrameH));
            Phase(k == Data.N ? "waiting for the host's despawn" : "camera " + (k + 1) + ": waiting for the host's camera");
        }

        protected override void Tick()
        {
            if (_closeAt >= 0 && NowMs >= _closeAt) Finished = true;
        }

        protected override void Cleanup()
        {
            if (_writer != null) _writer.Dispose();
            if (!Finished) return;
            if (!_hostSpawned) Fail("the host's spawn never arrived");
            if (!Spawns.SequenceEqual(new[] { Data.HostEntity })) Fail("spawns " + string.Join(",", Spawns) + ", expected 3");
            if (!Despawns.SequenceEqual(new[] { Data.HostEntity })) Fail("despawns " + string.Join(",", Despawns) + ", expected 3");
            if (!Focus.SequenceEqual(new[] { "guest:set", "host:set" })) Fail("focus events [" + string.Join(", ", Focus) + "], expected [guest:set, host:set]");
            if (Cameras != Data.N) Fail("cameras is " + Cameras + ", expected " + Data.N);
            if (Poses != Data.N) Fail("poses is " + Poses + ", expected " + Data.N);
            if (CollisionTriangles != Data.CollisionTriangleCount) Fail("collision_triangles is " + CollisionTriangles + ", expected " + Data.CollisionTriangleCount);
        }
    }
}
