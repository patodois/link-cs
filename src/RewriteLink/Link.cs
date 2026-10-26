using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Patodois.RewriteLink
{
    /// <summary>There is no session: connect first (the guest keeps trying; the host waits for a guest).</summary>
    public sealed class NotConnectedException : InvalidOperationException
    {
        public NotConnectedException() : base("not connected") { }
    }

    /// <summary>
    /// One side of a passthrough link (host or guest). Create it with <see cref="Host"/> or <see cref="Guest"/>, then call
    /// <see cref="Poll()"/> regularly; it never blocks. Every public method takes the link's lock, so a game thread may send while a
    /// helper thread polls. Everything binds and connects to 127.0.0.1 only.
    /// </summary>
    public sealed class Link : IDisposable
    {
        private const long ConnectTimeoutMs = 1000;

        private enum State { Idle, Connecting, Handshake, Live }

        // ---------------------------------------------------------------- the control connection

        /// <summary>A control connection: non-blocking socket, line reader and write queue.</summary>
        private sealed class Ctl
        {
            public Socket Sock;
            public byte[] RBuf = new byte[0];
            public int RStart, RLen;
            public byte[] WBuf = new byte[0];
            public int WLen;
            public string Fatal; // the stream is unusable (line too long, not UTF-8)
            public string End;   // the peer closed or the read failed: applies after the last complete line

            public Ctl(Socket s)
            {
                Sock = s;
                s.Blocking = false;
                try { s.NoDelay = true; } catch (SocketException) { }
            }

            public bool Queue(Control c)
            {
                byte[] line = Control.Utf8.GetBytes(c.ToLine());
                if (line.Length > Wire.MaxLine) return false;
                if (WLen + line.Length > WBuf.Length) Array.Resize(ref WBuf, Math.Max(WBuf.Length * 2, WLen + line.Length));
                Buffer.BlockCopy(line, 0, WBuf, WLen, line.Length);
                WLen += line.Length;
                return true;
            }

            /// <summary>Writes as much as the socket takes without blocking; returns an error reason or null.</summary>
            public string Flush() { return FlushTo(Sock, WBuf, ref WLen); }

            public static string FlushTo(Socket s, byte[] wbuf, ref int wlen)
            {
                int off = 0;
                while (off < wlen)
                {
                    SocketError err;
                    int n;
                    try { n = s.Send(wbuf, off, wlen - off, SocketFlags.None, out err); }
                    catch (SocketException) { return "control write failed"; }
                    catch (ObjectDisposedException) { return "control write failed"; }
                    if (err == SocketError.WouldBlock || err == SocketError.InProgress) break;
                    if (err != SocketError.Success) return "control write failed";
                    if (n <= 0) break;
                    off += n;
                }
                if (off > 0)
                {
                    Buffer.BlockCopy(wbuf, off, wbuf, 0, wlen - off);
                    wlen -= off;
                }
                return wlen > Wire.MaxPendingControl ? "peer stopped reading the control channel" : null;
            }

            /// <summary>Reads what is there; a closed or failed stream is remembered in <see cref="End"/>.</summary>
            public void Fill()
            {
                if (End != null || Fatal != null) return;
                if (RStart > 0)
                {
                    Buffer.BlockCopy(RBuf, RStart, RBuf, 0, RLen - RStart);
                    RLen -= RStart;
                    RStart = 0;
                }
                while (true)
                {
                    if (RLen > 4 * Wire.MaxLine) break; // let the caller process what it has first
                    if (RBuf.Length - RLen < 8192) Array.Resize(ref RBuf, Math.Max(16384, RBuf.Length * 2));
                    SocketError err;
                    int n;
                    try { n = Sock.Receive(RBuf, RLen, 8192, SocketFlags.None, out err); }
                    catch (SocketException) { End = "control read failed"; break; }
                    catch (ObjectDisposedException) { End = "control read failed"; break; }
                    if (err == SocketError.WouldBlock || err == SocketError.InProgress) break;
                    if (err != SocketError.Success) { End = "control read failed"; break; }
                    if (n == 0) { End = "connection closed"; break; }
                    RLen += n;
                }
            }

            /// <summary>The next complete line (with its newline), or null if there is none or the stream is unusable (<see cref="Fatal"/>).</summary>
            public string NextLine()
            {
                int nl = Array.IndexOf(RBuf, (byte)'\n', RStart, RLen - RStart);
                if (nl < 0)
                {
                    if (RLen - RStart >= Wire.MaxLine) Fatal = "control line too long";
                    return null;
                }
                int n = nl + 1 - RStart;
                if (n > Wire.MaxLine) { Fatal = "control line too long"; return null; }
                string s;
                try { s = Control.Utf8Strict.GetString(RBuf, RStart, n); }
                catch (DecoderFallbackException) { Fatal = "control line is not UTF-8"; return null; }
                RStart += n;
                return s;
            }

            public void CloseQuietly()
            {
                try { Sock.Close(); } catch (SocketException) { } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// A control connection being closed gracefully (CONTRACT.md 6.1): flush the last bytes (usually a bye), half-close the write
        /// side, then read and discard what the peer still sends until it closes too or 200 ms pass, and only then close the socket.
        /// Closing a socket that holds unread data makes the OS send a reset, which on Windows can destroy the bye.
        /// </summary>
        private sealed class Closing
        {
            public readonly Socket Sock;
            public byte[] WBuf;
            public int WLen;
            public readonly long Deadline;
            public bool Shut;
            private readonly byte[] _junk = new byte[4096];

            public Closing(Ctl c, long now)
            {
                Sock = c.Sock; WBuf = c.WBuf; WLen = c.WLen; Deadline = now + Wire.LingerMs;
            }

            /// <summary>Makes progress without blocking; false once the socket is closed.</summary>
            public bool Pump(long now)
            {
                if (now >= Deadline || Ctl.FlushTo(Sock, WBuf, ref WLen) != null) return Done();
                try
                {
                    if (!Shut)
                    {
                        if (WLen > 0) return true;
                        Sock.Shutdown(SocketShutdown.Send);
                        Shut = true;
                    }
                    for (int i = 0; i < 64; i++)
                    {
                        SocketError err;
                        int n = Sock.Receive(_junk, 0, _junk.Length, SocketFlags.None, out err);
                        if (err == SocketError.WouldBlock || err == SocketError.InProgress) return true;
                        if (err != SocketError.Success || n == 0) return Done();
                    }
                    return true;
                }
                catch (SocketException) { return Done(); }
                catch (ObjectDisposedException) { return Done(); }
            }

            private bool Done()
            {
                try { Sock.Close(); } catch (SocketException) { } catch (ObjectDisposedException) { }
                return false;
            }
        }

        private sealed class Assembly
        {
            public uint Query;
            public int Count, Received;
            public float[] Center;
            public float Radius;
            public float[][] Chunks;
            public int[] ChunkTriangles;
            public long Triangles;
        }

        // ---------------------------------------------------------------- state

        private readonly object _gate = new object();
        private readonly Role _role;
        private readonly LinkConfig _cfg;
        private readonly long _heartbeatMs;
        private readonly Socket _listener; // host only
        private readonly Socket _udp;
        private readonly int _controlPort;
        private readonly int _udpPort;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _collisionBurst;
        private readonly byte[] _rx = new byte[2 * Wire.MaxDatagram];
        private readonly byte[] _tx = new byte[Wire.MaxDatagram];
        private EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

        private State _state = State.Idle;
        private long _nextTry, _since;
        private Socket _connecting;
        private Ctl _ctl;
        private PeerInfo _peer;
        private IPEndPoint _peerEp;
        private long _timeoutMs, _lastHeard, _lastBeat;
        private readonly List<Closing> _closing = new List<Closing>();
        private readonly List<LinkEvent> _events = new List<LinkEvent>();
        private uint _seq;
        private uint _nextQuery = 1;
        private Role _focus = Role.Host;

        private List<Wire.CollisionChunk> _cq = new List<Wire.CollisionChunk>();
        private int _cqNext;

        private bool _hasLastCamera;
        private uint _lastCamera;
        private readonly Dictionary<uint, ulong> _entities = new Dictionary<uint, ulong>();
        private bool _spawnIgnoredLogged;
        private bool _hasLastCollision;
        private uint _lastCollision;
        private Assembly _assembly;
        private ulong _lastFrame;
        private bool _closed;

        // diagnostics
        public long DatagramsSent, DatagramsReceived, Malformed, Stale, Foreign, SendDropped, LinesSent, LinesReceived;
        /// <summary>Pose records for an entity the peer never spawned (or already despawned); not delivered.</summary>
        public long UnknownEntity;
        /// <summary>Spawns ignored because the peer already had <see cref="Wire.MaxEntities"/> live entities.</summary>
        public long SpawnIgnored;

        // ---------------------------------------------------------------- construction

        private Link(Role role, LinkConfig cfg)
        {
            _role = role;
            _cfg = cfg;
            _heartbeatMs = Math.Max(Wire.MinHeartbeatMs, Math.Min(Wire.MaxHeartbeatMs, cfg.HeartbeatMs));
            _nextTry = Now();
            Socket u = null, l = null;
            try
            {
                u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Collision snapshots arrive in bursts (CONTRACT.md 2); the OS may grant less, or refuse.
                try { u.ReceiveBufferSize = Wire.RecvBufferBytes; } catch (SocketException) { }
                u.Bind(new IPEndPoint(IPAddress.Loopback, cfg.UdpPort));
                u.Blocking = false;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // ICMP port-unreachable from an earlier send must not make ReceiveFrom fail (SIO_UDP_CONNRESET off)
                    try { u.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
                }
                if (role == Role.Host)
                {
                    l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    l.Bind(new IPEndPoint(IPAddress.Loopback, cfg.ControlPort));
                    l.Listen(8);
                    l.Blocking = false;
                }
            }
            catch
            {
                if (u != null) u.Close();
                if (l != null) l.Close();
                throw;
            }
            _udp = u;
            _listener = l;
            _udpPort = ((IPEndPoint)u.LocalEndPoint).Port;
            _controlPort = l != null ? ((IPEndPoint)l.LocalEndPoint).Port : cfg.ControlPort;
            // The peer is on this machine and asked for the same buffer, so what this socket was granted is what its buffer will be.
            // Linux caps the request at net.core.rmem_max and reports twice what it keeps; an 8 KiB datagram costs about 12 KB of
            // that. The contract says "at most 32", so fewer is allowed: never burst more than the buffer holds.
            int burst = Wire.CollisionPerPoll;
            try { burst = Math.Max(8, Math.Min(burst, u.ReceiveBufferSize / 12000)); } catch (SocketException) { }
            _collisionBurst = burst;
        }

        /// <summary>Listens for one guest on 127.0.0.1:<c>cfg.ControlPort</c>.</summary>
        public static Link Host(LinkConfig cfg) { return new Link(Role.Host, cfg); }

        /// <summary>Connects to a host on 127.0.0.1:<c>cfg.ControlPort</c>, retrying until one answers.</summary>
        public static Link Guest(LinkConfig cfg) { return new Link(Role.Guest, cfg); }

        private long Now() { return _clock.ElapsedTicks * 1000L / Stopwatch.Frequency; }

        public Role Role { get { return _role; } }
        /// <summary>Host: the control port actually bound.</summary>
        public int ControlPort { get { return _controlPort; } }
        public int UdpPort { get { return _udpPort; } }
        public bool IsConnected { get { lock (_gate) { return _state == State.Live; } } }
        /// <summary>The peer of the current session, or null.</summary>
        public PeerInfo Peer { get { lock (_gate) { return _state == State.Live ? _peer : null; } } }
        public Role Focus { get { lock (_gate) { return _focus; } } }

        // ---------------------------------------------------------------- polling

        /// <summary>Does one round of work and returns what happened. Call every few milliseconds (every frame).</summary>
        public List<LinkEvent> Poll()
        {
            var list = new List<LinkEvent>();
            Poll(list);
            return list;
        }

        /// <summary>Like <see cref="Poll()"/>, appending the events to <paramref name="into"/> (no list is allocated).</summary>
        public void Poll(List<LinkEvent> into)
        {
            lock (_gate)
            {
                if (_closed) return;
                long now = Now();
                for (int i = _closing.Count - 1; i >= 0; i--) if (!_closing[i].Pump(now)) _closing.RemoveAt(i);
                PollConnect(now);
                PollControl(now);
                PollUdp(now);
                PumpCollision();
                PollTimers(now);
                into.AddRange(_events);
                _events.Clear();
            }
        }

        private Control.Hello MakeHello(uint protocol, uint? session)
        {
            return new Control.Hello
            {
                Role = _role, Game = _cfg.Game ?? "", Protocol = protocol, MinProtocol = Wire.MinProtocolVersion, UdpPort = (uint)_udpPort,
                HeartbeatMs = (uint)_heartbeatMs, TimeoutMs = (uint)Math.Min(_cfg.TimeoutMs, uint.MaxValue), Session = session,
            };
        }

        private static bool WouldBlock(SocketException e)
        {
            return e.SocketErrorCode == SocketError.WouldBlock || e.SocketErrorCode == SocketError.InProgress || e.SocketErrorCode == SocketError.IOPending;
        }

        private void PollConnect(long now)
        {
            if (_listener != null)
            {
                while (true)
                {
                    Socket s;
                    try
                    {
                        if (!_listener.Poll(0, SelectMode.SelectRead)) break;
                        s = _listener.Accept();
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    Ctl ctl;
                    try { ctl = new Ctl(s); }
                    catch (SocketException) { try { s.Close(); } catch (SocketException) { } continue; }
                    if (_state == State.Idle) { _ctl = ctl; _state = State.Handshake; _since = now; }
                    else Retire(ctl, "busy", now); // one guest per host
                }
                return;
            }
            if (_state == State.Idle && now >= _nextTry)
            {
                Socket s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    s.Blocking = false;
                    try { s.Connect(new IPEndPoint(IPAddress.Loopback, _cfg.ControlPort)); }
                    catch (SocketException e) { if (!WouldBlock(e)) throw; }
                    _connecting = s;
                    _state = State.Connecting;
                    _since = now;
                }
                catch (SocketException)
                {
                    if (s != null) { try { s.Close(); } catch (SocketException) { } }
                    _nextTry = now + _cfg.ReconnectMs;
                }
            }
            else if (_state == State.Connecting)
            {
                try
                {
                    if (_connecting.Poll(0, SelectMode.SelectError)) { AbortConnect(now); return; }
                    if (_connecting.Poll(0, SelectMode.SelectWrite))
                    {
                        if ((int)_connecting.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) != 0) { AbortConnect(now); return; }
                        var ctl = new Ctl(_connecting);
                        _connecting = null;
                        ctl.Queue(MakeHello(Wire.ProtocolVersion, null));
                        LinesSent++;
                        ctl.Flush();
                        _ctl = ctl;
                        _state = State.Handshake;
                        _since = now;
                    }
                    else if (now - _since > ConnectTimeoutMs) AbortConnect(now);
                }
                catch (SocketException) { AbortConnect(now); }
                catch (ObjectDisposedException) { AbortConnect(now); }
            }
        }

        private void AbortConnect(long now)
        {
            if (_connecting != null) { try { _connecting.Close(); } catch (SocketException) { } }
            _connecting = null;
            _state = State.Idle;
            _nextTry = now + _cfg.ReconnectMs;
        }

        /// <summary>Closes a control connection gracefully, after an optional bye.</summary>
        private void Retire(Ctl ctl, string bye, long now)
        {
            if (bye != null)
            {
                ctl.Queue(new Control.Bye(bye));
                LinesSent++;
            }
            var c = new Closing(ctl, now);
            if (c.Pump(now)) _closing.Add(c);
        }

        private void PollControl(long now)
        {
            if (_state != State.Handshake && _state != State.Live) return;
            Ctl ctl = _ctl;
            string err = ctl.Flush();
            if (err != null) { End(err, false); return; }
            ctl.Fill();
            while (true)
            {
                string line = ctl.NextLine();
                if (line == null)
                {
                    if (ctl.Fatal != null) { End(ctl.Fatal, true); return; } // a protocol error: tell the peer (CONTRACT 8, 6.1)
                    break;
                }
                LinesReceived++;
                string reason;
                bool bye;
                if (OnLine(line, now, out reason, out bye)) { End(reason, bye); return; }
                if (_state == State.Idle) return;
            }
            // A bye was handled above (it ends the session first), so an end of stream or a reset that follows a bye never reaches
            // this point as a failure.
            if (ctl.End != null) End(ctl.End, false);
        }

        /// <summary>Handles one control line; true ends the session (<paramref name="bye"/>: send a bye first).</summary>
        private bool OnLine(string line, long now, out string reason, out bool bye)
        {
            reason = null; bye = false;
            Control msg;
            try { msg = Control.Parse(line); }
            catch (WireException e) { reason = "protocol error: " + e.Message; bye = true; return true; }
            if (_state == State.Live) _lastHeard = now;
            var b = msg as Control.Bye;
            if (b != null) { reason = "peer said bye: " + b.Reason; return true; }
            if (_state == State.Handshake)
            {
                var h = msg as Control.Hello;
                if (h == null) { reason = "expected hello"; bye = true; return true; }
                reason = OnHello(h, now);
                bye = reason != null;
                return bye;
            }
            if (msg is Control.Hello) { reason = "second hello"; bye = true; return true; }
            var sp = msg as Control.Spawn;
            if (sp != null)
            {
                // A second spawn of a live id replaces the first; a new id needs a free place.
                if (!_entities.ContainsKey(sp.Entity))
                {
                    if (_entities.Count >= Wire.MaxEntities)
                    {
                        SpawnIgnored++;
                        if (!_spawnIgnoredLogged)
                        {
                            _spawnIgnoredLogged = true;
                            Console.Error.WriteLine("RewriteLink: the peer has " + Wire.MaxEntities + " live entities; further spawns are ignored (entity "
                                + sp.Entity + " and later ones)");
                        }
                        return false;
                    }
                    _entities[sp.Entity] = 0;
                }
                _events.Add(new LinkEvent.Spawn(sp));
                return false;
            }
            var d = msg as Control.Despawn;
            if (d != null)
            {
                if (_entities.Remove(d.Entity)) _events.Add(new LinkEvent.Despawn(d.Entity));
                return false;
            }
            var inp = msg as Control.Input;
            if (inp != null)
            {
                bool requested = _role == Role.Host;
                if (!requested) _focus = inp.Focus;
                _events.Add(new LinkEvent.Focus(inp.Focus, requested));
                return false;
            }
            var fc = msg as Control.FrameChannel;
            if (fc != null)
            {
                _lastFrame = 0; // a new pixel file numbers its frames from 1 again (CONTRACT.md 8)
                _events.Add(new LinkEvent.FrameChannel(fc));
            }
            return false; // an Unknown type is ignored
        }

        /// <summary>Returns null on success, else the reason the handshake failed.</summary>
        private string OnHello(Control.Hello h, long now)
        {
            if (h.Role != Control.Other(_role)) return "peer claims role " + Control.RoleName(h.Role);
            if (h.UdpPort == 0) return "peer announced udp_port 0";
            if (h.HeartbeatMs < Wire.MinHeartbeatMs || h.HeartbeatMs > Wire.MaxHeartbeatMs)
                return "peer announced heartbeat_ms " + h.HeartbeatMs + ", outside " + Wire.MinHeartbeatMs + ".." + Wire.MaxHeartbeatMs;
            uint protocol, session;
            if (_role == Role.Host)
            {
                int v = Wire.Negotiate(Wire.MinProtocolVersion, Wire.ProtocolVersion, (int)h.MinProtocol, (int)h.Protocol);
                if (v < 0)
                    return "unsupported protocol: peer " + h.MinProtocol + ".." + h.Protocol + ", ours " + Wire.MinProtocolVersion + ".." + Wire.ProtocolVersion;
                protocol = (uint)v;
                var r = new byte[4];
                using (var rng = RandomNumberGenerator.Create())
                {
                    do { rng.GetBytes(r); session = Wire.Get32(r, 0); } while (session == 0);
                }
            }
            else
            {
                if (h.Protocol < Wire.MinProtocolVersion || h.Protocol > Wire.ProtocolVersion) return "host chose unsupported protocol " + h.Protocol;
                if (!h.Session.HasValue || h.Session.Value == 0) return "host hello without a nonzero session";
                protocol = h.Protocol;
                session = h.Session.Value;
            }
            long timeout = Math.Max(_cfg.TimeoutMs, 4L * h.HeartbeatMs);
            if (_role == Role.Host)
            {
                _ctl.Queue(MakeHello(protocol, session));
                LinesSent++;
                _ctl.Flush();
            }
            ResetSession();
            _peer = new PeerInfo
            {
                Game = h.Game, Role = h.Role, Protocol = (int)protocol, Session = session, UdpPort = (int)h.UdpPort,
                HeartbeatMs = h.HeartbeatMs, TimeoutMs = timeout,
            };
            _peerEp = new IPEndPoint(IPAddress.Loopback, (int)h.UdpPort);
            _timeoutMs = timeout;
            _state = State.Live;
            _lastHeard = now;
            _lastBeat = now;
            _events.Add(new LinkEvent.Connected(_peer));
            SendHeartbeat();
            return null;
        }

        private void ResetSession()
        {
            _focus = Role.Host;
            _hasLastCamera = false;
            _entities.Clear();
            _spawnIgnoredLogged = false;
            _cq.Clear();
            _cqNext = 0;
            _hasLastCollision = false;
            _assembly = null;
            _lastFrame = 0;
        }

        /// <summary>Ends the current connection, reporting it as lost (or as a failed handshake).</summary>
        private void End(string reason, bool bye)
        {
            long now = Now();
            bool wasLive = _state == State.Live;
            Ctl ctl = _ctl;
            _ctl = null;
            _state = State.Idle;
            _nextTry = _role == Role.Guest ? now + _cfg.ReconnectMs : now;
            if (ctl != null) Retire(ctl, bye ? reason : null, now);
            ResetSession();
            _events.Add(wasLive ? (LinkEvent)new LinkEvent.Lost(reason) : new LinkEvent.HandshakeFailed(reason));
        }

        private void PollUdp(long now)
        {
            for (int n = 0; n < 4096; n++)
            {
                int len;
                try
                {
                    if (!_udp.Poll(0, SelectMode.SelectRead)) break;
                    len = _udp.ReceiveFrom(_rx, 0, _rx.Length, SocketFlags.None, ref _from);
                }
                catch (SocketException) { continue; } // e.g. ICMP port-unreachable from an earlier send, or a datagram too big for the buffer
                catch (ObjectDisposedException) { break; }
                if (_state != State.Live) { Foreign++; continue; }
                var src = _from as IPEndPoint;
                if (src == null || !IsPeerAddress(src, _peerEp)) { Foreign++; continue; }
                Wire.Header h;
                Wire.Decoded d;
                try
                {
                    int kind;
                    h = Wire.DecodeHeader(_rx, len, out kind);
                    if (h.Session != _peer.Session || h.Version != _peer.Protocol) { Foreign++; continue; }
                    d = Wire.Decode(_rx, len);
                }
                catch (WireException) { Malformed++; continue; }
                _lastHeard = now;
                DatagramsReceived++;
                OnDatagram(d.Header, d.Body);
            }
        }

        /// <summary>Exactly 127.0.0.1 and the peer's announced UDP port (CONTRACT.md 2): not another loopback address, not ::1.</summary>
        internal static bool IsPeerAddress(IPEndPoint from, IPEndPoint peer)
        {
            return from != null && from.Port == peer.Port && from.AddressFamily == AddressFamily.InterNetwork
                && from.Address.Equals(IPAddress.Loopback);
        }

        private void OnDatagram(Wire.Header header, Wire.Datagram body)
        {
            var cam = body as Wire.CameraMsg;
            if (cam != null)
            {
                if (_hasLastCamera && !Wire.SeqNewer(header.Seq, _lastCamera)) { Stale++; return; }
                _hasLastCamera = true;
                _lastCamera = header.Seq;
                _events.Add(new LinkEvent.Camera(header, cam.Camera));
                return;
            }
            var pm = body as Wire.PoseMsg;
            if (pm != null)
            {
                var fresh = new List<Wire.Pose>(pm.Poses.Count);
                foreach (Wire.Pose pose in pm.Poses)
                {
                    ulong last;
                    if (!_entities.TryGetValue(pose.Entity, out last)) { UnknownEntity++; continue; } // never spawned (or despawned): no state is kept
                    if (pose.TimeUs >= last) { _entities[pose.Entity] = pose.TimeUs; fresh.Add(pose); }
                    else Stale++;
                }
                if (fresh.Count > 0) _events.Add(new LinkEvent.Poses(header, fresh));
                return;
            }
            var cm = body as Wire.CollisionMsg;
            if (cm != null) { OnCollision(cm.Chunk); return; }
            var fm = body as Wire.FrameMsg;
            if (fm != null)
            {
                if (fm.Frame.Frame <= _lastFrame) { Stale++; return; }
                _lastFrame = fm.Frame.Frame;
                _events.Add(new LinkEvent.Frame(header, fm.Frame));
            }
            // A Heartbeat or an Unknown kind only counts as the peer being alive (done by the caller).
        }

        private void OnCollision(Wire.CollisionChunk c)
        {
            if (_hasLastCollision && !Wire.SeqNewer(c.Query, _lastCollision)) { Stale++; return; }
            if (_assembly != null && _assembly.Query == c.Query)
            {
                if (_assembly.Count != c.ChunkCount) { Malformed++; return; }
            }
            else if (_assembly != null && Wire.SeqNewer(_assembly.Query, c.Query)) { Stale++; return; }
            else
            {
                // a newer query starts: the incomplete older one is discarded
                _assembly = new Assembly
                {
                    Query = c.Query, Count = c.ChunkCount, Center = c.Center, Radius = c.Radius,
                    Chunks = new float[c.ChunkCount][], ChunkTriangles = new int[c.ChunkCount],
                };
            }
            Assembly a = _assembly;
            if (a.Chunks[c.ChunkIndex] == null)
            {
                if (a.Triangles + c.TriangleCount > _cfg.MaxCollisionTriangles)
                {
                    Malformed++; // more than this receiver accepts: the snapshot is dropped, and so are the rest of its chunks
                    _assembly = null;
                    _hasLastCollision = true;
                    _lastCollision = c.Query;
                    return;
                }
                a.Chunks[c.ChunkIndex] = c.Triangles;
                a.ChunkTriangles[c.ChunkIndex] = c.TriangleCount;
                a.Triangles += c.TriangleCount;
                a.Received++;
            }
            if (a.Received == a.Count)
            {
                _assembly = null;
                _hasLastCollision = true;
                _lastCollision = a.Query;
                var all = new float[9 * a.Triangles];
                int at = 0;
                for (int i = 0; i < a.Count; i++)
                {
                    Buffer.BlockCopy(a.Chunks[i], 0, all, at * 4, 9 * a.ChunkTriangles[i] * 4);
                    at += 9 * a.ChunkTriangles[i];
                }
                _events.Add(new LinkEvent.Collision(a.Query, a.Center, a.Radius, all, (int)a.Triangles));
            }
        }

        private void PollTimers(long now)
        {
            if (_state == State.Handshake && now - _since > _cfg.TimeoutMs)
            {
                End("no hello before the timeout", true);
            }
            else if (_state == State.Live)
            {
                long silent = now - _lastHeard;
                if (silent > _timeoutMs) { End("watchdog: nothing heard for " + silent + " ms", true); return; }
                if (now - _lastBeat >= _heartbeatMs)
                {
                    _lastBeat = now;
                    SendHeartbeat();
                }
            }
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Sends one datagram to the peer and returns its seq.</summary>
        private uint Send(Wire.Datagram body)
        {
            if (_state != State.Live) throw new NotConnectedException();
            var h = new Wire.Header(_peer.Protocol, 0, _peer.Session, _seq, Wire.NowUs());
            int len = Wire.EncodeInto(_tx, h, body);
            uint seq = _seq++;
            try
            {
                _udp.SendTo(_tx, 0, len, SocketFlags.None, _peerEp);
                DatagramsSent++;
            }
            catch (SocketException) { SendDropped++; } // refused or would block: the next tick replaces it
            catch (ObjectDisposedException) { SendDropped++; }
            return seq;
        }

        private void SendHeartbeat()
        {
            try { Send(new Wire.HeartbeatMsg()); } catch (WireException) { }
        }

        /// <summary>Sends the camera; returns the datagram's seq (a guest quotes it in <see cref="Wire.FrameReady.CameraSeq"/>).</summary>
        public uint SendCamera(Wire.Camera camera)
        {
            lock (_gate) { return Send(new Wire.CameraMsg(camera)); }
        }

        /// <summary>Sends poses, at most <see cref="Wire.MaxPoses"/> per datagram. The entity must be spawned first.</summary>
        public void SendPoses(IList<Wire.Pose> poses)
        {
            lock (_gate)
            {
                if (_state != State.Live) throw new NotConnectedException();
                for (int i = 0; i < poses.Count; i += Wire.MaxPoses)
                {
                    int n = Math.Min(Wire.MaxPoses, poses.Count - i);
                    var part = new Wire.Pose[n];
                    for (int k = 0; k < n; k++) part[k] = poses[i + k];
                    Send(new Wire.PoseMsg(part));
                }
            }
        }

        /// <summary>Queues a collision snapshot (<paramref name="triangles"/> holds 9 floats per triangle) and returns its query number.</summary>
        public uint SendCollision(float[] center, float radius, float[] triangles)
        {
            return SendCollision(center, radius, triangles, triangles.Length / 9);
        }

        /// <summary>
        /// Queues a collision snapshot in as many chunks as it needs and returns its query number. The chunks go out
        /// <see cref="Wire.CollisionPerPoll"/> per <see cref="Poll()"/>, so a big snapshot never overflows the receiver's socket buffer; a snapshot
        /// queued while an older one still has chunks waiting replaces them (the receiver would discard the old query anyway). The triangles are copied.
        /// A snapshot of more than <see cref="Wire.MaxCollisionChunks"/> chunks (more than 925696 triangles) is refused with an <see cref="ArgumentException"/>, before
        /// anything is copied; the caller keeps the triangles nearest to <paramref name="center"/> and sends those.
        /// </summary>
        public uint SendCollision(float[] center, float radius, float[] triangles, int triangleCount)
        {
            lock (_gate)
            {
                if (_state != State.Live) throw new NotConnectedException();
                if (center == null || center.Length != 3) throw new ArgumentException("center needs 3 floats");
                if (Wire.ChunkCount(triangleCount) > Wire.MaxCollisionChunks)
                    throw new ArgumentException("too many triangles: a snapshot has at most " + Wire.MaxCollisionChunks + " chunks (" + Wire.MaxCollisionChunks * Wire.MaxTriangles + " triangles)");
                if (triangleCount < 0 || triangles.Length < 9 * (long)triangleCount) throw new ArgumentException("triangles holds fewer than triangleCount triangles");
                uint query = _nextQuery;
                List<Wire.CollisionChunk> chunks = Wire.Chunks(query, (float[])center.Clone(), radius, triangles, triangleCount);
                _cq = chunks;
                _cqNext = 0;
                _nextQuery++;
                return query;
            }
        }

        /// <summary>Sends up to the burst of queued collision chunks (32, fewer if the OS granted a small receive buffer).</summary>
        private void PumpCollision()
        {
            for (int i = 0; i < _collisionBurst && _cqNext < _cq.Count; i++)
            {
                try { Send(new Wire.CollisionMsg(_cq[_cqNext++])); }
                catch (NotConnectedException) { _cq.Clear(); _cqNext = 0; return; }
            }
            if (_cqNext >= _cq.Count) { _cq.Clear(); _cqNext = 0; }
        }

        public void SendFrame(Wire.FrameReady frame)
        {
            lock (_gate) { Send(new Wire.FrameMsg(frame)); }
        }

        public void Spawn(Control.Spawn spawn)
        {
            lock (_gate) { SendControl(spawn); }
        }

        public void Spawn(uint entity, string provider, string asset, string name)
        {
            Spawn(new Control.Spawn(entity, provider, asset, name));
        }

        public void Despawn(uint entity)
        {
            lock (_gate) { SendControl(new Control.Despawn(entity)); }
        }

        /// <summary>Host: gives the focus to <paramref name="focus"/>. Guest: asks the host for it.</summary>
        public void SetFocus(Role focus)
        {
            lock (_gate)
            {
                SendControl(new Control.Input(focus, null));
                if (_role == Role.Host) _focus = focus;
            }
        }

        /// <summary>Tells the peer about a pixel file (the <c>frame_channel</c> line of CONTRACT.md section 8).</summary>
        public void AnnounceFrames(Control.FrameChannel channel)
        {
            lock (_gate) { SendControl(channel); }
        }

        /// <summary>Tells the peer about the file a <see cref="PixelFile.Writer"/> fills.</summary>
        public void AnnounceFrames(PixelFile.Writer writer)
        {
            AnnounceFrames(new Control.FrameChannel(writer.Path, (uint)writer.Width, (uint)writer.Height, (uint)writer.Slots));
        }

        private void SendControl(Control c)
        {
            if (_state != State.Live) throw new NotConnectedException();
            if (!_ctl.Queue(c)) throw new WireException("control line too long");
            LinesSent++;
            string err = _ctl.Flush();
            if (err != null)
            {
                End(err, false);
                throw new NotConnectedException();
            }
        }

        /// <summary>Ends the session with a bye (the caller knows; no <see cref="LinkEvent.Lost"/> is reported for it).</summary>
        public void Disconnect(string reason)
        {
            lock (_gate)
            {
                if (_state == State.Handshake || _state == State.Live)
                {
                    End(reason ?? "", true);
                    _events.RemoveAt(_events.Count - 1);
                }
            }
        }

        /// <summary>Says bye and closes gracefully; may block for up to the 200 ms linger.</summary>
        public void Close() { Dispose(); }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                long now = Now();
                if (_state == State.Connecting && _connecting != null) { try { _connecting.Close(); } catch (SocketException) { } }
                if ((_state == State.Handshake || _state == State.Live) && _ctl != null) Retire(_ctl, "closing", now);
                _ctl = null;
                _state = State.Idle;
                long stop = now + Wire.LingerMs + 50;
                while (_closing.Count > 0 && Now() < stop)
                {
                    Thread.Sleep(2);
                    long t = Now();
                    for (int i = _closing.Count - 1; i >= 0; i--) if (!_closing[i].Pump(t)) _closing.RemoveAt(i);
                }
                foreach (Closing c in _closing) { try { c.Sock.Close(); } catch (SocketException) { } }
                _closing.Clear();
                try { _udp.Close(); if (_listener != null) _listener.Close(); } catch (SocketException) { }
                _closed = true;
            }
        }
    }
}
