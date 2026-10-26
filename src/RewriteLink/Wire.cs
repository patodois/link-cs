using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Patodois.RewriteLink
{
    /// <summary>A datagram, control line or pixel file that does not follow the contract.</summary>
    public sealed class WireException : Exception
    {
        public WireException(string message) : base(message) { }
    }

    /// <summary>
    /// Binary datagram layouts of the passthrough link, protocol v1 (docs/CONTRACT.md sections 9 and 10 of the
    /// rewrite-bridge repository). Everything is little-endian with no padding, written and read byte by byte,
    /// so it is the same on any machine.
    /// </summary>
    public static class Wire
    {
        public const uint Magic = 0x4B4C4252; // bytes 'R' 'B' 'L' 'K'
        public const int ProtocolVersion = 1;
        public const int MinProtocolVersion = 1;
        public const int DefaultControlPort = 47800;

        public const int HeaderLen = 24;
        public const int MaxDatagram = 8192;
        public const int CameraLen = 152;
        public const int PoseHeaderLen = 8;
        public const int PoseLen = 80;
        public const int MaxPoses = (MaxDatagram - HeaderLen - PoseHeaderLen) / PoseLen; // 102
        public const int CollisionHeaderLen = 32;
        public const int TriangleLen = 36;
        public const int MaxTriangles = (MaxDatagram - HeaderLen - CollisionHeaderLen) / TriangleLen; // 226
        public const int FrameLen = 24;
        /// <summary>
        /// Most chunks one collision snapshot may have, so the largest <c>chunk_count</c> (CONTRACT.md 9.5, revision 5): 4096 chunks are
        /// 925696 triangles, 32 MiB. A decoder drops a chunk that claims more and the encoder refuses to write one.
        /// </summary>
        public const int MaxCollisionChunks = 4096;
        public const int MaxLine = 65536;
        public const int MaxPendingControl = 1 << 20;
        public const int LingerMs = 200;

        /// <summary>Largest width or height of a pixel file (CONTRACT.md 9.6).</summary>
        public const int MaxFrameDim = 16384;
        /// <summary>Largest width * height of a pixel file (2^26, 8192 x 8192).</summary>
        public const long MaxFramePixels = 1L << 26;
        /// <summary>Live entities a receiver keeps for its peer; a spawn beyond that is ignored.</summary>
        public const int MaxEntities = 1024;
        public const int MinHeartbeatMs = 10;
        public const int MaxHeartbeatMs = 10000;
        /// <summary>Collision datagrams one <see cref="Link.Poll()"/> sends at most (CONTRACT.md 9.5).</summary>
        public const int CollisionPerPoll = 32;
        /// <summary>The UDP receive buffer a receiver asks for (CONTRACT.md 2); the OS may grant less.</summary>
        public const int RecvBufferBytes = 4 << 20;

        public const uint PoseTeleport = 1;

        public const int KindHeartbeat = 1;
        public const int KindCamera = 2;
        public const int KindPose = 3;
        public const int KindCollision = 4;
        public const int KindFrame = 5;

        /// <summary>Microseconds since the Unix epoch, wall clock (CONTRACT.md section 5).</summary>
        public static ulong NowUs()
        {
            return (ulong)((DateTime.UtcNow.Ticks - 621355968000000000L) / 10L);
        }

        /// <summary>RFC 1982 serial comparison on 32-bit counters: is <paramref name="a"/> newer than <paramref name="b"/>?</summary>
        public static bool SeqNewer(uint a, uint b)
        {
            return unchecked((int)(a - b)) > 0;
        }

        /// <summary>The session version two sides agree on, or -1 when their ranges do not overlap.</summary>
        public static int Negotiate(int hostMin, int hostMax, int guestMin, int guestMax)
        {
            int v = Math.Min(hostMax, guestMax);
            return v >= Math.Max(hostMin, guestMin) ? v : -1;
        }

        // ------------------------------------------------------------------ message types

        /// <summary>The 24-byte header.</summary>
        public sealed class Header
        {
            public readonly int Version;
            public readonly int Flags;
            public readonly uint Session;
            public readonly uint Seq;
            public readonly ulong TimeUs;

            public Header(int version, int flags, uint session, uint seq, ulong timeUs)
            {
                Version = version; Flags = flags; Session = session; Seq = seq; TimeUs = timeUs;
            }

            public override bool Equals(object o)
            {
                var h = o as Header;
                return h != null && Version == h.Version && Flags == h.Flags && Session == h.Session && Seq == h.Seq && TimeUs == h.TimeUs;
            }

            public override int GetHashCode() { return unchecked((int)(Session * 31u + Seq)); }

            public override string ToString()
            {
                return "Header[version=" + Version + ", flags=" + Flags + ", session=" + Session + ", seq=" + Seq + ", timeUs=" + TimeUs + "]";
            }
        }

        public enum DepthRange : uint { NegOneToOne = 0, ZeroToOne = 1, Reversed = 2 }

        /// <summary>The host's view. Matrices are column-major, <c>m[c*4 + r]</c>.</summary>
        public sealed class Camera
        {
            public float[] View;
            public float[] Proj;
            public float FovY;
            public float Near;
            public float Far;
            public uint ViewportWidth;
            public uint ViewportHeight;
            public DepthRange DepthRange;

            public Camera() { View = new float[16]; Proj = new float[16]; }

            public Camera(float[] view, float[] proj, float fovY, float near, float far, uint width, uint height, DepthRange range)
            {
                View = view; Proj = proj; FovY = fovY; Near = near; Far = far;
                ViewportWidth = width; ViewportHeight = height; DepthRange = range;
            }

            /// <summary>A right-handed look-at camera with an OpenGL-style perspective projection (depth range 0).</summary>
            public static Camera LookAt(float[] eye, float[] target, float fovY, float near, float far, uint width, uint height)
            {
                float[] f = Norm(Sub(target, eye));
                float[] s = Norm(Cross(f, new float[] { 0, 1, 0 }));
                float[] u = Cross(s, f);
                var view = new float[]
                {
                    s[0], u[0], -f[0], 0,
                    s[1], u[1], -f[1], 0,
                    s[2], u[2], -f[2], 0,
                    -Dot(s, eye), -Dot(u, eye), Dot(f, eye), 1,
                };
                float aspect = (float)width / Math.Max(height, 1u);
                float t = (float)(1.0 / Math.Tan(fovY * 0.5));
                var proj = new float[16];
                proj[0] = t / aspect;
                proj[5] = t;
                proj[11] = -1;
                if (!float.IsInfinity(far) && !float.IsNaN(far))
                {
                    proj[10] = (far + near) / (near - far);
                    proj[14] = 2 * far * near / (near - far);
                }
                else
                {
                    proj[10] = -1;
                    proj[14] = -2 * near;
                }
                return new Camera(view, proj, fovY, near, far, width, height, DepthRange.NegOneToOne);
            }

            /// <summary>The camera's world position, assuming <see cref="View"/> is a rigid transform.</summary>
            public float[] Eye()
            {
                var t = new float[] { View[12], View[13], View[14] };
                var e = new float[3];
                for (int c = 0; c < 3; c++)
                {
                    float s = 0;
                    for (int r = 0; r < 3; r++) s += View[c * 4 + r] * t[r];
                    e[c] = -s;
                }
                return e;
            }

            /// <summary>The direction the camera looks in, world space.</summary>
            public float[] Forward() { return new float[] { -View[2], -View[6], -View[10] }; }
        }

        /// <summary>One entity pose, world space.</summary>
        public sealed class Pose
        {
            public uint Entity;
            public uint Flags;
            public ulong TimeUs;
            public float[] Translation = new float[3];
            public float[] Rotation = new float[] { 0, 0, 0, 1 };
            public float[] Scale = new float[] { 1, 1, 1 };
            public float[] Velocity = new float[3];
            public float[] AngularVelocity = new float[3];

            /// <summary>An identity pose at the origin, at rest.</summary>
            public static Pose Identity(uint entity, ulong timeUs)
            {
                return new Pose { Entity = entity, TimeUs = timeUs };
            }
        }

        /// <summary>One datagram's share of a collision snapshot. <see cref="Triangles"/> holds 9 floats per triangle (a, b, c; x y z each).</summary>
        public sealed class CollisionChunk
        {
            public uint Query;
            public int ChunkIndex;
            public int ChunkCount;
            public float[] Center;
            public float Radius;
            public float[] Triangles;
            public int TriangleCount;
        }

        public sealed class FrameReady
        {
            public ulong Frame;
            public uint Slot;
            public uint CameraSeq;
            public uint Width;
            public uint Height;

            public FrameReady(ulong frame, uint slot, uint cameraSeq, uint width, uint height)
            {
                Frame = frame; Slot = slot; CameraSeq = cameraSeq; Width = width; Height = height;
            }
        }

        public abstract class Datagram { public abstract int Kind { get; } }
        public sealed class HeartbeatMsg : Datagram { public override int Kind { get { return KindHeartbeat; } } }
        public sealed class CameraMsg : Datagram
        {
            public readonly Camera Camera;
            public CameraMsg(Camera c) { Camera = c; }
            public override int Kind { get { return KindCamera; } }
        }
        public sealed class PoseMsg : Datagram
        {
            public readonly IList<Pose> Poses;
            public PoseMsg(IList<Pose> poses) { Poses = poses; }
            public override int Kind { get { return KindPose; } }
        }
        public sealed class CollisionMsg : Datagram
        {
            public readonly CollisionChunk Chunk;
            public CollisionMsg(CollisionChunk c) { Chunk = c; }
            public override int Kind { get { return KindCollision; } }
        }
        public sealed class FrameMsg : Datagram
        {
            public readonly FrameReady Frame;
            public FrameMsg(FrameReady f) { Frame = f; }
            public override int Kind { get { return KindFrame; } }
        }
        /// <summary>A kind this version does not know: ignored, but proof of life.</summary>
        public sealed class UnknownMsg : Datagram
        {
            private readonly int _kind;
            public readonly int PayloadLen;
            public UnknownMsg(int kind, int payloadLen) { _kind = kind; PayloadLen = payloadLen; }
            public override int Kind { get { return _kind; } }
        }

        public sealed class Decoded
        {
            public readonly Header Header;
            public readonly Datagram Body;
            public Decoded(Header h, Datagram b) { Header = h; Body = b; }
        }

        // ------------------------------------------------------------------ little-endian helpers

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        internal static void Put16(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        internal static void Put32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        internal static void Put64(byte[] b, int o, ulong v) { Put32(b, o, (uint)v); Put32(b, o + 4, (uint)(v >> 32)); }
        internal static void PutF(byte[] b, int o, float f) { var x = new FloatBits { F = f }; Put32(b, o, x.U); }
        internal static uint Get16(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8)); }
        internal static uint Get32(byte[] b, int o)
        {
            return (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        }
        internal static ulong Get64(byte[] b, int o) { return Get32(b, o) | ((ulong)Get32(b, o + 4) << 32); }
        internal static float GetF(byte[] b, int o) { var x = new FloatBits { U = Get32(b, o) }; return x.F; }

        private static void PutFloats(byte[] b, int o, float[] v, int n)
        {
            if (v == null || v.Length != n) throw new WireException("expected " + n + " floats");
            for (int i = 0; i < n; i++) PutF(b, o + 4 * i, v[i]);
        }

        private static float[] GetFloats(byte[] b, int o, int n)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = GetF(b, o + 4 * i);
            return v;
        }

        // ------------------------------------------------------------------ encode

        /// <summary>Encodes into a new array of exactly the datagram's size. Throws <see cref="WireException"/> for a value the contract forbids.</summary>
        public static byte[] Encode(Header h, Datagram body)
        {
            var buf = new byte[MaxDatagram];
            int n = EncodeInto(buf, h, body);
            var exact = new byte[n];
            Buffer.BlockCopy(buf, 0, exact, 0, n);
            return exact;
        }

        /// <summary>Encodes into <paramref name="buf"/> (at least <see cref="MaxDatagram"/> bytes) and returns the length.</summary>
        public static int EncodeInto(byte[] buf, Header h, Datagram body)
        {
            int payload;
            if (body is HeartbeatMsg) payload = 0;
            else if (body is CameraMsg)
            {
                if ((uint)((CameraMsg)body).Camera.DepthRange > 2) throw new WireException("bad value: depth_range");
                payload = CameraLen;
            }
            else if (body is PoseMsg)
            {
                int n = ((PoseMsg)body).Poses.Count;
                if (n < 1 || n > MaxPoses) throw new WireException("pose count " + n);
                payload = PoseHeaderLen + PoseLen * n;
            }
            else if (body is CollisionMsg)
            {
                CollisionChunk c = ((CollisionMsg)body).Chunk;
                if (c.TriangleCount < 0 || c.TriangleCount > MaxTriangles) throw new WireException("triangle count " + c.TriangleCount);
                if (c.Triangles == null || c.Triangles.Length < 9 * c.TriangleCount) throw new WireException("triangle array too short");
                if (c.ChunkCount < 1 || c.ChunkCount > MaxCollisionChunks || c.ChunkIndex < 0 || c.ChunkIndex >= c.ChunkCount)
                    throw new WireException("bad value: chunk_index " + c.ChunkIndex + " of " + c.ChunkCount + " (chunk_count is 1 to " + MaxCollisionChunks + ")");
                payload = CollisionHeaderLen + TriangleLen * c.TriangleCount;
            }
            else if (body is FrameMsg)
            {
                if (((FrameMsg)body).Frame.Frame == 0) throw new WireException("bad value: frame numbers start at 1");
                payload = FrameLen;
            }
            else throw new WireException("cannot encode an unknown kind");

            Put32(buf, 0, Magic);
            Put16(buf, 4, (uint)h.Version);
            buf[6] = (byte)body.Kind;
            buf[7] = (byte)h.Flags;
            Put32(buf, 8, h.Session);
            Put32(buf, 12, h.Seq);
            Put64(buf, 16, h.TimeUs);
            int p = HeaderLen;
            if (body is CameraMsg)
            {
                Camera c = ((CameraMsg)body).Camera;
                PutFloats(buf, p, c.View, 16);
                PutFloats(buf, p + 64, c.Proj, 16);
                PutF(buf, p + 128, c.FovY); PutF(buf, p + 132, c.Near); PutF(buf, p + 136, c.Far);
                Put32(buf, p + 140, c.ViewportWidth); Put32(buf, p + 144, c.ViewportHeight); Put32(buf, p + 148, (uint)c.DepthRange);
            }
            else if (body is PoseMsg)
            {
                IList<Pose> poses = ((PoseMsg)body).Poses;
                Put32(buf, p, (uint)poses.Count);
                Put32(buf, p + 4, 0);
                p += PoseHeaderLen;
                for (int i = 0; i < poses.Count; i++, p += PoseLen)
                {
                    Pose q = poses[i];
                    Put32(buf, p, q.Entity); Put32(buf, p + 4, q.Flags); Put64(buf, p + 8, q.TimeUs);
                    PutFloats(buf, p + 16, q.Translation, 3);
                    PutFloats(buf, p + 28, q.Rotation, 4);
                    PutFloats(buf, p + 44, q.Scale, 3);
                    PutFloats(buf, p + 56, q.Velocity, 3);
                    PutFloats(buf, p + 68, q.AngularVelocity, 3);
                }
            }
            else if (body is CollisionMsg)
            {
                CollisionChunk c = ((CollisionMsg)body).Chunk;
                Put32(buf, p, c.Query); Put16(buf, p + 4, (uint)c.ChunkIndex); Put16(buf, p + 6, (uint)c.ChunkCount);
                PutFloats(buf, p + 8, c.Center, 3);
                PutF(buf, p + 20, c.Radius);
                Put32(buf, p + 24, (uint)c.TriangleCount); Put32(buf, p + 28, 0);
                p += CollisionHeaderLen;
                for (int i = 0; i < 9 * c.TriangleCount; i++) PutF(buf, p + 4 * i, c.Triangles[i]);
            }
            else if (body is FrameMsg)
            {
                FrameReady f = ((FrameMsg)body).Frame;
                Put64(buf, p, f.Frame); Put32(buf, p + 8, f.Slot); Put32(buf, p + 12, f.CameraSeq);
                Put32(buf, p + 16, f.Width); Put32(buf, p + 20, f.Height);
            }
            return HeaderLen + payload;
        }

        // ------------------------------------------------------------------ decode

        /// <summary>Checks length, magic and version and reads the header; returns the kind in <paramref name="kind"/>.</summary>
        public static Header DecodeHeader(byte[] buf, int len, out int kind)
        {
            if (len < HeaderLen) throw new WireException("truncated: need 24 bytes, got " + len);
            if (Get32(buf, 0) != Magic) throw new WireException("bad magic");
            int version = (int)Get16(buf, 4);
            if (version != ProtocolVersion) throw new WireException("unsupported version " + version);
            kind = buf[6];
            return new Header(version, buf[7], Get32(buf, 8), Get32(buf, 12), Get64(buf, 16));
        }

        private static void Exact(int kind, int payload, int expected)
        {
            if (payload != expected)
                throw new WireException("kind " + kind + ": expected " + (HeaderLen + expected) + " bytes, got " + (HeaderLen + payload));
        }

        /// <summary>Decodes one datagram. Anything malformed throws <see cref="WireException"/>; nothing else is thrown.</summary>
        public static Decoded Decode(byte[] buf, int len)
        {
            int kind;
            Header h = DecodeHeader(buf, len, out kind);
            int payload = len - HeaderLen;
            int p = HeaderLen;
            Datagram body;
            switch (kind)
            {
                case KindHeartbeat:
                    Exact(kind, payload, 0);
                    body = new HeartbeatMsg();
                    break;
                case KindCamera:
                {
                    Exact(kind, payload, CameraLen);
                    uint dr = Get32(buf, p + 148);
                    if (dr > 2) throw new WireException("bad value: depth_range " + dr);
                    body = new CameraMsg(new Camera(GetFloats(buf, p, 16), GetFloats(buf, p + 64, 16), GetF(buf, p + 128), GetF(buf, p + 132),
                        GetF(buf, p + 136), Get32(buf, p + 140), Get32(buf, p + 144), (DepthRange)dr));
                    break;
                }
                case KindPose:
                {
                    if (payload < 4) throw new WireException("truncated pose");
                    uint count = Get32(buf, p);
                    if (count == 0 || count > MaxPoses) throw new WireException("bad value: pose count");
                    Exact(kind, payload, PoseHeaderLen + PoseLen * (int)count);
                    var poses = new Pose[count];
                    int q = p + PoseHeaderLen;
                    for (int i = 0; i < poses.Length; i++, q += PoseLen)
                    {
                        poses[i] = new Pose
                        {
                            Entity = Get32(buf, q), Flags = Get32(buf, q + 4), TimeUs = Get64(buf, q + 8),
                            Translation = GetFloats(buf, q + 16, 3), Rotation = GetFloats(buf, q + 28, 4), Scale = GetFloats(buf, q + 44, 3),
                            Velocity = GetFloats(buf, q + 56, 3), AngularVelocity = GetFloats(buf, q + 68, 3),
                        };
                    }
                    body = new PoseMsg(poses);
                    break;
                }
                case KindCollision:
                {
                    if (payload < CollisionHeaderLen) throw new WireException("truncated collision");
                    int index = (int)Get16(buf, p + 4);
                    int count = (int)Get16(buf, p + 6);
                    uint n = Get32(buf, p + 24);
                    if (count == 0 || index >= count) throw new WireException("bad value: chunk_index");
                    if (count > MaxCollisionChunks) throw new WireException("bad value: chunk_count above " + MaxCollisionChunks);
                    if (n > MaxTriangles) throw new WireException("bad value: triangle_count");
                    Exact(kind, payload, CollisionHeaderLen + TriangleLen * (int)n);
                    var tris = GetFloats(buf, p + CollisionHeaderLen, 9 * (int)n);
                    body = new CollisionMsg(new CollisionChunk
                    {
                        Query = Get32(buf, p), ChunkIndex = index, ChunkCount = count, Center = GetFloats(buf, p + 8, 3),
                        Radius = GetF(buf, p + 20), Triangles = tris, TriangleCount = (int)n,
                    });
                    break;
                }
                case KindFrame:
                {
                    Exact(kind, payload, FrameLen);
                    var f = new FrameReady(Get64(buf, p), Get32(buf, p + 8), Get32(buf, p + 12), Get32(buf, p + 16), Get32(buf, p + 20));
                    if (f.Frame == 0) throw new WireException("bad value: frame 0");
                    body = new FrameMsg(f);
                    break;
                }
                default:
                    body = new UnknownMsg(kind, payload);
                    break;
            }
            return new Decoded(h, body);
        }

        /// <summary>
        /// The number of datagrams a snapshot of <paramref name="triangles"/> triangles needs (at least 1). It does not check the limit:
        /// a result above <see cref="MaxCollisionChunks"/> means the snapshot cannot be sent.
        /// </summary>
        public static int ChunkCount(int triangles)
        {
            return (int)Math.Max(1L, ((long)triangles + MaxTriangles - 1) / MaxTriangles);
        }

        /// <summary>
        /// Splits a snapshot (9 floats per triangle) into datagram-sized chunks; an empty one still gives one chunk. A snapshot that needs
        /// more than <see cref="MaxCollisionChunks"/> chunks (more than 925696 triangles) is refused with a <see cref="WireException"/>
        /// before anything is allocated.
        /// </summary>
        public static List<CollisionChunk> Chunks(uint query, float[] center, float radius, float[] triangles, int triangleCount)
        {
            int count = ChunkCount(triangleCount);
            if (count > MaxCollisionChunks) throw new WireException("too many triangles: a snapshot has at most " + MaxCollisionChunks + " chunks");
            var list = new List<CollisionChunk>(count);
            for (int i = 0; i < count; i++)
            {
                int from = Math.Min(triangleCount, i * MaxTriangles);
                int to = Math.Min(triangleCount, from + MaxTriangles);
                var t = new float[9 * (to - from)];
                Array.Copy(triangles, 9 * from, t, 0, t.Length);
                list.Add(new CollisionChunk { Query = query, ChunkIndex = i, ChunkCount = count, Center = center, Radius = radius, Triangles = t, TriangleCount = to - from });
            }
            return list;
        }

        // ------------------------------------------------------------------ small vector helpers

        internal static float[] Sub(float[] a, float[] b) { return new float[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        internal static float Dot(float[] a, float[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        internal static float[] Cross(float[] a, float[] b)
        {
            return new float[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }
        internal static float[] Norm(float[] a)
        {
            float l = (float)Math.Sqrt(Dot(a, a));
            return l > 0 ? new float[] { a[0] / l, a[1] / l, a[2] / l } : a;
        }
    }

    /// <summary>Quaternion helpers in the link's convention: x, y, z, w, unit length, +Z is an entity's front.</summary>
    public static class Quat
    {
        public static float[] Rotate(float[] q, float[] v)
        {
            float ux = q[0], uy = q[1], uz = q[2], w = q[3];
            float cx = uy * v[2] - uz * v[1];
            float cy = uz * v[0] - ux * v[2];
            float cz = ux * v[1] - uy * v[0];
            float dx = uy * cz - uz * cy;
            float dy = uz * cx - ux * cz;
            float dz = ux * cy - uy * cx;
            return new float[] { v[0] + 2 * (w * cx + dx), v[1] + 2 * (w * cy + dy), v[2] + 2 * (w * cz + dz) };
        }

        /// <summary>The shortest-arc rotation taking direction a onto direction b.</summary>
        public static float[] FromTo(float[] a, float[] b)
        {
            float[] u = Wire.Norm(a), v = Wire.Norm(b);
            float d = Wire.Dot(u, v);
            if (d < -0.999999f)
            {
                float[] axis = Wire.Norm(Wire.Cross(u, Math.Abs(u[0]) < 0.9f ? new float[] { 1, 0, 0 } : new float[] { 0, 1, 0 }));
                return new float[] { axis[0], axis[1], axis[2], 0 };
            }
            float[] c = Wire.Cross(u, v);
            float n = (float)Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + (1 + d) * (1 + d));
            return new float[] { c[0] / n, c[1] / n, c[2] / n, (1 + d) / n };
        }

        public static float[] AxisAngle(float[] axis, float angle)
        {
            float s = (float)Math.Sin(angle * 0.5);
            return new float[] { axis[0] * s, axis[1] * s, axis[2] * s, (float)Math.Cos(angle * 0.5) };
        }
    }
}
