using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using static Patodois.RewriteLink.Tests.T;

namespace Patodois.RewriteLink.Tests
{
    /// <summary>
    /// Runs the shared link test vectors (tests/vectors/link, see the README there) against this library: control lines, datagrams, pixel files
    /// and scripted sessions over real sockets. The vectors are derived from CONTRACT.md alone and are the files the Rust, Java and C
    /// implementations run too, so a vector this runner fails is a place where this library and the contract (or another implementation)
    /// disagree. Every failing vector is listed together at the end of its file, with its id, section and basis.
    ///
    /// <see cref="KnownDisagreements"/> names the vectors this library is known to fail, with the reason. They are reported but do not fail the
    /// run; a known vector that now passes does, so the list cannot go stale.
    ///
    /// RBL_VECTORS_DIR overrides the directory; RBL_VECTORS_SKIP_LARGE=1 skips the cases that need a sparse file of up to 1 GiB.
    /// </summary>
    internal static class VectorTests
    {
        /// <summary>
        /// The SHA-256 of tests/vectors/link/SHA256SUMS. A vector can only change together with a deliberate edit of this constant. To update
        /// the vectors: run scripts/sync-link-vectors.sh REPO_DIR in the rewrite-bridge repository (it copies the directory and prints
        /// "VECTORS_DIGEST = ..."), paste that value here, then run the tests and sort out the new or changed vectors.
        /// </summary>
        private const string VectorsDigest = "815d4f09e6d15f17dfcfedcf96865c5cfd60e170d3f71fb97f3adc0edc200fd9";

        /// <summary>Vectors this library fails, id -> why. Empty when it passes them all.</summary>
        private static readonly Dictionary<string, string> KnownDisagreements = new Dictionary<string, string>
        {
        };

        private static string _dir;
        private static Dictionary<string, Dictionary<string, object>> _datagramCases;

        public static void Run()
        {
            Test("vectors: files match SHA256SUMS and the pinned digest", CheckSums);
            Test("vectors: control lines (control.json)", ControlVectors);
            Test("vectors: datagrams (datagrams.json)", DatagramVectors);
            Test("vectors: pixel files, readers (pixelfiles.json)", PixelReaderVectors);
            Test("vectors: pixel files, writers (pixelfiles.json)", PixelWriterVectors);
            Test("vectors: scripted sessions (sessions.json)", SessionVectors);
        }

        // ------------------------------------------------------------------------------------------ loading

        /// <summary>Raised for a problem in the runner or in the vector files, not for a disagreement of the library.</summary>
        private sealed class RunnerBug : Exception
        {
            public RunnerBug(string m) : base(m) { }
        }

        private static string Dir()
        {
            if (_dir != null) return _dir;
            string env = Environment.GetEnvironmentVariable("RBL_VECTORS_DIR");
            if (!string.IsNullOrEmpty(env))
            {
                if (!File.Exists(Path.Combine(env, "SHA256SUMS"))) throw new RunnerBug("RBL_VECTORS_DIR has no SHA256SUMS: " + env);
                return _dir = Path.GetFullPath(env);
            }
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    string cand = Path.Combine(d.FullName, "tests", "vectors", "link");
                    if (File.Exists(Path.Combine(cand, "SHA256SUMS"))) return _dir = cand;
                }
            }
            throw new RunnerBug("tests/vectors/link not found from " + Directory.GetCurrentDirectory() + " or " + AppContext.BaseDirectory + " (set RBL_VECTORS_DIR)");
        }

        private static List<object> Load(string name)
        {
            string path = Path.Combine(Dir(), name);
            var doc = J.Parse(File.ReadAllText(path, new UTF8Encoding(false, true))) as Dictionary<string, object>;
            if (doc == null || !"rbl-link-vectors/1".Equals(J.Get(doc, "format"))) throw new RunnerBug(name + ": unknown format");
            return (List<object>)doc["cases"];
        }

        private static IEnumerable<Dictionary<string, object>> Cases(string name)
        {
            return Load(name).Cast<Dictionary<string, object>>();
        }

        private static bool SkipLarge() { return Environment.GetEnvironmentVariable("RBL_VECTORS_SKIP_LARGE") == "1"; }

        private static string Sha256Hex(byte[] data)
        {
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(data));
        }

        private static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return s.ToString();
        }

        private static byte[] Unhex(string s)
        {
            if (s.Length % 2 != 0) throw new RunnerBug("odd hex");
            var o = new byte[s.Length / 2];
            for (int i = 0; i < o.Length; i++) o[i] = Convert.ToByte(s.Substring(2 * i, 2), 16);
            return o;
        }

        // ------------------------------------------------------------------------------------------ reporting

        private sealed class Report
        {
            public int Passed;
            public readonly List<string> Skipped = new List<string>(), Failed = new List<string>(), Known = new List<string>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            /// <summary><paramref name="error"/> is null when the vector passed.</summary>
            public void Record(Dictionary<string, object> c, string error)
            {
                string id = J.Str(c, "id", "?"), basis = J.Str(c, "basis", "MUST"), section = J.Str(c, "section", "");
                string why;
                bool known = KnownDisagreements.TryGetValue(id, out why);
                if (error == null && !known) Passed++;
                else if (error == null) Failed.Add(id + ": now passes, remove it from KnownDisagreements (" + why + ")");
                else if (!known) Failed.Add(id + " [section " + section + ", " + basis + "]: " + error);
                else Known.Add(id + " [section " + section + ", " + basis + "]: " + error + " (known: " + why + ")");
            }

            public void Finish(string what)
            {
                Console.WriteLine("     " + what + ": " + Passed + " passed, " + Known.Count + " known disagreements, " + Skipped.Count + " skipped, " + Failed.Count + " failed (" + _clock.ElapsedMilliseconds + " ms)");
                foreach (string k in Known) Console.WriteLine("       known disagreement: " + k);
                foreach (string s in Skipped) Console.WriteLine("       skipped: " + s);
                foreach (string f in Failed) Console.WriteLine("       FAILED VECTOR " + f);
                if (Failed.Count > 0) throw new Exception(what + ": " + Failed.Count + " vectors failed (listed above)");
            }
        }

        /// <summary>Runs a check that returns an error text, or null when the vector passes; an exception of the library is a failure too.</summary>
        private static string Guard(Func<string> check)
        {
            try { return check(); }
            catch (RunnerBug) { throw; }
            catch (Exception e) { return "exception " + e.GetType().Name + ": " + e.Message; }
        }

        // ------------------------------------------------------------------------------------------ checksums

        private static void CheckSums()
        {
            string dir = Dir();
            byte[] sums = File.ReadAllBytes(Path.Combine(dir, "SHA256SUMS"));
            Eq(Sha256Hex(new byte[0]), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "SHA-256 of the empty string");
            var problems = new List<string>();
            var listed = new HashSet<string> { "SHA256SUMS" };
            foreach (string line in Encoding.ASCII.GetString(sums).Split('\n'))
            {
                if (line.Length == 0) continue;
                int sep = line.IndexOf("  ", StringComparison.Ordinal);
                if (sep != 64) { problems.Add("bad SHA256SUMS line: " + line); continue; }
                string hash = line.Substring(0, 64), name = line.Substring(66);
                listed.Add(name);
                string path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { problems.Add(name + " is listed but missing"); continue; }
                if (Sha256Hex(File.ReadAllBytes(path)) != hash) problems.Add(name + " differs from SHA256SUMS");
            }
            Check(listed.Count > 10, "SHA256SUMS lists too little");
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                if (!listed.Contains(rel)) problems.Add(rel + " is not listed in SHA256SUMS");
            }
            string digest = Sha256Hex(sums);
            if (digest != VectorsDigest)
                problems.Add("the SHA-256 of SHA256SUMS is " + digest + ", this file pins " + VectorsDigest + " (update VectorsDigest together with the vectors)");
            if (problems.Count > 0) throw new Exception(problems.Count + " problems:\n       " + string.Join("\n       ", problems));
        }

        // ------------------------------------------------------------------------------------------ control.json

        private static Control ControlFrom(Dictionary<string, object> m)
        {
            Func<string, string> s = k => J.Str(m, k, null) ?? throw new RunnerBug("no string " + k);
            Func<string, uint> u = k => J.U32(m[k]);
            Func<string, Role> role = k => J.Str(m, k, null) == "host" ? Role.Host : Role.Guest;
            switch (s("type"))
            {
                case "hello":
                    return new Control.Hello
                    {
                        Role = role("role"), Game = s("game"), Protocol = u("protocol"), MinProtocol = u("min_protocol"), UdpPort = u("udp_port"),
                        HeartbeatMs = u("heartbeat_ms"), TimeoutMs = u("timeout_ms"), Session = m.ContainsKey("session") ? u("session") : (uint?)null,
                    };
                case "spawn": return new Control.Spawn(u("entity"), s("provider"), s("asset"), J.Str(m, "name", null));
                case "despawn": return new Control.Despawn(u("entity"));
                case "input": return new Control.Input(role("focus"), J.Str(m, "reason", null));
                case "frame_channel": return new Control.FrameChannel(s("path"), u("width"), u("height"), u("slots"));
                case "bye": return new Control.Bye(s("reason"));
                default: throw new RunnerBug("vector message of type " + s("type"));
            }
        }

        private static string CheckControl(Dictionary<string, object> c)
        {
            string line = J.Str(c, "line", null);
            if (line == null)
            {
                string hex = J.Str(c, "line_hex", null) ?? throw new RunnerBug("no line");
                try { line = Control.Utf8Strict.GetString(Unhex(hex)); }
                catch (ArgumentException) { return null; } // the reader refuses a line that is not UTF-8 before it parses it (sessions.json checks that path)
            }
            Control parsed = null;
            string rejected = null;
            try { parsed = Control.Parse(line); }
            catch (WireException e) { rejected = e.Message; }
            switch (J.Str(c, "expect", null))
            {
                case "reject":
                    return rejected != null ? null : "accepted " + Show(line) + " as " + Show(parsed.ToLine());
                case "unknown":
                {
                    var u = parsed as Control.Unknown;
                    if (u != null && u.Type == J.Str(c, "type", null)) return null;
                    return Show(line) + " gave " + (parsed == null ? "a rejection (" + rejected + ")" : Show(parsed.ToLine())) + ", expected the unknown type " + J.Str(c, "type", null);
                }
                case "message":
                {
                    var message = (Dictionary<string, object>)c["message"];
                    Control want = ControlFrom(message);
                    if (parsed == null) return Show(line) + " was rejected (" + rejected + "), expected " + Show(want.ToLine());
                    if (parsed.GetType() != want.GetType() || parsed.ToLine() != want.ToLine())
                        return Show(line) + " gave " + Show(parsed.ToLine()) + ", expected " + Show(want.ToLine());
                    if (J.IsTrue(c, "encode"))
                    {
                        string o = want.ToLine();
                        if (!o.EndsWith("\n", StringComparison.Ordinal) || o.Substring(0, o.Length - 1).IndexOfAny(new[] { '\n', '\r' }) >= 0)
                            return "encoded line is not a single line: " + Show(o);
                        object back;
                        try { back = J.Parse(o); }
                        catch (J.JsonError e) { return "encoded " + Show(o) + ": " + e.Message; }
                        if (!J.Eq(back, message)) return "encoded " + Show(o) + ", expected the object " + Show(J.Write(message));
                    }
                    return null;
                }
                default: throw new RunnerBug("expect " + J.Str(c, "expect", null));
            }
        }

        private static string Show(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder("\"");
            foreach (char ch in s.Length > 300 ? s.Substring(0, 300) : s)
            {
                if (ch == '\n') b.Append("\\n");
                else if (ch == '\r') b.Append("\\r");
                else if (ch < 0x20 || ch > 0x7e) b.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(ch);
            }
            return b.Append(s.Length > 300 ? "...\" (" + s.Length + " chars)" : "\"").ToString();
        }

        private static void ControlVectors()
        {
            var r = new Report();
            foreach (var c in Cases("control.json")) r.Record(c, Guard(() => CheckControl(c)));
            r.Finish("control.json");
        }

        // ------------------------------------------------------------------------------------------ datagrams.json

        private static Wire.Pose PoseOf(Dictionary<string, object> p)
        {
            return new Wire.Pose
            {
                Entity = J.U32(p["entity"]), Flags = J.U32(p["flags"]), TimeUs = J.U64(p["time_us"]),
                Translation = J.F32s(p["translation"], 3), Rotation = J.F32s(p["rotation"], 4), Scale = J.F32s(p["scale"], 3),
                Velocity = J.F32s(p["velocity"], 3), AngularVelocity = J.F32s(p["angular_velocity"], 3),
            };
        }

        private static Wire.Camera CameraOf(Dictionary<string, object> b)
        {
            return new Wire.Camera(J.F32s(b["view"], 16), J.F32s(b["proj"], 16), J.F32(b["fov_y"]), J.F32(b["near"]), J.F32(b["far"]),
                J.U32(b["viewport_width"]), J.U32(b["viewport_height"]), (Wire.DepthRange)J.U32(b["depth_range"]));
        }

        private static Wire.FrameReady FrameReadyOf(Dictionary<string, object> b)
        {
            return new Wire.FrameReady(J.U64(b["frame"]), J.U32(b["slot"]), J.U32(b["camera_seq"]), J.U32(b["width"]), J.U32(b["height"]));
        }

        private static Wire.Datagram DatagramOf(Dictionary<string, object> b)
        {
            switch (J.Str(b, "kind", null))
            {
                case "heartbeat": return new Wire.HeartbeatMsg();
                case "camera": return new Wire.CameraMsg(CameraOf(b));
                case "pose": return new Wire.PoseMsg(((List<object>)b["poses"]).Select(p => PoseOf((Dictionary<string, object>)p)).ToList());
                case "collision":
                {
                    var tris = ((List<object>)b["triangles"]).SelectMany(t => J.F32s(t, 9)).ToArray();
                    return new Wire.CollisionMsg(new Wire.CollisionChunk
                    {
                        Query = J.U32(b["query"]), ChunkIndex = (int)J.U32(b["chunk_index"]), ChunkCount = (int)J.U32(b["chunk_count"]),
                        Center = J.F32s(b["center"], 3), Radius = J.F32(b["radius"]), Triangles = tris, TriangleCount = tris.Length / 9,
                    });
                }
                case "frame": return new Wire.FrameMsg(FrameReadyOf(b));
                default: throw new RunnerBug("body kind " + J.Str(b, "kind", null));
            }
        }

        private static string Fs(IEnumerable<float> v)
        {
            return "[" + string.Join(",", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture) + (f == 0 && 1 / f < 0 ? "(-0)" : ""))) + "]";
        }

        /// <summary>A text that is equal for two datagram bodies exactly when every field is equal (floats bit for bit up to the sign of zero, which Fs shows).</summary>
        private static string Describe(Wire.Datagram d)
        {
            var b = new StringBuilder();
            var cam = d as Wire.CameraMsg;
            var pose = d as Wire.PoseMsg;
            var col = d as Wire.CollisionMsg;
            var fr = d as Wire.FrameMsg;
            if (d is Wire.HeartbeatMsg) b.Append("heartbeat");
            else if (cam != null)
            {
                Wire.Camera c = cam.Camera;
                b.Append("camera view=" + Fs(c.View) + " proj=" + Fs(c.Proj) + " fov_y=" + Fs(new[] { c.FovY }) + " near=" + Fs(new[] { c.Near }) + " far=" + Fs(new[] { c.Far })
                    + " viewport=" + c.ViewportWidth + "x" + c.ViewportHeight + " depth_range=" + (uint)c.DepthRange);
            }
            else if (pose != null)
            {
                b.Append("pose count=" + pose.Poses.Count);
                foreach (Wire.Pose p in pose.Poses)
                    b.Append(" {entity=" + p.Entity + " flags=" + p.Flags + " time_us=" + p.TimeUs + " t=" + Fs(p.Translation) + " r=" + Fs(p.Rotation) + " s=" + Fs(p.Scale)
                        + " v=" + Fs(p.Velocity) + " w=" + Fs(p.AngularVelocity) + "}");
            }
            else if (col != null)
            {
                Wire.CollisionChunk c = col.Chunk;
                b.Append("collision query=" + c.Query + " chunk=" + c.ChunkIndex + "/" + c.ChunkCount + " center=" + Fs(c.Center) + " radius=" + Fs(new[] { c.Radius })
                    + " triangles(" + c.TriangleCount + ")=" + Fs(c.Triangles.Take(9 * c.TriangleCount)));
            }
            else if (fr != null)
            {
                b.Append("frame frame=" + fr.Frame.Frame + " slot=" + fr.Frame.Slot + " camera_seq=" + fr.Frame.CameraSeq + " " + fr.Frame.Width + "x" + fr.Frame.Height);
            }
            else b.Append("kind " + d.Kind);
            return b.ToString();
        }

        private static string KindName(Wire.Datagram d)
        {
            if (d is Wire.HeartbeatMsg) return "heartbeat";
            if (d is Wire.CameraMsg) return "camera";
            if (d is Wire.PoseMsg) return "pose";
            if (d is Wire.CollisionMsg) return "collision";
            if (d is Wire.FrameMsg) return "frame";
            return "unknown kind " + d.Kind;
        }

        private static string Clip(string s) { return s.Length > 400 ? s.Substring(0, 400) + "... (" + s.Length + " chars)" : s; }

        private static string CheckDatagram(Dictionary<string, object> c)
        {
            byte[] bytes = Unhex(J.Str(c, "hex", null));
            Wire.Decoded got = null;
            string rejected = null;
            try { got = Wire.Decode(bytes, bytes.Length); }
            catch (WireException e) { rejected = e.Message; }
            switch (J.Str(c, "expect", null))
            {
                case "reject":
                    return rejected != null ? null : "accepted (" + J.Str(c, "why", "") + "): " + Clip(Describe(got.Body));
                case "ignore":
                {
                    var u = got == null ? null : got.Body as Wire.UnknownMsg;
                    if (u != null && u.Kind == (int)J.U32(c["kind"]) && u.PayloadLen == (int)J.U32(c["payload_len"])) return null;
                    return "expected an unknown kind " + J.Str(c, "kind", J.Write(c["kind"])) + ", got " + (got == null ? "a rejection (" + rejected + ")" : Clip(Describe(got.Body)));
                }
                case "accept":
                {
                    // revision 5: the values are not checked (a NaN has no JSON form), so only the kind is compared
                    if (got == null) return "rejected (" + J.Str(c, "why", "") + "): " + rejected;
                    string want = J.Str(c, "kind", null), have = KindName(got.Body);
                    return have == want ? null : "decoded as " + have + ", expected " + want;
                }
                case "decode":
                {
                    if (got == null) return "rejected: " + rejected;
                    var h = (Dictionary<string, object>)c["header"];
                    int wantFlags = h.ContainsKey("flags") ? (int)J.U32(h["flags"]) : 0;
                    var want = new Wire.Header((int)J.U32(h["version"]), wantFlags, J.U32(h["session"]), J.U32(h["seq"]), J.U64(h["time_us"]));
                    Wire.Header g = got.Header;
                    if (g.Version != want.Version || g.Session != want.Session || g.Seq != want.Seq || g.TimeUs != want.TimeUs || (h.ContainsKey("flags") && g.Flags != want.Flags))
                        return "header " + g + ", expected " + want;
                    Wire.Datagram wantBody = DatagramOf((Dictionary<string, object>)c["body"]);
                    string a = Describe(got.Body), e = Describe(wantBody);
                    if (a != e) return "body " + Clip(a) + ", expected " + Clip(e);
                    if (J.IsTrue(c, "encode"))
                    {
                        byte[] outb = Wire.Encode(want, wantBody);
                        if (!outb.SequenceEqual(bytes)) return "encoded " + Clip(Hex(outb)) + ", expected " + Clip(Hex(bytes));
                    }
                    return null;
                }
                default: throw new RunnerBug("expect " + J.Str(c, "expect", null));
            }
        }

        private static void DatagramVectors()
        {
            var r = new Report();
            foreach (var c in Cases("datagrams.json")) r.Record(c, Guard(() => CheckDatagram(c)));
            r.Finish("datagrams.json");
        }

        // ------------------------------------------------------------------------------------------ pixelfiles.json

        private sealed class TempDir : IDisposable
        {
            private static int _n;
            public readonly string Root;

            public TempDir(string tag)
            {
                Root = Path.Combine(Path.GetTempPath(), "link-cs-vectors-" + Environment.ProcessId + "-" + tag + "-" + Interlocked.Increment(ref _n));
                Directory.CreateDirectory(Root);
            }

            public string At(string name) { return Path.Combine(Root, name); }

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void CopyFixture(Dictionary<string, object> c, string to)
        {
            File.Copy(Path.Combine(Dir(), J.Str(c, "file", null).Replace('/', Path.DirectorySeparatorChar)), to);
        }

        private static void MakeRecipe(Dictionary<string, object> recipe, string to)
        {
            byte[] header = Unhex(J.Str(recipe, "header_hex", null));
            using (var f = new FileStream(to, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                f.SetLength((long)J.U64(recipe["length"]));
                f.Position = 0;
                f.Write(header, 0, header.Length);
            }
        }

        private static string CheckRead(PixelFile.Reader reader, Dictionary<string, object> rd)
        {
            ulong frame = J.U64(rd["frame"]);
            int slot = (int)J.U32(rd["slot"]);
            bool wantDepth = J.IsTrue(rd, "want_depth");
            PixelFile.Frame got = reader.Read(frame, slot, J.U32(rd["width"]), J.U32(rd["height"]), wantDepth);
            switch (J.Str(rd, "expect", null))
            {
                case "skip":
                    return got == null ? null : "delivered frame " + got.Number + " (" + J.Str(rd, "why", "") + ")";
                case "ok":
                {
                    if (got == null) return "frame " + frame + " slot " + slot + " skipped";
                    var checks = new[]
                    {
                        Tuple.Create("frame", frame, got.Number),
                        Tuple.Create("time_us", J.U64(rd["time_us"]), got.TimeUs),
                        Tuple.Create("camera_seq", (ulong)J.U32(rd["camera_seq"]), (ulong)got.CameraSeq),
                        Tuple.Create("width", (ulong)J.U32(rd["width"]), (ulong)got.Width),
                        Tuple.Create("height", (ulong)J.U32(rd["height"]), (ulong)got.Height),
                    };
                    foreach (var t in checks) if (t.Item2 != t.Item3) return t.Item1 + " is " + t.Item3 + ", expected " + t.Item2;
                    string hex = J.Str(rd, "rgba", null);
                    if (hex != null) { if (Hex(got.Rgba) != hex) return "colour bytes differ"; }
                    else if (Sha256Hex(got.Rgba) != J.Str(rd, "rgba_sha256", null)) return "colour bytes differ (sha256)";
                    var wantDepthList = rd["depth"] as List<object>;
                    if (wantDepthList == null)
                    {
                        if (got.Depth != null && got.Depth.Length > 0) return got.Depth.Length + " depth values, expected none";
                    }
                    else
                    {
                        float[] want = wantDepthList.Select(J.F32).ToArray();
                        if (got.Depth == null || got.Depth.Length != want.Length || got.Depth.Zip(want, (g, w) => BitConverter.SingleToInt32Bits(g) != BitConverter.SingleToInt32Bits(w)).Any(x => x))
                            return "depth " + (got.Depth == null ? "none" : Clip(Fs(got.Depth))) + ", expected " + Clip(Fs(want));
                    }
                    return null;
                }
                default: throw new RunnerBug("read expect " + J.Str(rd, "expect", null));
            }
        }

        /// <summary>Makes a FIFO with the mkfifo command; false if there is no such command or it fails.</summary>
        private static bool MakeFifo(string path)
        {
            try
            {
                var psi = new ProcessStartInfo("mkfifo", "\"" + path + "\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
                    return p.ExitCode == 0 && File.Exists(path);
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Null: passed. "skip:" prefix: the platform cannot make this kind of path.</summary>
        private static string CheckPixelReader(Dictionary<string, object> c)
        {
            using (var t = new TempDir("pf"))
            {
                string path = t.At("pixel.frames");
                string openPath;
                switch (J.Str(c, "path_kind", null))
                {
                    case null:
                        if (c.ContainsKey("recipe")) MakeRecipe((Dictionary<string, object>)c["recipe"], path);
                        else CopyFixture(c, path);
                        openPath = path;
                        break;
                    case "relative":
                    {
                        // a valid copy exists; only the relative spelling may refuse it. The spelling is relative to the working directory.
                        CopyFixture(c, path);
                        string rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
                        string copy = null;
                        if (Path.IsPathRooted(rel))
                        {
                            copy = "rbl-vector-relative-" + Environment.ProcessId + ".frames";
                            File.Copy(path, copy, true);
                            rel = copy;
                        }
                        try
                        {
                            string err;
                            using (OpenReader(rel, out err)) return err != null ? null : "opened a relative path";
                        }
                        finally { if (copy != null) { try { File.Delete(copy); } catch (IOException) { } } }
                    }
                    case "missing": openPath = t.At("does-not-exist.frames"); break;
                    case "empty": openPath = ""; break;
                    case "directory": openPath = t.At("a-directory"); Directory.CreateDirectory(openPath); break;
                    case "symlink":
                        if (OperatingSystem.IsWindows()) return "skip:symlink";
                        CopyFixture(c, path);
                        openPath = t.At("link.frames");
                        try { File.CreateSymbolicLink(openPath, path); }
                        catch (Exception) { return "skip:symlink"; }
                        break;
                    case "fifo":
                        if (OperatingSystem.IsWindows()) return "skip:fifo";
                        openPath = t.At("fifo.frames");
                        if (!MakeFifo(openPath)) return "skip:fifo";
                        break;
                    default: throw new RunnerBug("path_kind " + J.Str(c, "path_kind", null));
                }
                string openErr;
                PixelFile.Reader reader = OpenReader(openPath, out openErr);
                using (reader)
                {
                    switch (J.Str(c, "open", null))
                    {
                        case "refused":
                            return reader == null ? null : "opened the file (" + J.Str(c, "why", "") + ")";
                        case "ok":
                        {
                            if (reader == null) return "refused: " + openErr;
                            var info = c.ContainsKey("info") ? (Dictionary<string, object>)c["info"] : null;
                            if (info != null)
                            {
                                var got = new Dictionary<string, long>
                                {
                                    { "slots", reader.Slots }, { "width", reader.Width }, { "height", reader.Height }, { "stride", reader.Stride }, { "slot_size", reader.SlotSize },
                                };
                                foreach (var kv in got)
                                    if (kv.Value != (long)J.U64(info[kv.Key])) return kv.Key + " is " + kv.Value + ", expected " + J.Write(info[kv.Key]);
                                if (reader.HasDepth != J.IsTrue(info, "has_depth")) return "has_depth is " + reader.HasDepth + ", expected " + J.Write(info["has_depth"]);
                            }
                            foreach (object o in c.ContainsKey("reads") ? (List<object>)c["reads"] : new List<object>())
                            {
                                var rd = (Dictionary<string, object>)o;
                                string e = CheckRead(reader, rd);
                                if (e != null) return "read of frame " + J.Write(rd["frame"]) + " slot " + J.Write(rd["slot"]) + ": " + e;
                            }
                            foreach (object o in c.ContainsKey("then") ? (List<object>)c["then"] : new List<object>())
                            {
                                var step = (Dictionary<string, object>)o;
                                switch (J.Str(step, "op", null))
                                {
                                    case "truncate":
                                        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) f.SetLength((long)J.U64(step["length"]));
                                        break;
                                    case "read":
                                    {
                                        string e = CheckRead(reader, step);
                                        if (e != null) return "after truncation: " + e;
                                        break;
                                    }
                                    default: throw new RunnerBug("then op " + J.Str(step, "op", null));
                                }
                            }
                            return null;
                        }
                        default: throw new RunnerBug("open " + J.Str(c, "open", null));
                    }
                }
            }
        }

        /// <summary>Opens a reader; null (and the reason) if the library refuses.</summary>
        private static PixelFile.Reader OpenReader(string path, out string error)
        {
            error = null;
            try { return PixelFile.Reader.Open(path); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        private static void PixelReaderVectors()
        {
            var r = new Report();
            foreach (var c in Cases("pixelfiles.json"))
            {
                if (c.ContainsKey("writer")) continue;
                string id = J.Str(c, "id", "?");
                if (J.IsTrue(c, "large") && SkipLarge()) { r.Skipped.Add(id + " (large)"); continue; }
                if (OperatingSystem.IsWindows() && c.ContainsKey("skip_on") && ((List<object>)c["skip_on"]).Contains("windows")) { r.Skipped.Add(id + " (windows)"); continue; }
                string res = Guard(() => CheckPixelReader(c));
                if (res != null && res.StartsWith("skip:", StringComparison.Ordinal)) { r.Skipped.Add(id + " (" + res.Substring(5) + " not available here)"); continue; }
                r.Record(c, res);
            }
            r.Finish("pixelfiles.json (readers)");
        }

        private static string CheckPixelWriter(Dictionary<string, object> c)
        {
            var w = (Dictionary<string, object>)c["writer"];
            int width = (int)J.U32(w["width"]), height = (int)J.U32(w["height"]), slots = (int)J.U32(w["slots"]);
            bool depth = J.IsTrue(w, "with_depth");
            using (var t = new TempDir("pw"))
            {
                string path = t.At("writer.frames");
                switch (J.Str(c, "expect", null))
                {
                    case "create_fails_and_file_untouched":
                    {
                        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("precious"));
                        try { new PixelFile.Writer(path, width, height, slots, depth).Dispose(); return "created a writer over an existing file"; }
                        catch (Exception) { }
                        return Encoding.ASCII.GetString(File.ReadAllBytes(path)) == "precious" ? null : "the existing file was changed";
                    }
                    case "create_fails":
                    {
                        try { new PixelFile.Writer(path, width, height, slots, depth).Dispose(); return "created a writer (" + J.Str(c, "why", "") + ")"; }
                        catch (Exception) { }
                        return File.Exists(path) ? "failed but left a file" : null;
                    }
                }
                using (var writer = new PixelFile.Writer(path, width, height, slots, depth))
                {
                    var writes = (List<object>)w["writes"];
                    ulong done = 0; // accepted writes so far: a refused write uses no frame number
                    for (int i = 0; i < writes.Count; i++)
                    {
                        var wr = (Dictionary<string, object>)writes[i];
                        byte[] rgba = Unhex(J.Str(wr, "rgba", null));
                        float[] d = wr["depth"] is List<object> dl ? dl.Select(J.F32).ToArray() : null;
                        bool refused = J.Str(wr, "expect", null) == "refused";
                        ulong frame = 0;
                        string failure = null;
                        try { frame = writer.Write(rgba, d, (int)J.U32(wr["width"]), (int)J.U32(wr["height"]), J.U32(wr["camera_seq"]), J.U64(wr["time_us"])); }
                        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
                        if (refused)
                        {
                            if (failure == null) return "write " + i + " was accepted as frame " + frame + " (" + J.Str(wr, "why", "") + ")";
                            continue;
                        }
                        if (failure != null) return "write " + i + " failed: " + failure;
                        done++;
                        if (frame != done) return "write " + i + " numbered the frame " + frame + ", expected " + done;
                    }
                    byte[] got;
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        got = new byte[f.Length];
                        int n = 0;
                        while (n < got.Length) { int k = f.Read(got, n, got.Length - n); if (k <= 0) break; n += k; }
                    }
                    byte[] want = File.ReadAllBytes(Path.Combine(Dir(), J.Str(c, "file", null).Replace('/', Path.DirectorySeparatorChar)));
                    if (!got.SequenceEqual(want))
                    {
                        int at = -1;
                        for (int i = 0; i < Math.Min(got.Length, want.Length); i++) if (got[i] != want[i]) { at = i; break; }
                        return "file bytes differ (lengths " + got.Length + " and " + want.Length + ", first difference at " + (at < 0 ? "none" : at.ToString()) + ")";
                    }
                    return null;
                }
            }
        }

        private static void PixelWriterVectors()
        {
            var r = new Report();
            foreach (var c in Cases("pixelfiles.json"))
                if (c.ContainsKey("writer")) r.Record(c, Guard(() => CheckPixelWriter(c)));
            r.Finish("pixelfiles.json (writers)");
        }

        // ------------------------------------------------------------------------------------------ sessions.json

        private static void SessionVectors()
        {
            _datagramCases = Cases("datagrams.json").ToDictionary(c => J.Str(c, "id", null));
            var r = new Report();
            foreach (var c in Cases("sessions.json"))
            {
                string res;
                var sw = Stopwatch.StartNew();
                using (var s = new VSession(c)) res = s.Run((List<object>)c["steps"]);
                if (sw.ElapsedMilliseconds > 5000) Console.WriteLine("       (slow: " + J.Str(c, "id", null) + " took " + sw.ElapsedMilliseconds + " ms)");
                r.Record(c, res);
            }
            r.Finish("sessions.json");
        }

        private sealed class Conn
        {
            public Socket Sock;
            public readonly List<byte> Buf = new List<byte>();
            public bool Eof;
        }

        private sealed class EventRec
        {
            public string Type;
            public Dictionary<string, object> Data;
        }

        private sealed class StepFail : Exception
        {
            public StepFail(string m) : base(m) { }
        }

        /// <summary>One session case: the implementation under test (a Link), the raw peer's sockets and the UT's queued events.</summary>
        private sealed class VSession : IDisposable
        {
            private const int StepWaitMs = 3000;
            private readonly Dictionary<string, object> _case;
            private readonly Link _ut;
            private readonly List<EventRec> _events = new List<EventRec>();
            private readonly Dictionary<string, Conn> _conns = new Dictionary<string, Conn>();
            private readonly Socket _listener;
            private readonly Socket _udp;
            private Socket _other;
            private readonly Dictionary<string, string> _vars = new Dictionary<string, string>();
            private readonly List<LinkEvent> _tmp = new List<LinkEvent>();

            public VSession(Dictionary<string, object> c)
            {
                _case = c;
                var cfg = c.ContainsKey("config") ? (Dictionary<string, object>)c["config"] : new Dictionary<string, object>();
                Func<string, long, long> ms = (k, d) => cfg.ContainsKey(k) ? (long)J.U64(cfg[k]) : d;
                _udp = NewUdp();
                bool host = J.Str(c, "role_under_test", null) == "host";
                int port = 0;
                if (!host)
                {
                    _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    _listener.Listen(8);
                    _listener.Blocking = false;
                    port = ((IPEndPoint)_listener.LocalEndPoint).Port;
                }
                var lc = new LinkConfig("vector ut") { ControlPort = port, UdpPort = 0, HeartbeatMs = ms("heartbeat_ms", 250), TimeoutMs = ms("timeout_ms", 2000), ReconnectMs = ms("reconnect_ms", 200) };
                _ut = host ? Link.Host(lc) : Link.Guest(lc);
                _vars["peer_udp_port"] = ((IPEndPoint)_udp.LocalEndPoint).Port.ToString(CultureInfo.InvariantCulture);
                _vars["ut_udp_port"] = _ut.UdpPort.ToString(CultureInfo.InvariantCulture);
            }

            private static Socket NewUdp()
            {
                var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                u.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                u.Blocking = false;
                if (OperatingSystem.IsWindows())
                {
                    // an ICMP port-unreachable from an earlier send must not make a later receive fail (SIO_UDP_CONNRESET off)
                    try { u.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
                }
                return u;
            }

            public void Dispose()
            {
                // The raw peer's end goes first, so that the UT's closing drain meets the end of stream at once instead of waiting 200 ms for it.
                foreach (Conn c in _conns.Values) { try { c.Sock.Close(); } catch (Exception) { } }
                if (_listener != null) _listener.Close();
                _udp.Close();
                if (_other != null) _other.Close();
                try { _ut.Dispose(); } catch (Exception) { }
            }

            // ------------------------------------------------------------ polling and waiting

            private void Pump()
            {
                _tmp.Clear();
                _ut.Poll(_tmp);
                foreach (LinkEvent e in _tmp) _events.Add(ToRec(e));
            }

            /// <summary>Polls the UT until <paramref name="f"/> says true, for at most <paramref name="ms"/>; false on timeout.</summary>
            private bool Wait(int ms, Func<bool> f)
            {
                long end = Environment.TickCount64 + ms;
                while (true)
                {
                    Pump();
                    if (f()) return true;
                    if (Environment.TickCount64 >= end) return false;
                    Thread.Sleep(2);
                }
            }

            private static EventRec ToRec(LinkEvent e)
            {
                var d = new Dictionary<string, object>();
                string type;
                switch (e.Kind)
                {
                    case LinkEventKind.Connected:
                    {
                        PeerInfo p = ((LinkEvent.Connected)e).Peer;
                        type = "connected";
                        d["role"] = Control.RoleName(p.Role); d["game"] = p.Game; d["protocol"] = (long)p.Protocol; d["session"] = (long)p.Session; d["heartbeat_ms"] = p.HeartbeatMs;
                        break;
                    }
                    case LinkEventKind.Lost: type = "lost"; break;
                    case LinkEventKind.HandshakeFailed: type = "handshake_failed"; break;
                    case LinkEventKind.Camera:
                    {
                        var c = (LinkEvent.Camera)e;
                        type = "camera";
                        d["seq"] = (long)c.Header.Seq; d["viewport_width"] = (long)c.Value.ViewportWidth; d["viewport_height"] = (long)c.Value.ViewportHeight; d["depth_range"] = (long)(uint)c.Value.DepthRange;
                        break;
                    }
                    case LinkEventKind.Poses:
                    {
                        var p = (LinkEvent.Poses)e;
                        type = "poses";
                        d["count"] = (long)p.Values.Count;
                        d["entities"] = p.Values.Select(x => (object)(long)x.Entity).ToList();
                        break;
                    }
                    case LinkEventKind.Collision:
                    {
                        var c = (LinkEvent.Collision)e;
                        type = "collision";
                        d["query"] = (long)c.Query; d["triangles"] = (long)c.TriangleCount;
                        d["center"] = c.Center.Select(x => (object)(double)x).ToList(); d["radius"] = (double)c.Radius;
                        break;
                    }
                    case LinkEventKind.Frame:
                    {
                        Wire.FrameReady f = ((LinkEvent.Frame)e).Value;
                        type = "frame";
                        d["frame"] = f.Frame.ToString(CultureInfo.InvariantCulture); d["slot"] = (long)f.Slot; d["camera_seq"] = (long)f.CameraSeq; d["width"] = (long)f.Width; d["height"] = (long)f.Height;
                        break;
                    }
                    case LinkEventKind.Spawn:
                    {
                        Control.Spawn s = ((LinkEvent.Spawn)e).Value;
                        type = "spawn";
                        d["entity"] = (long)s.Entity; d["provider"] = s.Provider; d["asset"] = s.Asset;
                        if (s.Name != null) d["name"] = s.Name;
                        break;
                    }
                    case LinkEventKind.Despawn: type = "despawn"; d["entity"] = (long)((LinkEvent.Despawn)e).Entity; break;
                    case LinkEventKind.Focus:
                    {
                        var f = (LinkEvent.Focus)e;
                        type = "focus";
                        d["focus"] = Control.RoleName(f.Value); d["requested"] = f.Requested;
                        break;
                    }
                    case LinkEventKind.FrameChannel:
                    {
                        Control.FrameChannel f = ((LinkEvent.FrameChannel)e).Value;
                        type = "frame_channel";
                        d["path"] = f.Path; d["width"] = (long)f.Width; d["height"] = (long)f.Height; d["slots"] = (long)f.Slots;
                        break;
                    }
                    default: throw new RunnerBug("event kind " + e.Kind);
                }
                return new EventRec { Type = type, Data = d };
            }

            // ------------------------------------------------------------ variables, patterns

            private string Subst(string s)
            {
                foreach (var kv in _vars) s = s.Replace("${" + kv.Key + "}", kv.Value);
                return s;
            }

            private uint SessionVar()
            {
                string v;
                uint n;
                if (!_vars.TryGetValue("session", out v) || !uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new StepFail("a session variable is needed here");
                return n;
            }

            private static string LeHex(uint v) { return Hex(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }); }

            private string SubstHex(string s)
            {
                s = Subst(s);
                if (s.Contains("{session_le"))
                {
                    uint session = SessionVar();
                    s = s.Replace("{session_le}", LeHex(session)).Replace("{session_le_plus1}", LeHex(unchecked(session + 1))).Replace("{session_le_minus1}", LeHex(unchecked(session - 1)));
                }
                return s;
            }

            private static bool PatternOk(object actual, object pattern)
            {
                var p = pattern as string;
                if (p != null && p.StartsWith("$", StringComparison.Ordinal))
                {
                    long n = actual is long ? (long)actual : -1;
                    switch (p)
                    {
                        case "$string": return actual is string;
                        case "$int": return actual is long && n >= 0;
                        case "$u32nz": return actual is long && n >= 1 && n <= uint.MaxValue;
                        case "$u16nz": return actual is long && n >= 1 && n <= 65535;
                        case "$hb": return actual is long && n >= 10 && n <= 10000;
                        default: throw new RunnerBug("pattern " + p);
                    }
                }
                return J.Eq(actual, pattern);
            }

            private static string MatchOk(object actual, object match)
            {
                var a = actual as Dictionary<string, object>;
                if (a == null) return "not an object: " + J.Write(actual);
                foreach (var kv in (Dictionary<string, object>)match)
                {
                    object got;
                    if (!a.TryGetValue(kv.Key, out got) || !PatternOk(got, kv.Value))
                        return "key \"" + kv.Key + "\": expected " + J.Write(kv.Value) + ", got " + (got == null && !a.ContainsKey(kv.Key) ? "nothing" : J.Write(got)) + " in " + Clip(J.Write(a));
                }
                return null;
            }

            // ------------------------------------------------------------ the raw peer's connections

            private Conn ConnOf(Dictionary<string, object> step)
            {
                string name = J.Str(step, "conn", "a");
                Conn c;
                if (!_conns.TryGetValue(name, out c)) throw new StepFail("no connection " + name);
                return c;
            }

            private static void Fill(Conn c)
            {
                var tmp = new byte[16384];
                while (!c.Eof)
                {
                    SocketError err;
                    int n = c.Sock.Receive(tmp, 0, tmp.Length, SocketFlags.None, out err);
                    if (n > 0) { for (int i = 0; i < n; i++) c.Buf.Add(tmp[i]); continue; }
                    if (err == SocketError.WouldBlock || err == SocketError.Interrupted) return;
                    c.Eof = true; // end of stream, or a reset
                }
            }

            private static byte[] TakeLine(Conn c)
            {
                int i = c.Buf.IndexOf((byte)'\n');
                if (i < 0) return null;
                byte[] line = c.Buf.GetRange(0, i + 1).ToArray();
                c.Buf.RemoveRange(0, i + 1);
                return line;
            }

            /// <summary>Writes to the connection in a loop, polling the UT meanwhile. A write the UT's closed connection refuses ends the write quietly: the steps after it say what was expected.</summary>
            private void WriteAll(Dictionary<string, object> step, byte[] data)
            {
                Conn c = ConnOf(step);
                int sent = 0;
                long end = Environment.TickCount64 + 10000;
                while (sent < data.Length)
                {
                    SocketError err;
                    int n = c.Sock.Send(data, sent, data.Length - sent, SocketFlags.None, out err);
                    if (n > 0) sent += n;
                    else if (err == SocketError.WouldBlock) { Pump(); Thread.Sleep(1); }
                    else if (err != SocketError.Success && err != SocketError.Interrupted) return;
                    if (Environment.TickCount64 >= end) throw new StepFail("write timed out");
                }
            }

            // ------------------------------------------------------------ running

            public string Run(List<object> steps)
            {
                for (int n = 0; n < steps.Count; n++)
                {
                    var step = (Dictionary<string, object>)steps[n];
                    string op = J.Str(step, "op", null);
                    try { Step(op, step); }
                    catch (StepFail e) { return "step " + n + " (" + op + "): " + e.Message; }
                    catch (RunnerBug) { throw; }
                    catch (Exception e) { return "step " + n + " (" + op + "): exception " + e.GetType().Name + ": " + e.Message; }
                }
                return null;
            }

            private void Step(string op, Dictionary<string, object> step)
            {
                string name = J.Str(step, "conn", "a");
                switch (op)
                {
                    case "connect":
                    {
                        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        s.Connect(new IPEndPoint(IPAddress.Loopback, _ut.ControlPort));
                        s.Blocking = false;
                        _conns[name] = new Conn { Sock = s };
                        break;
                    }
                    case "accept":
                    {
                        Socket got = null;
                        bool ok = Wait(StepWaitMs, () =>
                        {
                            if (!_listener.Poll(0, SelectMode.SelectRead)) return false;
                            try { got = _listener.Accept(); return true; }
                            catch (SocketException) { return false; }
                        });
                        if (!ok) throw new StepFail("the guest did not connect");
                        got.NoDelay = true;
                        got.Blocking = false;
                        _conns[name] = new Conn { Sock = got };
                        break;
                    }
                    case "send_line": WriteAll(step, Encoding.UTF8.GetBytes(Subst(J.Str(step, "text", null)))); break;
                    case "send_hex": WriteAll(step, Unhex(J.Str(step, "hex", null))); break;
                    case "send_fill":
                    {
                        var b = new List<byte>(Encoding.ASCII.GetBytes(J.Str(step, "prefix", null)));
                        byte fill = (byte)J.Str(step, "fill", null)[0];
                        long count = (long)J.U64(step["count"]);
                        for (long i = 0; i < count; i++) b.Add(fill);
                        b.AddRange(Encoding.ASCII.GetBytes(J.Str(step, "suffix", null)));
                        WriteAll(step, b.ToArray());
                        break;
                    }
                    case "send_range":
                    {
                        string t = J.Str(step, "template", null);
                        var sb = new StringBuilder();
                        for (long i = (long)J.U64(step["from"]); i <= (long)J.U64(step["to"]); i++)
                            sb.Append(Subst(t.Replace("${i}", i.ToString(CultureInfo.InvariantCulture))));
                        WriteAll(step, Encoding.UTF8.GetBytes(sb.ToString()));
                        break;
                    }
                    case "expect_line": ExpectLine(step); break;
                    case "expect_no_line":
                    {
                        string bad = null;
                        Conn c = ConnOf(step);
                        Wait((int)J.U64(step["ms"]), () =>
                        {
                            Fill(c);
                            byte[] l = TakeLine(c);
                            if (l != null) { bad = "a line arrived: " + Show(Encoding.UTF8.GetString(l)); return true; }
                            if (c.Eof) { bad = "the connection closed"; return true; }
                            return false;
                        });
                        if (bad != null) throw new StepFail(bad);
                        break;
                    }
                    case "expect_closed":
                    {
                        bool allow = J.IsTrue(step, "allow_lines");
                        int within = step.ContainsKey("within_ms") ? (int)J.U64(step["within_ms"]) : 3000;
                        string bad = null;
                        Conn c = ConnOf(step);
                        bool closed = Wait(within, () =>
                        {
                            Fill(c);
                            if (!allow)
                            {
                                byte[] l = TakeLine(c);
                                if (l != null) { bad = "a line arrived before end of stream: " + Show(Encoding.UTF8.GetString(l)); return true; }
                            }
                            return c.Eof;
                        });
                        if (bad != null) throw new StepFail(bad);
                        if (!closed) throw new StepFail("the connection stayed open");
                        break;
                    }
                    case "close":
                    {
                        Conn c = ConnOf(step);
                        bool half = J.IsTrue(step, "half");
                        try { c.Sock.Shutdown(half ? SocketShutdown.Send : SocketShutdown.Both); } catch (SocketException) { }
                        if (!half) { c.Sock.Close(); _conns.Remove(name); }
                        break;
                    }
                    case "set":
                        foreach (var kv in (Dictionary<string, object>)step["vars"])
                            _vars[kv.Key] = kv.Value is long ? ((long)kv.Value).ToString(CultureInfo.InvariantCulture) : (string)kv.Value;
                        break;
                    case "sleep": Wait((int)J.U64(step["ms"]), () => false); break;
                    case "send_datagram":
                    {
                        byte[] b = Unhex(SubstHex(J.Str(step, "hex", null)));
                        var dest = new IPEndPoint(IPAddress.Loopback, _ut.UdpPort);
                        if (J.Str(step, "from", null) == "other")
                        {
                            if (_other == null) _other = NewUdp();
                            _other.SendTo(b, dest);
                        }
                        else _udp.SendTo(b, dest);
                        break;
                    }
                    case "expect_datagram": ExpectDatagram(step); break;
                    case "expect_no_datagram":
                    {
                        string bad = null;
                        Wait((int)J.U64(step["ms"]), () =>
                        {
                            byte[] d = RecvDatagram(out EndPoint from);
                            if (d == null) return false;
                            bad = "a " + d.Length + " byte datagram arrived from " + from;
                            return true;
                        });
                        if (bad != null) throw new StepFail(bad);
                        break;
                    }
                    case "expect_focus":
                    {
                        Pump();
                        string got = Control.RoleName(_ut.Focus);
                        if (got != J.Str(step, "focus", null)) throw new StepFail("focus is " + got + ", expected " + J.Str(step, "focus", null));
                        break;
                    }
                    case "ut": UtDo(step); break;
                    case "expect_event": ExpectEvent(step); break;
                    case "expect_no_event":
                    {
                        Wait((int)J.U64(step["ms"]), () => false);
                        string ty = J.Str(step, "event", null);
                        EventRec bad = _events.FirstOrDefault(e => e.Type == ty);
                        if (bad != null) throw new StepFail("unexpected " + ty + " event " + J.Write(bad.Data));
                        break;
                    }
                    default: throw new RunnerBug("unknown op " + op);
                }
            }

            private void ExpectLine(Dictionary<string, object> step)
            {
                Conn c = ConnOf(step);
                byte[] line = null;
                if (!Wait(StepWaitMs, () => { Fill(c); line = TakeLine(c); return line != null; })) throw new StepFail("no line from the implementation");
                string text = Encoding.UTF8.GetString(line);
                object v;
                try { v = J.Parse(text.TrimEnd()); }
                catch (J.JsonError e) { throw new StepFail("not JSON: " + Show(text) + ": " + e.Message); }
                var match = (Dictionary<string, object>)step["match"];
                string err = MatchOk(v, match);
                if (err != null) throw new StepFail("line " + Show(text) + ": " + err);
                if (J.IsTrue(step, "exact"))
                {
                    var got = ((Dictionary<string, object>)v).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    var want = match.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    if (!got.SequenceEqual(want)) throw new StepFail("keys [" + string.Join(", ", got) + "], expected exactly [" + string.Join(", ", want) + "]");
                }
                if (step.ContainsKey("capture"))
                {
                    var obj = (Dictionary<string, object>)v;
                    foreach (var kv in (Dictionary<string, object>)step["capture"])
                    {
                        object val;
                        if (!obj.TryGetValue((string)kv.Value, out val) || !(val is long) || (long)val < 0) throw new StepFail("captured value is not an integer");
                        string s = ((long)val).ToString(CultureInfo.InvariantCulture);
                        if (kv.Key == "ut_udp_port" && s != _ut.UdpPort.ToString(CultureInfo.InvariantCulture))
                            throw new StepFail("hello announces udp_port " + s + ", the socket is " + _ut.UdpPort);
                        _vars[kv.Key] = s;
                    }
                }
            }

            private byte[] RecvDatagram(out EndPoint from)
            {
                from = new IPEndPoint(IPAddress.Any, 0);
                var buf = new byte[16384];
                try
                {
                    if (!_udp.Poll(0, SelectMode.SelectRead)) return null;
                    int n = _udp.ReceiveFrom(buf, ref from);
                    return buf.Take(n).ToArray();
                }
                catch (SocketException) { return null; }
            }

            private void ExpectDatagram(Dictionary<string, object> step)
            {
                uint session = SessionVar();
                int timeout = step.ContainsKey("timeout_ms") ? (int)J.U64(step["timeout_ms"]) : 3000;
                int count = step.ContainsKey("count") ? (int)J.U64(step["count"]) : 1;
                byte[] like = null;
                if (step.ContainsKey("like"))
                {
                    Dictionary<string, object> dc;
                    if (!_datagramCases.TryGetValue(J.Str(step, "like", null), out dc)) throw new RunnerBug("no datagram case " + J.Str(step, "like", null));
                    like = Unhex(J.Str(dc, "hex", null));
                }
                int kind = step.ContainsKey("kind") ? (int)J.U64(step["kind"]) : like != null ? like[6] : -1;
                var seqs = new List<uint>();
                int utPort = _ut.UdpPort;
                string bad = null;
                bool done = Wait(timeout, () =>
                {
                    while (true)
                    {
                        EndPoint from;
                        byte[] d = RecvDatagram(out from);
                        if (d == null) return false;
                        int fromPort = ((IPEndPoint)from).Port;
                        if (fromPort != utPort) { bad = "datagram from " + from + ", the UT's port is " + utPort; return true; }
                        if (d.Length < 24 || d[0] != 'R' || d[1] != 'B' || d[2] != 'L' || d[3] != 'K' || (d[4] | d[5] << 8) != 1)
                        { bad = "bad header in " + Hex(d.Take(24).ToArray()); return true; }
                        if (Wire.Get32(d, 8) != session) { bad = "wrong session in a datagram from the UT"; return true; }
                        if (kind >= 0 && d[6] != kind) continue;
                        if (like != null)
                        {
                            bool same = d.Length == like.Length && d.Take(8).SequenceEqual(like.Take(8)) && d.Skip(24).SequenceEqual(like.Skip(24));
                            if (!same) { bad = "datagram " + Clip(Hex(d)) + " differs from " + Clip(Hex(like)); return true; }
                        }
                        seqs.Add(Wire.Get32(d, 12));
                        if (seqs.Count >= count) return true;
                    }
                });
                if (bad != null) throw new StepFail(bad);
                if (!done) throw new StepFail("got " + seqs.Count + " of " + count + " datagrams");
                if (J.IsTrue(step, "seq_consecutive"))
                    for (int i = 1; i < seqs.Count; i++)
                        if (seqs[i] != unchecked(seqs[i - 1] + 1)) throw new StepFail("seq values [" + string.Join(", ", seqs) + "] are not consecutive");
            }

            private void UtDo(Dictionary<string, object> step)
            {
                string what = J.Str(step, "do", null);
                Func<Wire.Datagram> body = () => DatagramOf((Dictionary<string, object>)_datagramCases[J.Str(step, "like", null)]["body"]);
                Func<string, Role> role = k => J.Str(step, k, null) == "host" ? Role.Host : Role.Guest;
                try
                {
                    switch (what)
                    {
                        case "spawn": _ut.Spawn(new Control.Spawn(J.U32(step["entity"]), J.Str(step, "provider", null), J.Str(step, "asset", null), J.Str(step, "name", null))); break;
                        case "despawn": _ut.Despawn(J.U32(step["entity"])); break;
                        case "set_focus": _ut.SetFocus(role("focus")); break;
                        case "announce_frames":
                            _ut.AnnounceFrames(new Control.FrameChannel(J.Str(step, "path", null), J.U32(step["width"]), J.U32(step["height"]), J.U32(step["slots"])));
                            break;
                        case "disconnect": _ut.Disconnect(J.Str(step, "reason", null)); break;
                        case "send_camera": _ut.SendCamera(((Wire.CameraMsg)body()).Camera); break;
                        case "send_poses": _ut.SendPoses(((Wire.PoseMsg)body()).Poses); break;
                        case "send_frame": _ut.SendFrame(((Wire.FrameMsg)body()).Frame); break;
                        default: throw new RunnerBug("ut do " + what);
                    }
                }
                catch (InvalidCastException) { throw new RunnerBug("ut " + what + ": the datagram case " + J.Str(step, "like", null) + " is of another kind"); }
                catch (NotConnectedException e) { throw new StepFail(e.Message); }
                catch (WireException e) { throw new StepFail(e.Message); }
            }

            private void ExpectEvent(Dictionary<string, object> step)
            {
                string ty = J.Str(step, "event", null);
                int count = step.ContainsKey("count") ? (int)J.U64(step["count"]) : 1;
                if (!Wait(StepWaitMs, () => _events.Count(e => e.Type == ty) >= count))
                    throw new StepFail("no " + count + " " + ty + " event(s); queue holds [" + string.Join(", ", _events.Select(e => e.Type)) + "]");
                int taken = 0;
                for (int i = 0; taken < count;)
                {
                    if (_events[i].Type != ty) { i++; continue; }
                    EventRec rec = _events[i];
                    _events.RemoveAt(i);
                    if (step.ContainsKey("match"))
                    {
                        string err = MatchOk(rec.Data, step["match"]);
                        if (err != null) throw new StepFail(ty + " event " + taken + ": " + err);
                    }
                    taken++;
                }
            }
        }
    }

    /// <summary>A small JSON reader and writer for the vector files: objects are Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers long (integers) or double.</summary>
    internal static class J
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw Fail(s, i, "trailing characters");
            return v;
        }

        internal sealed class JsonError : Exception
        {
            public JsonError(string m) : base(m) { }
        }

        private static Exception Fail(string s, int i, string m)
        {
            return new JsonError("JSON: " + m + " at " + i);
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw Fail(s, i, "unexpected end");
            switch (s[i])
            {
                case '{':
                {
                    var o = new Dictionary<string, object>();
                    i++;
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    while (true)
                    {
                        Ws(s, ref i);
                        if (i >= s.Length || s[i] != '"') throw Fail(s, i, "expected a key");
                        string k = Str(s, ref i);
                        Ws(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw Fail(s, i, "expected :");
                        i++;
                        o[k] = Value(s, ref i);
                        Ws(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == '}') { i++; return o; }
                        throw Fail(s, i, "expected , or }");
                    }
                }
                case '[':
                {
                    var a = new List<object>();
                    i++;
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    while (true)
                    {
                        a.Add(Value(s, ref i));
                        Ws(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == ']') { i++; return a; }
                        throw Fail(s, i, "expected , or ]");
                    }
                }
                case '"': return Str(s, ref i);
                case 't': Lit(s, ref i, "true"); return true;
                case 'f': Lit(s, ref i, "false"); return false;
                case 'n': Lit(s, ref i, "null"); return null;
                default:
                {
                    int st = i;
                    bool integer = true;
                    if (s[i] == '-') i++;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-'))
                    {
                        if (s[i] == '.' || s[i] == 'e' || s[i] == 'E') integer = false;
                        i++;
                    }
                    string tok = s.Substring(st, i - st);
                    long l;
                    if (tok.Length == 0 || tok == "-") throw Fail(s, st, "unexpected character");
                    if (integer && long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l) && tok != "-0") return l;
                    double d;
                    if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Fail(s, st, "bad number " + tok);
                    return d;
                }
            }
        }

        private static void Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) throw Fail(s, i, "bad literal");
            i += lit.Length;
        }

        private static string Str(string s, ref int i)
        {
            var b = new StringBuilder();
            i++;
            while (true)
            {
                if (i >= s.Length) throw Fail(s, i, "unterminated string");
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) throw Fail(s, i, "unterminated string");
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Fail(s, i, "bad unicode escape");
                        b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw Fail(s, i, "bad escape");
                }
            }
        }

        public static string Write(object o)
        {
            var b = new StringBuilder();
            Write(b, o);
            return b.ToString();
        }

        private static void Write(StringBuilder b, object o)
        {
            if (o == null) b.Append("null");
            else if (o is bool) b.Append((bool)o ? "true" : "false");
            else if (o is long) b.Append(((long)o).ToString(CultureInfo.InvariantCulture));
            else if (o is double) b.Append(((double)o).ToString("R", CultureInfo.InvariantCulture));
            else if (o is string) b.Append(Control_Quote((string)o));
            else if (o is List<object>)
            {
                b.Append('[');
                bool first = true;
                foreach (object x in (List<object>)o) { if (!first) b.Append(','); first = false; Write(b, x); }
                b.Append(']');
            }
            else
            {
                b.Append('{');
                bool first = true;
                foreach (var kv in (Dictionary<string, object>)o) { if (!first) b.Append(','); first = false; b.Append(Control_Quote(kv.Key)).Append(':'); Write(b, kv.Value); }
                b.Append('}');
            }
        }

        private static string Control_Quote(string s)
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

        /// <summary>Deep equality; numbers compare by value whether long or double.</summary>
        public static bool Eq(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if ((a is long || a is double) && (b is long || b is double))
                return a is long && b is long ? (long)a == (long)b : Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
            if (a is string && b is string) return (string)a == (string)b;
            if (a is bool && b is bool) return (bool)a == (bool)b;
            var la = a as List<object>;
            var lb = b as List<object>;
            if (la != null && lb != null) return la.Count == lb.Count && la.Zip(lb, Eq).All(x => x);
            var da = a as Dictionary<string, object>;
            var db = b as Dictionary<string, object>;
            if (da != null && db != null)
            {
                if (da.Count != db.Count) return false;
                foreach (var kv in da)
                {
                    object other;
                    if (!db.TryGetValue(kv.Key, out other) || !Eq(kv.Value, other)) return false;
                }
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ accessors

        public static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) ? v : null;
        }

        /// <summary>The string at <paramref name="k"/>, or <paramref name="dflt"/> if the key is missing or null.</summary>
        public static string Str(Dictionary<string, object> d, string k, string dflt)
        {
            return Get(d, k) as string ?? dflt;
        }

        public static bool IsTrue(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            return v is bool && (bool)v;
        }

        /// <summary>u64 values are decimal strings in the vectors; smaller ones are numbers.</summary>
        public static ulong U64(object v)
        {
            if (v is string) return ulong.Parse((string)v, NumberStyles.None, CultureInfo.InvariantCulture);
            if (v is long && (long)v >= 0) return (ulong)(long)v;
            throw new JsonError("not a u64: " + Write(v));
        }

        public static uint U32(object v)
        {
            ulong n = U64(v);
            if (n > uint.MaxValue) throw new JsonError("not a u32: " + n);
            return (uint)n;
        }

        /// <summary>An f32: a number, or the string "inf".</summary>
        public static float F32(object v)
        {
            if (v is string) { if ((string)v == "inf") return float.PositiveInfinity; if ((string)v == "-inf") return float.NegativeInfinity; }
            if (v is long || v is double) return (float)Convert.ToDouble(v, CultureInfo.InvariantCulture);
            throw new JsonError("not a number: " + Write(v));
        }

        public static float[] F32s(object v, int n)
        {
            var a = v as List<object>;
            if (a == null || a.Count != n) throw new JsonError("not an array of " + n + ": " + Write(v));
            return a.Select(F32).ToArray();
        }
    }
}
