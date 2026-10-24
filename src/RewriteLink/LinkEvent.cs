using System.Collections.Generic;

namespace Patodois.RewriteLink
{
    public sealed class PeerInfo
    {
        public string Game;
        public Role Role;
        public int Protocol;
        public uint Session;
        public int UdpPort;
        public long HeartbeatMs;
        /// <summary>The watchdog timeout in force for this peer.</summary>
        public long TimeoutMs;
    }

    public enum LinkEventKind { Connected, Lost, HandshakeFailed, Camera, Poses, Collision, Frame, Spawn, Despawn, Focus, FrameChannel }

    /// <summary>What <see cref="Link.Poll()"/> reports. Test <see cref="Kind"/> (or use <c>is</c>) and cast to the nested class.</summary>
    public abstract class LinkEvent
    {
        public abstract LinkEventKind Kind { get; }

        public sealed class Connected : LinkEvent
        {
            public readonly PeerInfo Peer;
            public Connected(PeerInfo peer) { Peer = peer; }
            public override LinkEventKind Kind { get { return LinkEventKind.Connected; } }
        }

        /// <summary>The session ended.</summary>
        public sealed class Lost : LinkEvent
        {
            public readonly string Reason;
            public Lost(string reason) { Reason = reason; }
            public override LinkEventKind Kind { get { return LinkEventKind.Lost; } }
        }

        /// <summary>A session that never started.</summary>
        public sealed class HandshakeFailed : LinkEvent
        {
            public readonly string Reason;
            public HandshakeFailed(string reason) { Reason = reason; }
            public override LinkEventKind Kind { get { return LinkEventKind.HandshakeFailed; } }
        }

        public sealed class Camera : LinkEvent
        {
            public readonly Wire.Header Header;
            public readonly Wire.Camera Value;
            public Camera(Wire.Header header, Wire.Camera camera) { Header = header; Value = camera; }
            public override LinkEventKind Kind { get { return LinkEventKind.Camera; } }
        }

        /// <summary>Only for entities the peer spawned; stale ones are left out.</summary>
        public sealed class Poses : LinkEvent
        {
            public readonly Wire.Header Header;
            public readonly IList<Wire.Pose> Values;
            public Poses(Wire.Header header, IList<Wire.Pose> poses) { Header = header; Values = poses; }
            public override LinkEventKind Kind { get { return LinkEventKind.Poses; } }
        }

        /// <summary>A complete snapshot. <see cref="Triangles"/> has 9 floats per triangle.</summary>
        public sealed class Collision : LinkEvent
        {
            public readonly uint Query;
            public readonly float[] Center;
            public readonly float Radius;
            public readonly float[] Triangles;
            public readonly int TriangleCount;
            public Collision(uint query, float[] center, float radius, float[] triangles, int count)
            { Query = query; Center = center; Radius = radius; Triangles = triangles; TriangleCount = count; }
            public override LinkEventKind Kind { get { return LinkEventKind.Collision; } }
        }

        /// <summary>A frame is ready in the peer's pixel file.</summary>
        public sealed class Frame : LinkEvent
        {
            public readonly Wire.Header Header;
            public readonly Wire.FrameReady Value;
            public Frame(Wire.Header header, Wire.FrameReady frame) { Header = header; Value = frame; }
            public override LinkEventKind Kind { get { return LinkEventKind.Frame; } }
        }

        public sealed class Spawn : LinkEvent
        {
            public readonly Control.Spawn Value;
            public Spawn(Control.Spawn spawn) { Value = spawn; }
            public override LinkEventKind Kind { get { return LinkEventKind.Spawn; } }
        }

        public sealed class Despawn : LinkEvent
        {
            public readonly uint Entity;
            public Despawn(uint entity) { Entity = entity; }
            public override LinkEventKind Kind { get { return LinkEventKind.Despawn; } }
        }

        /// <summary>Host: the guest REQUESTS the focus (<see cref="Requested"/>). Guest: the host SET it.</summary>
        public sealed class Focus : LinkEvent
        {
            public readonly Role Value;
            public readonly bool Requested;
            public Focus(Role focus, bool requested) { Value = focus; Requested = requested; }
            public override LinkEventKind Kind { get { return LinkEventKind.Focus; } }
        }

        /// <summary>The peer announced a pixel file.</summary>
        public sealed class FrameChannel : LinkEvent
        {
            public readonly Control.FrameChannel Value;
            public FrameChannel(Control.FrameChannel channel) { Value = channel; }
            public override LinkEventKind Kind { get { return LinkEventKind.FrameChannel; } }
        }
    }
}
