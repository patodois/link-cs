// A command-line stand-in host or guest for RewriteLink, the C# twin of link-java's LinkDemo, link-c's demo and the Rust fake_host /
// fake_guest examples. Used for the interop tests.
//
//   dotnet LinkDemo.dll host  [--port 47800] [--seconds 6] [--frames] [--big-collision 400]
//   dotnet LinkDemo.dll guest [--port 47800] [--seconds 6] [--frames] [--orbit]
//
// guest --frames renders a disc with linear depth into a 320x240 pixel file and announces it for every camera it gets; the host reads
// such frames and reports what is in them. host --big-collision N sends a snapshot of N full chunks each second instead of the small
// ground patch. guest --orbit circles the camera at 8 m instead of floating 3 m ahead of it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Patodois.RewriteLink.Demo
{
    internal static class Program
    {
        private const int FrameW = 320, FrameH = 240;
        private static System.Diagnostics.Stopwatch _clock;
        private static bool _orbit, _frames;
        private static int _bigCollision;

        private static double Elapsed() { return _clock.Elapsed.TotalSeconds; }

        private static void Log(string role, string msg)
        {
            Console.WriteLine("[cs-" + role + " " + Elapsed().ToString("F2", CultureInfo.InvariantCulture).PadLeft(6) + "s] " + msg);
            Console.Out.Flush();
        }

        private static string Arg(string[] args, string name, string def)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return def;
        }

        private static string F(double v, string fmt = "G6") { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        public static int Main(string[] args)
        {
            if (args.Length < 1 || (args[0] != "host" && args[0] != "guest"))
            {
                Console.Error.WriteLine("usage: LinkDemo host|guest [--port N] [--seconds S] [--frames] [--orbit] [--big-collision N]");
                return 2;
            }
            bool host = args[0] == "host";
            int port = int.Parse(Arg(args, "--port", "47800"), CultureInfo.InvariantCulture);
            double seconds = double.Parse(Arg(args, "--seconds", "0"), CultureInfo.InvariantCulture);
            _orbit = args.Contains("--orbit");
            _frames = args.Contains("--frames");
            _bigCollision = int.Parse(Arg(args, "--big-collision", "0"), CultureInfo.InvariantCulture);
            _clock = System.Diagnostics.Stopwatch.StartNew();
            var cfg = new LinkConfig(host ? "C# Host" : "C# Guest") { ControlPort = port };
            string name = host ? "host" : "guest";
            using (Link link = host ? Link.Host(cfg) : Link.Guest(cfg))
            {
                if (host) Log(name, "listening on 127.0.0.1:" + link.ControlPort); else Log(name, "connecting to 127.0.0.1:" + port);
                if (host) RunHost(link, seconds); else RunGuest(link, seconds);
                Log(name, "done");
            }
            return 0;
        }

        // ------------------------------------------------------------------------------------------ host

        private static void RunHost(Link link, double seconds)
        {
            int cameras = 0, poseMsgs = 0, poses = 0, framesRead = 0, framesMissed = 0;
            string lastFrame = "none";
            PixelFile.Reader reader = null;
            float[] lastPose = null;
            double nextReport = 1, nextCollision = 0, giveBackAt = -1;
            var events = new List<LinkEvent>();
            while (true)
            {
                double t = Elapsed();
                if (seconds > 0 && t > seconds) break;
                events.Clear();
                link.Poll(events);
                foreach (LinkEvent e in events)
                {
                    switch (e)
                    {
                        case LinkEvent.Connected c:
                            Log("host", "connected: guest \"" + c.Peer.Game + "\", protocol v" + c.Peer.Protocol + ", session 0x" + c.Peer.Session.ToString("x8")
                                + ", guest udp " + c.Peer.UdpPort);
                            nextCollision = t;
                            break;
                        case LinkEvent.FrameChannel fc:
                            reader?.Dispose();
                            reader = null;
                            try
                            {
                                reader = PixelFile.Reader.Open(fc.Value.Path);
                                Log("host", "guest pixel file " + fc.Value.Path + " (" + fc.Value.Width + "x" + fc.Value.Height + ", " + fc.Value.Slots + " slots, depth plane: "
                                    + (reader.HasDepth ? "true" : "false") + ")");
                            }
                            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                            {
                                Log("host", "cannot open the guest's pixel file: " + ex.Message);
                            }
                            break;
                        case LinkEvent.Frame fr:
                        {
                            PixelFile.Frame f = reader?.Read(fr.Value.Frame, (int)fr.Value.Slot, true);
                            if (f == null) framesMissed++;
                            else { framesRead++; lastFrame = Describe(f); }
                            break;
                        }
                        case LinkEvent.Lost l:
                            reader?.Dispose();
                            reader = null;
                            Log("host", "LOST the guest: " + l.Reason);
                            break;
                        case LinkEvent.HandshakeFailed h: Log("host", "handshake failed: " + h.Reason); break;
                        case LinkEvent.Spawn sp:
                            Log("host", "guest spawned entity " + sp.Value.Entity + " = " + sp.Value.Provider + " asset \"" + sp.Value.Asset + "\"");
                            break;
                        case LinkEvent.Despawn d: Log("host", "guest despawned entity " + d.Entity); break;
                        case LinkEvent.Poses p:
                            poseMsgs++;
                            poses += p.Values.Count;
                            lastPose = p.Values[p.Values.Count - 1].Translation;
                            break;
                        case LinkEvent.Focus fo when fo.Requested:
                            Log("host", "guest asks for input focus; granting it to the " + Control.RoleName(fo.Value));
                            link.SetFocus(fo.Value);
                            giveBackAt = t + 2;
                            break;
                    }
                }
                if (link.IsConnected)
                {
                    double a = 0.5 * t;
                    var eye = new float[] { (float)(10 * Math.Cos(a)), 3, (float)(10 * Math.Sin(a)) };
                    link.SendCamera(Wire.Camera.LookAt(eye, new float[] { 0, 1, 0 }, 1.0f, 0.1f, 1000f, 1280, 720));
                    cameras++;
                    if (t >= nextCollision)
                    {
                        nextCollision = t + 1;
                        float cx = (float)Math.Round(eye[0]), cz = (float)Math.Round(eye[2]);
                        float[] tris;
                        if (_bigCollision > 0)
                        {
                            int n = _bigCollision * Wire.MaxTriangles;
                            tris = new float[9 * n];
                            for (int i = 0; i < n; i++)
                            {
                                float x = i % 4096;
                                Array.Copy(new float[] { x, 0, 0, x + 1, 0, 0, x, 0, 1 }, 0, tris, 9 * i, 9);
                            }
                        }
                        else
                        {
                            var list = new List<float>();
                            for (int i = -10; i < 10; i++)
                                for (int j = -10; j < 10; j++)
                                {
                                    float x = cx + i, z = cz + j;
                                    list.AddRange(new float[] { x, 0, z, x, 0, z + 1, x + 1, 0, z + 1 });
                                    list.AddRange(new float[] { x, 0, z, x + 1, 0, z + 1, x + 1, 0, z });
                                }
                            tris = list.ToArray();
                        }
                        link.SendCollision(new float[] { cx, 0, cz }, 10, tris);
                    }
                    if (giveBackAt >= 0 && t >= giveBackAt)
                    {
                        giveBackAt = -1;
                        Log("host", "taking input focus back");
                        link.SetFocus(Role.Host);
                    }
                }
                if (t >= nextReport)
                {
                    nextReport += 1;
                    if (link.IsConnected)
                    {
                        Log("host", "sent " + cameras + " cameras; got " + poses + " poses in " + poseMsgs + " datagrams; last pose "
                            + (lastPose == null ? "none" : "[" + F(lastPose[0]) + ", " + F(lastPose[1]) + ", " + F(lastPose[2]) + "]")
                            + "; malformed " + link.Malformed + " stale " + link.Stale + " foreign " + link.Foreign + " unknown_entity " + link.UnknownEntity);
                        if (framesRead + framesMissed > 0) Log("host", "frames read " + framesRead + ", missed " + framesMissed + "; last: " + lastFrame);
                    }
                    cameras = poseMsgs = poses = framesRead = framesMissed = 0;
                }
                Thread.Sleep(16);
            }
            reader?.Dispose();
        }

        /// <summary>What the host can tell about a frame it read: its size, camera and how much of it is drawn.</summary>
        private static string Describe(PixelFile.Frame f)
        {
            int opaque = 0, finite = 0;
            float near = float.PositiveInfinity, far = 0;
            for (int i = 0; i < f.Width * f.Height; i++)
            {
                if (f.Rgba[i * 4 + 3] > 0) opaque++;
                if (f.Depth != null && !float.IsInfinity(f.Depth[i]) && !float.IsNaN(f.Depth[i]))
                {
                    finite++;
                    near = Math.Min(near, f.Depth[i]);
                    far = Math.Max(far, f.Depth[i]);
                }
            }
            return "frame " + f.Number + " " + f.Width + "x" + f.Height + " camera_seq " + f.CameraSeq + ", " + opaque + " drawn pixels, " + finite + " with finite depth"
                + (finite > 0 ? " (" + near.ToString("F2", CultureInfo.InvariantCulture) + ".." + far.ToString("F2", CultureInfo.InvariantCulture) + " m)" : "");
        }

        // ----------------------------------------------------------------------------------------- guest

        private static void RunGuest(Link link, double seconds)
        {
            const uint cube = 1;
            int cameras = 0, posesSent = 0, framesSent = 0;
            double nextReport = 1, askFocusAt = -1;
            float[] lastPos = null;
            ulong lastT = 0;
            PixelFile.Writer writer = null;
            var rgba = new byte[FrameW * FrameH * 4];
            var depth = new float[FrameW * FrameH];
            var events = new List<LinkEvent>();
            while (true)
            {
                double t = Elapsed();
                if (seconds > 0 && t > seconds) break;
                events.Clear();
                link.Poll(events);
                foreach (LinkEvent e in events)
                {
                    switch (e)
                    {
                        case LinkEvent.Connected c:
                            Log("guest", "connected: host \"" + c.Peer.Game + "\", protocol v" + c.Peer.Protocol + ", session 0x" + c.Peer.Session.ToString("x8"));
                            link.Spawn(cube, "C# Guest", "demo/cube", "Cube");
                            askFocusAt = t + 2;
                            lastPos = null;
                            if (_frames)
                            {
                                writer?.Dispose();
                                writer = PixelFile.Writer.CreateTemp("cs-guest-" + c.Peer.Session.ToString("x8"), FrameW, FrameH, 3, true);
                                link.AnnounceFrames(writer);
                                Log("guest", "pixel file " + writer.Path + " (" + FrameW + "x" + FrameH + ", depth: true)");
                            }
                            break;
                        case LinkEvent.Lost l:
                            writer?.Dispose();
                            writer = null;
                            Log("guest", "LOST the host: " + l.Reason);
                            break;
                        case LinkEvent.HandshakeFailed h: Log("guest", "handshake failed: " + h.Reason); break;
                        case LinkEvent.Camera cam:
                        {
                            cameras++;
                            float[] eye = cam.Value.Eye(), fwd = cam.Value.Forward();
                            float[] pos = { eye[0] + 3 * fwd[0], eye[1] + 3 * fwd[1], eye[2] + 3 * fwd[2] };
                            ulong now = Wire.NowUs();
                            Wire.Pose pose = Wire.Pose.Identity(cube, now);
                            if (_orbit)
                            {
                                double a = 0.6 * t;
                                pos = new float[] { eye[0] + (float)(8 * Math.Cos(a)), eye[1], eye[2] + (float)(8 * Math.Sin(a)) };
                                // The front (+Z) points along the direction of travel.
                                pose.Rotation = Quat.FromTo(new float[] { 0, 0, 1 }, new float[] { (float)-Math.Sin(a), 0, (float)Math.Cos(a) });
                            }
                            Array.Copy(pos, pose.Translation, 3);
                            if (lastPos != null)
                            {
                                float dt = (now - lastT) * 1e-6f;
                                if (dt > 0) for (int i = 0; i < 3; i++) pose.Velocity[i] = (pos[i] - lastPos[i]) / dt;
                            }
                            lastPos = pos;
                            lastT = now;
                            link.SendPoses(new[] { pose });
                            posesSent++;
                            if (writer != null)
                            {
                                Render(rgba, depth, framesSent);
                                ulong number = writer.Write(rgba, depth, FrameW, FrameH, cam.Header.Seq, Wire.NowUs());
                                link.SendFrame(new Wire.FrameReady(number, (uint)(number % (ulong)writer.Slots), cam.Header.Seq, FrameW, FrameH));
                                framesSent++;
                            }
                            break;
                        }
                        case LinkEvent.Collision col:
                            Log("guest", "collision query " + col.Query + ": " + col.TriangleCount + " triangles within " + F(col.Radius) + " m of [" + F(col.Center[0]) + ", "
                                + F(col.Center[1]) + ", " + F(col.Center[2]) + "]");
                            break;
                        case LinkEvent.Focus fo:
                            Log("guest", "host says input focus is now with the " + Control.RoleName(fo.Value));
                            break;
                    }
                }
                if (askFocusAt >= 0 && t >= askFocusAt)
                {
                    askFocusAt = -1;
                    Log("guest", "asking the host for input focus");
                    link.SetFocus(Role.Guest);
                }
                if (t >= nextReport)
                {
                    nextReport += 1;
                    if (link.IsConnected)
                        Log("guest", "got " + cameras + " cameras; sent " + posesSent + " poses, " + framesSent + " frames; focus with the " + Control.RoleName(link.Focus));
                    cameras = posesSent = framesSent = 0;
                }
                Thread.Sleep(4);
            }
            writer?.Dispose();
        }

        /// <summary>A coloured disc, 3 m away, on nothing: transparent with depth +inf outside it.</summary>
        private static void Render(byte[] rgba, float[] depth, int n)
        {
            int cx = FrameW / 2 + (n % 40) - 20, cy = FrameH / 2, r = 60;
            for (int y = 0; y < FrameH; y++)
                for (int x = 0; x < FrameW; x++)
                {
                    int i = y * FrameW + x;
                    bool inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
                    rgba[i * 4] = (byte)(inside ? 40 + x / 2 : 0);
                    rgba[i * 4 + 1] = (byte)(inside ? 120 : 0);
                    rgba[i * 4 + 2] = (byte)(inside ? 200 - y / 3 : 0);
                    rgba[i * 4 + 3] = (byte)(inside ? 255 : 0);
                    depth[i] = inside ? 3f : float.PositiveInfinity;
                }
        }
    }
}
