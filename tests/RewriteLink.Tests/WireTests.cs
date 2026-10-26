using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Patodois.RewriteLink.Tests.T;
using Hdr = Patodois.RewriteLink.Wire.Header;

namespace Patodois.RewriteLink.Tests
{
    internal static class WireTests
    {
        // the worked example of CONTRACT.md section 11
        private const string ExampleHeartbeat = "52 42 4C 4B 01 00 01 00 01 00 CD AB 04 00 00 00 00 40 1E 18 24 0A 06 00";

        private static Wire.Camera Cam()
        {
            return Wire.Camera.LookAt(new float[] { 0, 0, 5 }, new float[] { 0, 0, 0 }, 1f, 0.1f, 100f, 16, 9);
        }

        public static void Run()
        {
            Test("worked example: the heartbeat encodes to the contract bytes", () =>
            {
                byte[] got = Wire.Encode(new Hdr(1, 0, 0xABCD0001u, 4, 1_700_000_000_000_000UL), new Wire.HeartbeatMsg());
                Check(got.SequenceEqual(Hex(ExampleHeartbeat)), "heartbeat bytes: " + BitConverter.ToString(got));
                Eq(Encoding.ASCII.GetString(got, 0, 4), "RBLK", "magic bytes spell RBLK");
            });

            Test("worked example: the heartbeat decodes from the contract bytes", () =>
            {
                byte[] b = Hex(ExampleHeartbeat);
                Wire.Decoded d = Wire.Decode(b, b.Length);
                Check(d.Header.Equals(new Hdr(1, 0, 0xABCD0001u, 4, 1_700_000_000_000_000UL)), "header " + d.Header);
                Check(d.Body is Wire.HeartbeatMsg, "kind 1 is a heartbeat");
                Eq(d.Header.Session, 0xABCD0001u, "session as u32");
            });

            Test("constants match the contract", () =>
            {
                Eq(Wire.Magic, 0x4B4C4252u, "magic"); Eq(Wire.MaxPoses, 102, "max poses"); Eq(Wire.MaxTriangles, 226, "max triangles");
                Eq(Wire.HeaderLen, 24, "header"); Eq(Wire.CameraLen, 152, "camera payload"); Eq(Wire.DefaultControlPort, 47800, "port");
                Eq(Wire.MaxEntities, 1024, "entities"); Eq(Wire.MaxFrameDim, 16384, "dim"); Eq(Wire.MaxFramePixels, 67108864L, "pixels");
                Eq(Wire.CollisionPerPoll, 32, "pacing"); Eq(Wire.MaxDatagram, 8192, "datagram");
                Eq(Wire.MaxCollisionChunks, 4096, "collision chunks"); Eq(Wire.MaxCollisionChunks * Wire.MaxTriangles, 925696, "triangles in 4096 chunks");
            });

            Test("camera: a 176-byte datagram with the contract offsets", () =>
            {
                var view = new float[16]; var proj = new float[16];
                for (int i = 0; i < 16; i++) { view[i] = i + 1; proj[i] = 100 + i; }
                var c = new Wire.Camera(view, proj, 1.0f, 0.1f, float.PositiveInfinity, 1280, 720, Wire.DepthRange.Reversed);
                byte[] b = Wire.Encode(new Hdr(1, 0, 7, 9, 5), new Wire.CameraMsg(c));
                Eq(b.Length, 176, "datagram length");
                Eq(BitConverter.ToSingle(b, 24), 1f, "view[0] at payload 0");
                Eq(BitConverter.ToSingle(b, 24 + 15 * 4), 16f, "view[15]");
                Eq(BitConverter.ToSingle(b, 24 + 64), 100f, "proj[0] at payload 64");
                Eq(BitConverter.ToSingle(b, 24 + 128), 1.0f, "fov_y at 128");
                Eq(BitConverter.ToSingle(b, 24 + 132), 0.1f, "near at 132");
                Check(float.IsPositiveInfinity(BitConverter.ToSingle(b, 24 + 136)), "far at 136");
                Eq(BitConverter.ToUInt32(b, 24 + 140), 1280u, "viewport_width at 140");
                Eq(BitConverter.ToUInt32(b, 24 + 144), 720u, "viewport_height at 144");
                Eq(BitConverter.ToUInt32(b, 24 + 148), 2u, "depth_range at 148");
                var back = ((Wire.CameraMsg)Wire.Decode(b, b.Length).Body).Camera;
                Check(back.View.SequenceEqual(view) && back.Proj.SequenceEqual(proj), "matrices round trip");
                Eq(back.DepthRange, Wire.DepthRange.Reversed, "depth range");
                c.DepthRange = (Wire.DepthRange)3;
                Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 7, 9, 5), new Wire.CameraMsg(c)), "depth_range 3");
            });

            Test("pose: record offsets and the exact length 24 + 8 + 80n", () =>
            {
                var p = new Wire.Pose
                {
                    Entity = 0xFFFFFFFEu, Flags = Wire.PoseTeleport, TimeUs = 1_700_000_000_123_456UL,
                    Translation = new float[] { 1, 2, 3 }, Rotation = new float[] { 0, 0.5f, 0, 0.5f }, Scale = new float[] { 1, 1, 2 },
                    Velocity = new float[] { 4, 5, 6 }, AngularVelocity = new float[] { 7, 8, 9 },
                };
                byte[] b = Wire.Encode(new Hdr(1, 0, 7, 1, 2), new Wire.PoseMsg(new[] { p, p }));
                Eq(b.Length, 24 + 8 + 160, "two poses");
                Eq(BitConverter.ToUInt32(b, 24), 2u, "count");
                int r = 32;
                Eq(BitConverter.ToUInt32(b, r), 0xFFFFFFFEu, "entity"); Eq(BitConverter.ToUInt32(b, r + 4), 1u, "flags");
                Eq(BitConverter.ToUInt64(b, r + 8), 1_700_000_000_123_456UL, "time_us");
                Eq(BitConverter.ToSingle(b, r + 16), 1f, "translation.x"); Eq(BitConverter.ToSingle(b, r + 28 + 4), 0.5f, "rotation.y");
                Eq(BitConverter.ToSingle(b, r + 28 + 12), 0.5f, "rotation.w"); Eq(BitConverter.ToSingle(b, r + 44 + 8), 2f, "scale.z");
                Eq(BitConverter.ToSingle(b, r + 56), 4f, "velocity.x"); Eq(BitConverter.ToSingle(b, r + 68 + 8), 9f, "angular_velocity.z");
                var back = ((Wire.PoseMsg)Wire.Decode(b, b.Length).Body).Poses[1];
                Eq(back.Entity, 0xFFFFFFFEu, "entity round trip as u32");
                Check(back.Translation.SequenceEqual(p.Translation) && back.Rotation.SequenceEqual(p.Rotation), "round trip");
            });

            Test("pose: a full datagram of 102 poses is exactly 8192 bytes", () =>
            {
                var ps = Enumerable.Range(0, 102).Select(i => Wire.Pose.Identity((uint)i, 10)).ToList();
                Eq(Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.PoseMsg(ps)).Length, 8192, "full pose datagram");
                Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.PoseMsg(new List<Wire.Pose>())), "zero poses");
                ps.Add(Wire.Pose.Identity(1, 1));
                Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.PoseMsg(ps)), "103 poses");
            });

            Test("collision: chunking, offsets and the exact length", () =>
            {
                var tris = new float[9 * 800];
                for (int i = 0; i < 800; i++) { tris[9 * i] = i; tris[9 * i + 5] = 1; tris[9 * i + 3] = i; tris[9 * i + 6] = i + 1; tris[9 * i + 8] = 1; }
                List<Wire.CollisionChunk> chunks = Wire.Chunks(5, new float[] { 1, 2, 3 }, 10f, tris, 800);
                Eq(chunks.Count, 4, "800 triangles need 4 chunks of at most 226");
                Eq(chunks[0].TriangleCount, 226, "first chunk"); Eq(chunks[3].TriangleCount, 800 - 3 * 226, "last chunk");
                byte[] b = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(chunks[3]));
                Eq(b.Length, 24 + 32 + 36 * (800 - 3 * 226), "length");
                Eq(BitConverter.ToUInt32(b, 24), 5u, "query"); Eq(BitConverter.ToUInt16(b, 28), (ushort)3, "chunk_index"); Eq(BitConverter.ToUInt16(b, 30), (ushort)4, "chunk_count");
                Eq(BitConverter.ToSingle(b, 32), 1f, "center.x"); Eq(BitConverter.ToSingle(b, 44), 10f, "radius");
                Eq(BitConverter.ToUInt32(b, 48), (uint)(800 - 3 * 226), "triangle_count");
                Eq(Wire.Chunks(1, new float[3], 1f, new float[0], 0).Count, 1, "an empty snapshot is one chunk");
                var back = ((Wire.CollisionMsg)Wire.Decode(b, b.Length).Body).Chunk;
                Check(back.Triangles.Take(9).SequenceEqual(chunks[3].Triangles.Take(9)), "triangle round trip");
                foreach (var bad in new[] { new[] { 1, 1 }, new[] { 5, 2 }, new[] { 0, 0 } })
                {
                    var c = new Wire.CollisionChunk { Query = 1, ChunkIndex = bad[0], ChunkCount = bad[1], Center = new float[3], Radius = 1, Triangles = new float[0], TriangleCount = 0 };
                    Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(c)), "chunk_index " + bad[0] + " of " + bad[1]);
                }
            });

            Test("collision: at most 4096 chunks, on the wire, in the encoder and in the chunking (revision 5)", () =>
            {
                Func<int, int, byte[]> chunk = (index, count) =>
                {
                    var b = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(new Wire.CollisionChunk
                        { Query = 7, ChunkIndex = 0, ChunkCount = 1, Center = new float[3], Radius = 1, Triangles = new float[0], TriangleCount = 0 }));
                    Wire.Put16(b, 28, (uint)index); Wire.Put16(b, 30, (uint)count); // the layout is the same whatever the count says: patch it in
                    return b;
                };
                foreach (int[] ok in new[] { new[] { 0, 1 }, new[] { 4095, 4096 }, new[] { 0, 4096 } })
                {
                    byte[] b = chunk(ok[0], ok[1]);
                    var c = ((Wire.CollisionMsg)Wire.Decode(b, b.Length).Body).Chunk;
                    Check(c.ChunkIndex == ok[0] && c.ChunkCount == ok[1], "chunk " + ok[0] + " of " + ok[1] + " decodes");
                }
                foreach (int[] bad in new[] { new[] { 0, 4097 }, new[] { 4096, 4097 }, new[] { 5, 9281 }, new[] { 0, 65535 }, new[] { 65534, 65535 } })
                {
                    byte[] b = chunk(bad[0], bad[1]);
                    Throws<WireException>(() => Wire.Decode(b, b.Length), "chunk " + bad[0] + " of " + bad[1]);
                }
                var over = new Wire.CollisionChunk { Query = 1, ChunkIndex = 0, ChunkCount = 4097, Center = new float[3], Radius = 1, Triangles = new float[0], TriangleCount = 0 };
                Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(over)), "the encoder refuses chunk_count 4097");
                over.ChunkCount = 4096; over.ChunkIndex = 4095;
                Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(over));
                // the chunking helper counts without overflow and refuses before it allocates anything: the array is a few floats, the count is not
                Eq(Wire.ChunkCount(925696), 4096, "925696 triangles are 4096 chunks"); Eq(Wire.ChunkCount(925697), 4097, "one more is 4097");
                Eq(Wire.ChunkCount(int.MaxValue), 9502141, "no overflow near int.MaxValue");
                Throws<WireException>(() => Wire.Chunks(1, new float[3], 1f, new float[9], 925697), "4097 chunks");
                Throws<WireException>(() => Wire.Chunks(1, new float[3], 1f, new float[9], int.MaxValue), "int.MaxValue triangles");
                // the largest snapshot there is: 4096 chunks, the last one full
                var all = new float[9 * 925696];
                all[all.Length - 1] = 42f;
                List<Wire.CollisionChunk> chunks = Wire.Chunks(1, new float[3], 1f, all, 925696);
                Eq(chunks.Count, 4096, "chunks"); Eq(chunks[4095].ChunkCount, 4096, "chunk_count"); Eq(chunks[4095].TriangleCount, Wire.MaxTriangles, "the last chunk is full");
                byte[] last = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(chunks[4095]));
                var back = ((Wire.CollisionMsg)Wire.Decode(last, last.Length).Body).Chunk;
                Check(back.ChunkIndex == 4095 && back.ChunkCount == 4096 && back.Triangles[back.Triangles.Length - 1] == 42f, "the last chunk of the largest snapshot round trips");
            });

            Test("frame: a 48-byte datagram; frame 0 is refused", () =>
            {
                byte[] b = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.FrameMsg(new Wire.FrameReady(3, 0, 77, 64, 36)));
                Eq(b.Length, 48, "frame length");
                var f = ((Wire.FrameMsg)Wire.Decode(b, b.Length).Body).Frame;
                Check(f.Frame == 3 && f.Slot == 0 && f.CameraSeq == 77 && f.Width == 64 && f.Height == 36, "round trip");
                Throws<WireException>(() => Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.FrameMsg(new Wire.FrameReady(0, 0, 0, 1, 1))), "frame 0");
            });

            Test("malformed datagrams are rejected", () =>
            {
                byte[] ok = Hex(ExampleHeartbeat);
                Throws<WireException>(() => Wire.Decode(ok, 23), "truncated header");
                var badMagic = (byte[])ok.Clone(); badMagic[0] = (byte)'X';
                Throws<WireException>(() => Wire.Decode(badMagic, badMagic.Length), "bad magic");
                var badVersion = (byte[])ok.Clone(); badVersion[4] = 2;
                Throws<WireException>(() => Wire.Decode(badVersion, badVersion.Length), "bad version");
                Throws<WireException>(() => Wire.Decode(ok.Concat(new byte[] { 0 }).ToArray(), 25), "heartbeat with a payload");
                byte[] cam = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CameraMsg(Cam()));
                Throws<WireException>(() => Wire.Decode(cam, cam.Length - 1), "short camera");
                var badDepth = (byte[])cam.Clone(); badDepth[24 + 148] = 3;
                Throws<WireException>(() => Wire.Decode(badDepth, badDepth.Length), "depth_range 3");
                byte[] pose = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.PoseMsg(new[] { Wire.Pose.Identity(1, 1) }));
                var c0 = (byte[])pose.Clone(); c0[24] = 0;
                Throws<WireException>(() => Wire.Decode(c0, c0.Length), "pose count 0");
                var c2 = (byte[])pose.Clone(); c2[24] = 2;
                Throws<WireException>(() => Wire.Decode(c2, c2.Length), "pose count does not match the length");
                byte[] fr = Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.FrameMsg(new Wire.FrameReady(1, 0, 0, 1, 1)));
                Array.Clear(fr, 24, 8);
                Throws<WireException>(() => Wire.Decode(fr, fr.Length), "frame 0");
                var unknown = (byte[])ok.Clone(); unknown[6] = 99; // an unknown kind is not an error
                Check(Wire.Decode(unknown, unknown.Length).Body is Wire.UnknownMsg, "unknown kind ignored");
            });

            Test("fuzz: 20000 random buffers, every prefix of valid ones and 20000 mutations only ever throw WireException", () =>
            {
                var rnd = new Random(42);
                byte[] ok = Hex(ExampleHeartbeat);
                long accepted = 0, rejected = 0;
                for (int i = 0; i < 20000; i++)
                {
                    var junk = new byte[rnd.Next(300)];
                    rnd.NextBytes(junk);
                    if (junk.Length >= 8 && rnd.Next(2) == 0) { Array.Copy(ok, junk, 6); junk[6] = (byte)(1 + rnd.Next(6)); }
                    try { Wire.Decode(junk, junk.Length); accepted++; } catch (WireException) { rejected++; }
                }
                var tris = new float[45];
                var valid = new List<byte[]>
                {
                    Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CameraMsg(Cam())),
                    Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.PoseMsg(new[] { Wire.Pose.Identity(0, 5), Wire.Pose.Identity(1, 5), Wire.Pose.Identity(2, 5) })),
                    Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.CollisionMsg(new Wire.CollisionChunk { Query = 3, ChunkIndex = 1, ChunkCount = 2, Center = new float[3], Radius = 4, Triangles = tris, TriangleCount = 5 })),
                    Wire.Encode(new Hdr(1, 0, 1, 0, 0), new Wire.FrameMsg(new Wire.FrameReady(9, 1, 4, 8, 8))),
                };
                foreach (byte[] v in valid)
                    for (int cut = 0; cut <= v.Length; cut++)
                    {
                        bool good;
                        try { Wire.Decode(v, cut); good = true; } catch (WireException) { good = false; }
                        Check(good == (cut == v.Length), "only the whole datagram decodes (cut " + cut + " of " + v.Length + ")");
                    }
                for (int i = 0; i < 20000; i++)
                {
                    byte[] m = (byte[])valid[rnd.Next(4)].Clone();
                    int flips = 1 + rnd.Next(4);
                    for (int k = 0; k < flips; k++) m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                    int len = rnd.Next(8) == 0 ? rnd.Next(m.Length + 1) : m.Length;
                    try { Wire.Decode(m, len); accepted++; } catch (WireException) { rejected++; }
                }
                Console.WriteLine("     fuzz: " + accepted + " accepted, " + rejected + " rejected");
                Check(rejected > 20000, "most mutations are refused");
            });

            Test("serial arithmetic and version negotiation", () =>
            {
                Check(Wire.SeqNewer(1, 0), "1 newer than 0"); Check(!Wire.SeqNewer(0, 1), "0 not newer than 1");
                Check(Wire.SeqNewer(0, 0xFFFFFFFFu), "0 newer than 0xFFFFFFFF after the wrap"); Check(!Wire.SeqNewer(5, 5), "equal is not newer");
                Check(Wire.SeqNewer(0x7FFFFFFFu, 0), "half the range"); Check(!Wire.SeqNewer(0x80000000u, 0), "beyond half the range");
                Eq(Wire.Negotiate(1, 1, 1, 1), 1, "same version"); Eq(Wire.Negotiate(1, 3, 1, 2), 2, "overlap picks the lower max"); Eq(Wire.Negotiate(2, 3, 1, 1), -1, "disjoint ranges");
            });

            Test("camera helpers and quaternions agree", () =>
            {
                var c = Wire.Camera.LookAt(new float[] { 3, 4, 5 }, new float[] { 3, 4, 0 }, 1f, 0.1f, 100f, 16, 9);
                float[] eye = c.Eye(), fwd = c.Forward();
                for (int i = 0; i < 3; i++) Check(Math.Abs(eye[i] - new float[] { 3, 4, 5 }[i]) < 1e-4, "eye " + i);
                Check(Math.Abs(fwd[2] + 1) < 1e-5 && Math.Abs(fwd[0]) < 1e-5, "forward is -Z");
                float[] q = Quat.AxisAngle(new float[] { 0, 1, 0 }, (float)Math.PI / 2);
                float[] r = Quat.Rotate(q, new float[] { 0, 0, 1 });
                Check(Math.Abs(r[0] - 1) < 1e-5 && Math.Abs(r[1]) < 1e-5 && Math.Abs(r[2]) < 1e-5, "+Z turned 90 degrees about +Y is +X");
                float[] target = { 0.3f, 0.2f, -0.9f };
                float[] fwd2 = Quat.Rotate(Quat.FromTo(new float[] { 0, 0, 1 }, target), new float[] { 0, 0, 1 });
                float n = (float)Math.Sqrt(0.09 + 0.04 + 0.81);
                for (int i = 0; i < 3; i++) Check(Math.Abs(fwd2[i] - target[i] / n) < 1e-5, "FromTo axis " + i);
                Check(Math.Abs(Quat.Rotate(Quat.FromTo(new float[] { 0, 0, 1 }, new float[] { 0, 0, -1 }), new float[] { 0, 0, 1 })[2] + 1) < 1e-5, "opposite direction");
                var inf = Wire.Camera.LookAt(new float[] { 0, 0, 5 }, new float[3], 1f, 0.1f, float.PositiveInfinity, 16, 9);
                Check(inf.Proj[10] == -1f && Math.Abs(inf.Proj[14] + 0.2f) < 1e-6, "an infinite far plane");
            });
        }
    }
}
