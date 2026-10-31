using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Patodois.RewriteLink
{
    public enum Role { Host, Guest }

    /// <summary>Control-channel messages: one JSON object per line (CONTRACT.md section 8).</summary>
    public abstract class Control
    {
        public static string RoleName(Role r) { return r == Role.Host ? "host" : "guest"; }
        internal static Role Other(Role r) { return r == Role.Host ? Role.Guest : Role.Host; }

        /// <summary>The message as one JSON line, newline included.</summary>
        public abstract string ToLine();

        /// <summary><see cref="Session"/> is null in a guest hello and a nonzero u32 in a host hello.</summary>
        public sealed class Hello : Control
        {
            public Role Role;
            public string Game;
            public uint Protocol, MinProtocol, UdpPort, HeartbeatMs, TimeoutMs;
            public uint? Session;
            /// <summary>Revision 6: the host's token, sent by a guest only (CONTRACT.md 6.2); null = no field. A secret: <see cref="ToString"/> hides it.</summary>
            public string Token;

            public override string ToString() { return "hello " + RoleName(Role) + " " + Json.Quote(Game ?? "") + (Token != null ? " token=<redacted>" : ""); }

            public override string ToLine()
            {
                var b = new StringBuilder();
                b.Append("{\"type\":\"hello\",\"role\":").Append(Json.Quote(RoleName(Role)))
                 .Append(",\"game\":").Append(Json.Quote(Game))
                 .Append(",\"protocol\":").Append(Protocol).Append(",\"min_protocol\":").Append(MinProtocol)
                 .Append(",\"udp_port\":").Append(UdpPort).Append(",\"heartbeat_ms\":").Append(HeartbeatMs)
                 .Append(",\"timeout_ms\":").Append(TimeoutMs);
                if (Session.HasValue) b.Append(",\"session\":").Append(Session.Value);
                if (Token != null) b.Append(",\"token\":").Append(Json.Quote(Token));
                return b.Append("}\n").ToString();
            }
        }

        public sealed class Spawn : Control
        {
            public uint Entity;
            public string Provider, Asset, Name; // Name may be null

            public Spawn() { }
            public Spawn(uint entity, string provider, string asset, string name) { Entity = entity; Provider = provider; Asset = asset; Name = name; }

            public override string ToLine()
            {
                var b = new StringBuilder();
                b.Append("{\"type\":\"spawn\",\"entity\":").Append(Entity).Append(",\"provider\":").Append(Json.Quote(Provider))
                 .Append(",\"asset\":").Append(Json.Quote(Asset));
                if (Name != null) b.Append(",\"name\":").Append(Json.Quote(Name));
                return b.Append("}\n").ToString();
            }
        }

        public sealed class Despawn : Control
        {
            public uint Entity;
            public Despawn(uint entity) { Entity = entity; }
            public override string ToLine() { return "{\"type\":\"despawn\",\"entity\":" + Entity + "}\n"; }
        }

        public sealed class Input : Control
        {
            public Role Focus;
            public string Reason; // may be null
            public Input(Role focus, string reason) { Focus = focus; Reason = reason; }
            public override string ToLine()
            {
                string r = Reason == null ? "" : ",\"reason\":" + Json.Quote(Reason);
                return "{\"type\":\"input\",\"focus\":" + Json.Quote(RoleName(Focus)) + r + "}\n";
            }
        }

        public sealed class FrameChannel : Control
        {
            public string Path;
            public uint Width, Height, Slots;
            public FrameChannel(string path, uint width, uint height, uint slots) { Path = path; Width = width; Height = height; Slots = slots; }
            public override string ToLine()
            {
                return "{\"type\":\"frame_channel\",\"path\":" + Json.Quote(Path) + ",\"width\":" + Width + ",\"height\":" + Height + ",\"slots\":" + Slots + "}\n";
            }
        }

        public sealed class Bye : Control
        {
            public string Reason;
            public Bye(string reason) { Reason = reason; }
            public override string ToLine() { return "{\"type\":\"bye\",\"reason\":" + Json.Quote(Reason) + "}\n"; }
        }

        /// <summary>A "type" this version does not know; ignored by receivers.</summary>
        public sealed class Unknown : Control
        {
            public string Type;
            public Unknown(string type) { Type = type; }
            public override string ToLine() { return "{\"type\":" + Json.Quote(Type) + "}\n"; }
        }

        /// <summary>
        /// Parses one line (with or without its line ending). Unknown types give <see cref="Unknown"/>; a line that is not a JSON object
        /// with a string "type", or a known type with a missing or out-of-range field, throws <see cref="WireException"/>.
        /// </summary>
        public static Control Parse(string line)
        {
            int end = line.Length;
            while (end > 0 && (line[end - 1] == '\n' || line[end - 1] == '\r')) end--;
            if (end != line.Length) line = line.Substring(0, end);
            if (line.Length * 3 >= Wire.MaxLine && Utf8.GetByteCount(line) >= Wire.MaxLine) throw new WireException("bad control line: line too long");
            object v;
            try { v = Json.Parse(line); }
            catch (Json.JsonException e) { throw new WireException("bad control line: " + e.Message); }
            var o = v as Dictionary<string, object>;
            if (o == null) throw new WireException("bad control line: not an object");
            object t;
            if (!o.TryGetValue("type", out t) || !(t is string)) throw new WireException("bad control line: no string \"type\"");
            switch ((string)t)
            {
                case "hello":
                {
                    string role = Str(o, "role");
                    if (role != "host" && role != "guest") throw new WireException("bad control line: role");
                    var h = new Hello
                    {
                        Role = role == "host" ? Role.Host : Role.Guest, Game = Str(o, "game"),
                        Protocol = Int(o, "protocol", 0xFFFF), MinProtocol = Int(o, "min_protocol", 0xFFFF), UdpPort = Int(o, "udp_port", 0xFFFF),
                        HeartbeatMs = Int(o, "heartbeat_ms", uint.MaxValue), TimeoutMs = Int(o, "timeout_ms", uint.MaxValue),
                    };
                    object s;
                    if (o.TryGetValue("session", out s) && s != null) h.Session = Int(o, "session", uint.MaxValue);
                    // any string parses, whatever its length; a host with a token judges it (CONTRACT.md 6.2, 8)
                    h.Token = OptStr(o, "token");
                    return h;
                }
                case "spawn":
                    return new Spawn(Int(o, "entity", uint.MaxValue), Str(o, "provider"), Str(o, "asset"), OptStr(o, "name"));
                case "despawn":
                    return new Despawn(Int(o, "entity", uint.MaxValue));
                case "input":
                {
                    string f = Str(o, "focus");
                    if (f != "host" && f != "guest") throw new WireException("bad control line: focus");
                    return new Input(f == "host" ? Role.Host : Role.Guest, OptStr(o, "reason"));
                }
                case "frame_channel":
                    return new FrameChannel(Str(o, "path"), Int(o, "width", uint.MaxValue), Int(o, "height", uint.MaxValue), Int(o, "slots", uint.MaxValue));
                case "bye":
                    return new Bye(OptStr(o, "reason") ?? "");
                default:
                    return new Unknown((string)t);
            }
        }

        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        /// <summary>Strict: invalid UTF-8 (overlong forms, surrogates, above U+10FFFF) throws, like Rust's from_utf8.</summary>
        internal static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        private static string Str(Dictionary<string, object> o, string k)
        {
            object v;
            var s = o.TryGetValue(k, out v) ? v as string : null;
            if (s == null) throw new WireException("bad control line: missing string \"" + k + "\"");
            return s;
        }

        private static string OptStr(Dictionary<string, object> o, string k)
        {
            object v;
            if (!o.TryGetValue(k, out v) || v == null) return null;
            return Str(o, k);
        }

        private static uint Int(Dictionary<string, object> o, string k, uint max)
        {
            object v;
            if (o.TryGetValue(k, out v) && v is ulong && (ulong)v <= max) return (uint)(ulong)v;
            throw new WireException("bad control line: missing or out-of-range integer \"" + k + "\"");
        }
    }
}
