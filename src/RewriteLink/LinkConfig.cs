namespace Patodois.RewriteLink
{
    /// <summary>Settings for <see cref="Link.Host"/> and <see cref="Link.Guest"/>. Everything is loopback only.</summary>
    public sealed class LinkConfig
    {
        /// <summary>This side's game, sent in hello.</summary>
        public string Game;
        /// <summary>Host: TCP port to listen on (0 = any free port). Guest: the host's port to connect to.</summary>
        public int ControlPort = Wire.DefaultControlPort;
        /// <summary>This side's UDP port (0 = any free port); announced in hello.</summary>
        public int UdpPort = 0;
        /// <summary>Heartbeat period; a hello carries it and a peer accepts 10 to 10000 only, so the link clamps it to that range.</summary>
        public long HeartbeatMs = 250;
        /// <summary>Watchdog timeout; the effective one is at least four peer heartbeats.</summary>
        public long TimeoutMs = 2000;
        /// <summary>Guest: how often to retry connecting while no host answers.</summary>
        public long ReconnectMs = 500;
        /// <summary>
        /// Receiver limit on one assembled collision snapshot; a bigger one is dropped (and counted as malformed). The protocol itself allows at most
        /// <see cref="Wire.MaxCollisionChunks"/> chunks (925696 triangles), so the default is never reached; a smaller value is the receiver's own choice.
        /// </summary>
        public int MaxCollisionTriangles = 2097152;

        public LinkConfig(string game) { Game = game; }
    }
}
