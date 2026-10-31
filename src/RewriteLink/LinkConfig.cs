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

        /// <summary>
        /// Revision 6 (CONTRACT.md 6.2), default null. Host: the token a guest must show in its hello (1 to 256 bytes of UTF-8, no U+0000; creating the
        /// link throws <see cref="System.ArgumentException"/> otherwise). Guest: the token it sends. A secret; <see cref="ToString"/> hides it.
        /// </summary>
        public string Token;
        /// <summary>
        /// Revision 6 (CONTRACT.md 6.2), default null. Host: after the control port is bound the link writes this file with the port it bound and the token
        /// (a new one from <see cref="Tokens.Generate"/> if <see cref="Token"/> is null, and then the host requires it), and deletes it when the link is
        /// closed; if the file cannot be written, creating the host throws <see cref="EndpointException"/>. Guest: before every connection attempt it reads
        /// this file and uses its port and token instead of <see cref="ControlPort"/> and <see cref="Token"/>; when the file cannot be read it does not
        /// attempt this time, and tries again after <see cref="ReconnectMs"/>.
        /// </summary>
        public string EndpointFile;

        public LinkConfig(string game) { Game = game; }

        public override string ToString()
        {
            return "LinkConfig { Game = " + Game + ", ControlPort = " + ControlPort + ", UdpPort = " + UdpPort + ", Token = " + (Token == null ? "none" : "<redacted>")
                + ", EndpointFile = " + (EndpointFile ?? "none") + " }";
        }
    }
}
