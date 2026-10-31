using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static Patodois.RewriteLink.Tests.T;

namespace Patodois.RewriteLink.Tests
{
    /// <summary>Revision 6 (CONTRACT.md 6.2): the token, its comparison, the endpoint file and a live host or guest that uses them. The vectors cover the wire; these cover the rest.</summary>
    internal static class TokenTests
    {
        private static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }
        private static byte[] Bytes(int n, byte v) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = v; return b; }

        private static bool IsLowerHex64(string s) { return s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }

        private static string NewDir()
        {
            string d = TempPath("dir");
            Directory.CreateDirectory(d);
            return d;
        }

        private static string Names(string dir) { return string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(x => x)); }

        public static void Run()
        {
            Test("token: constant-time comparison gives the right answers", () =>
            {
                Check(Tokens.Equal(B("secret"), B("secret")), "equal");
                Check(!Tokens.Equal(B("Xecret"), B("secret")), "differ at the first byte");
                Check(!Tokens.Equal(B("secreX"), B("secret")), "differ at the last byte");
                Check(!Tokens.Equal(B("secre"), B("secret")), "a prefix");
                Check(!Tokens.Equal(B("secret"), B("secre")), "the other way round");
                Check(!Tokens.Equal(B("secret0"), B("secret")), "longer");
                Check(!Tokens.Equal(B("secret"), B("secret\0")), "a trailing NUL is a different length, not padding");
                Check(Tokens.Equal(new byte[0], new byte[0]), "both empty is true here (a handshake never reaches it)");
                Check(!Tokens.Equal(new byte[0], B("a")), "empty against one byte");
                Check(Tokens.Equal(Bytes(256, 7), Bytes(256, 7)), "256 and 256, equal");
                Check(!Tokens.Equal(Bytes(257, 7), Bytes(257, 7)), "257 and 257 with equal content is false");
                Check(!Tokens.Equal(Bytes(257, 7), Bytes(256, 7)), "257 against its first 256");
                Check(!Tokens.Equal(Bytes(256, 7), Bytes(257, 7)), "and the other way round");
                var late = Bytes(256, 7); late[255] = 8;
                Check(!Tokens.Equal(late, Bytes(256, 7)), "differ at byte 256");
                Throws<ArgumentNullException>(() => Tokens.Equal(null, B("a")), "null");
            });

            Test("token: Generate gives 64 lowercase hex digits, never the same twice", () =>
            {
                string a = Tokens.Generate(), b = Link.GenerateToken();
                Check(IsLowerHex64(a) && IsLowerHex64(b), "64 lowercase hex digits");
                Check(a != b, "two calls differ");
                var seen = new HashSet<string>();
                for (int i = 0; i < 200; i++) Check(seen.Add(Tokens.Generate()), "no repeats in 200 tokens");
            });

            Test("token: a configured token of the wrong shape fails with an ArgumentException that does not show it", () =>
            {
                string e256 = new string('é', 128), e258 = new string('é', 129);
                var bad = new Dictionary<string, string>
                {
                    { "empty", "" }, { "257 bytes", new string('t', 257) }, { "258 bytes in 129 characters", e258 }, { "U+0000", "ab\0cd-SECRET" }, { "lone surrogate", "SECRET\ud800" },
                };
                foreach (var kv in bad)
                {
                    foreach (bool host in new[] { true, false })
                    {
                        var cfg = new LinkConfig("t") { ControlPort = 0, Token = kv.Value };
                        try { (host ? Link.Host(cfg) : Link.Guest(cfg)).Dispose(); throw new Exception(kv.Key + ": accepted (" + (host ? "host" : "guest") + ")"); }
                        catch (ArgumentException e) { Check(!e.Message.Contains("SECRET") && (kv.Value.Length < 8 || !e.Message.Contains(kv.Value)), kv.Key + ": the message shows the token"); }
                    }
                }
                foreach (string ok in new[] { "a", new string('t', 256), e256, "\U0001F600" })
                {
                    using (Link.Host(new LinkConfig("t") { ControlPort = 0, Token = ok })) { }
                    using (Link.Guest(new LinkConfig("t") { ControlPort = 1, Token = ok })) { }
                }
                Throws<ArgumentException>(() => Link.Host(new LinkConfig("t") { ControlPort = 0, EndpointFile = "" }), "an empty endpoint file path");
            });

            Test("endpoint: write and read round trip, with the runtime's calls and with libc's", () =>
            {
                if (OperatingSystem.IsWindows()) return; // the Unix paths; Windows is untested here
                foreach (bool forced in new[] { false, true })
                {
                    Posix.ForcePInvoke = forced;
                    try
                    {
                        string dir = NewDir(), file = Path.Combine(dir, "game.json");
                        string tok = Tokens.Generate();
                        Endpoint.Write(file, 47800, tok);
                        Eq(Posix.LastPath, forced ? "libc" : "runtime", "which calls were used");
                        Eq(File.ReadAllText(file), "{\"control_port\":47800,\"token\":\"" + tok + "\"}\n", "the bytes");
                        Eq(Names(dir), "game.json", "no temporary file or directory is left behind");
                        Eq(File.GetUnixFileMode(file), UnixFileMode.UserRead | UnixFileMode.UserWrite, "mode 0600");
                        var info = Endpoint.Read(file);
                        Eq(info.ControlPort, 47800, "port"); Eq(info.Token, tok, "token");
                        Check(!info.ToString().Contains(tok), "ToString hides the token");

                        // an existing file is replaced, not appended to, and stays private
                        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("0644", 8));
                        string tok2 = Tokens.Generate();
                        Endpoint.Write(file, 1234, tok2);
                        info = Endpoint.Read(file);
                        Eq(info.ControlPort, 1234, "replaced port"); Eq(info.Token, tok2, "replaced token");
                        Eq(File.GetUnixFileMode(file), UnixFileMode.UserRead | UnixFileMode.UserWrite, "the replacement is 0600 again");
                        Eq(Names(dir), "game.json", "still nothing left behind");

                        // the reader refuses what the group or others can read
                        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("0644", 8));
                        try { Endpoint.Read(file); throw new Exception("a 0644 file was accepted"); }
                        catch (EndpointException e) { Check(!e.Message.Contains(tok2), "the message shows the token"); }
                        File.SetUnixFileMode(file, UnixFileMode.UserRead);
                        Eq(Endpoint.Read(file).Token, tok2, "0400 is fine");

                        // a path in a directory that does not exist, and a target that is a directory: errors without the token, nothing left behind
                        try { Endpoint.Write(Path.Combine(dir, "nope", "x.json"), 1, tok); throw new Exception("written into a missing directory"); }
                        catch (EndpointException e) { Check(!e.Message.Contains(tok), "the message shows the token"); }
                        string sub = Path.Combine(dir, "adir");
                        Directory.CreateDirectory(sub);
                        try { Endpoint.Write(sub, 1, tok); throw new Exception("replaced a directory"); }
                        catch (EndpointException e) { Check(!e.Message.Contains(tok), "the message shows the token"); }
                        Eq(Names(dir), "adir,game.json", "failed writes leave nothing behind");
                        // a symbolic link to the file is followed
                        string link = Path.Combine(dir, "link.json");
                        File.CreateSymbolicLink(link, file);
                        Eq(Endpoint.Read(link).Token, tok2, "read through a symbolic link");
                        File.Delete(link);
                        Check(Endpoint.Delete(file), "Delete removes it");
                        Check(!Endpoint.Delete(file), "Delete of a missing file is false, not an error");
                    }
                    finally { Posix.ForcePInvoke = false; }
                }
            });

            Test("endpoint: Write refuses a bad port or token without showing the token", () =>
            {
                string file = Path.Combine(NewDir(), "e.json");
                Throws<ArgumentOutOfRangeException>(() => Endpoint.Write(file, 0, "a"), "port 0");
                Throws<ArgumentOutOfRangeException>(() => Endpoint.Write(file, 65536, "a"), "port 65536");
                Throws<ArgumentNullException>(() => Endpoint.Write(file, 1, null), "no token");
                Throws<ArgumentException>(() => Endpoint.Write(file, 1, ""), "empty token");
                Throws<ArgumentException>(() => Endpoint.Write(file, 1, new string('t', 257)), "257 bytes");
                Throws<ArgumentException>(() => Endpoint.Write(file, 1, "a\0b"), "NUL");
                Check(!File.Exists(file), "nothing was written");
                Endpoint.Write(file, 65535, new string('é', 128)); // 256 bytes
                Eq(Endpoint.Read(file).Token, new string('é', 128), "the longest token round-trips");
                Endpoint.Write(file, 1, "q\"\\\n\t日\U0001F600"); // everything the JSON writer has to escape
                Eq(Endpoint.Read(file).Token, "q\"\\\n\t日\U0001F600", "an awkward token round-trips");
            });

            Test("endpoint: a host with an endpoint file writes it with the bound port, a guest uses it, closing deletes it", () =>
            {
                string dir = NewDir(), file = Path.Combine(dir, "link.json");
                var host = Link.Host(new LinkConfig("Token Host") { ControlPort = 0, EndpointFile = file, TimeoutMs = 4000 });
                Link guest = null;
                try
                {
                    Check(File.Exists(file), "the file exists as soon as the host is created");
                    var info = Endpoint.Read(file);
                    Eq(info.ControlPort, host.ControlPort, "the port the host actually bound");
                    Check(host.ControlPort != 0, "a real port");
                    Check(IsLowerHex64(info.Token), "a generated token: 64 lowercase hex digits");
                    if (!OperatingSystem.IsWindows()) Eq(File.GetUnixFileMode(file), UnixFileMode.UserRead | UnixFileMode.UserWrite, "mode 0600");

                    // a guest that does not know the port or the token, only the file
                    var he = new List<LinkEvent>(); var ge = new List<LinkEvent>();
                    guest = Link.Guest(new LinkConfig("Token Guest") { ControlPort = 1, EndpointFile = file, ReconnectMs = 100, TimeoutMs = 4000 });
                    Check(Until(3000, () => { host.Poll(he); guest.Poll(ge); return host.IsConnected && guest.IsConnected; }), "the guest connects through the file");
                    Eq(guest.Peer.Game, "Token Host", "peer");

                    // the host requires its generated token: a raw guest without it, and with another one, is answered "unauthorized"
                    guest.Dispose(); guest = null;
                    Check(Until(2000, () => { host.Poll(he); return !host.IsConnected; }), "the host notices the guest left");
                    foreach (string tokenField in new[] { "", ",\"token\":\"" + new string('0', 64) + "\"" })
                    {
                        using (var raw = new Raw(host.ControlPort))
                        {
                            raw.Send(Raw.HelloLine("guest", 1, 1, raw.UdpPort, 250).Replace("}\n", tokenField + "}\n"));
                            Eq(raw.WaitLine(host, 2000), "{\"type\":\"bye\",\"reason\":\"unauthorized\"}", "the answer");
                        }
                    }
                    he.Clear();
                    host.Poll(he);
                    using (var raw = new Raw(host.ControlPort))
                    {
                        raw.Send(Raw.HelloLine("guest", 1, 1, raw.UdpPort, 250).Replace("}\n", ",\"token\":\"" + info.Token + "\"}\n"));
                        Check(raw.WaitLine(host, 2000).Contains("\"role\":\"host\""), "the right token gets the host's hello");
                    }
                }
                finally
                {
                    if (guest != null) guest.Dispose();
                    host.Dispose();
                }
                Check(!File.Exists(file), "the file is gone after the host is closed");
                Eq(Names(dir), "", "and nothing else is in the directory");
            });

            Test("endpoint: a host whose endpoint file cannot be written fails, closes its port and shows no token", () =>
            {
                int port;
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    port = ((IPEndPoint)probe.LocalEndPoint).Port;
                }
                string file = Path.Combine(NewDir(), "no-such-dir", "link.json");
                try
                {
                    Link.Host(new LinkConfig("x") { ControlPort = port, Token = "TOPSECRETTOKEN", EndpointFile = file });
                    throw new Exception("the host was created");
                }
                catch (EndpointException e) { Check(!e.Message.Contains("TOPSECRETTOKEN"), "the message shows the token"); }
                // the control listener was closed: the port can be bound again
                using (var again = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    again.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    again.Listen(1);
                }
            });

            Test("endpoint: a configured token is used, and a file that cannot be written is the only thing that stops the host", () =>
            {
                string file = Path.Combine(NewDir(), "link.json");
                using (var host = Link.Host(new LinkConfig("h") { ControlPort = 0, Token = "my-own-token", EndpointFile = file }))
                {
                    Eq(Endpoint.Read(file).Token, "my-own-token", "the configured token goes into the file");
                    using (var g = Link.Guest(new LinkConfig("g") { ControlPort = 1, EndpointFile = file, ReconnectMs = 50 }))
                        Check(Until(3000, () => { host.Poll(); g.Poll(); return host.IsConnected && g.IsConnected; }), "and a guest with the file connects");
                }
            });

            Test("endpoint: a guest without a file makes no attempt and no event, then connects when the host's file appears; a restarted host is followed", () =>
            {
                string dir = NewDir(), file = Path.Combine(dir, "link.json");
                var ge = new List<LinkEvent>();
                using (var guest = Link.Guest(new LinkConfig("g") { ControlPort = 1, EndpointFile = file, ReconnectMs = 50, TimeoutMs = 4000 }))
                {
                    Until(400, () => { guest.Poll(ge); return false; });
                    Eq(ge.Count, 0, "no event while there is no file");
                    int port1, port2;
                    string tok1, tok2;
                    var he = new List<LinkEvent>();
                    using (var h1 = Link.Host(new LinkConfig("h") { ControlPort = 0, EndpointFile = file, TimeoutMs = 4000 }))
                    {
                        port1 = h1.ControlPort; tok1 = Endpoint.Read(file).Token;
                        Check(Until(3000, () => { h1.Poll(he); guest.Poll(ge); return h1.IsConnected && guest.IsConnected; }), "connects to the first host");
                    }
                    Check(!File.Exists(file), "the first host removed its file");
                    Check(Until(3000, () => { guest.Poll(ge); return !guest.IsConnected; }), "the guest notices the first host is gone");
                    using (var h2 = Link.Host(new LinkConfig("h") { ControlPort = 0, EndpointFile = file, TimeoutMs = 4000 }))
                    {
                        port2 = h2.ControlPort; tok2 = Endpoint.Read(file).Token;
                        Check(tok1 != tok2, "a new run makes a new token");
                        Check(Until(3000, () => { h2.Poll(he); guest.Poll(ge); return h2.IsConnected && guest.IsConnected; }), "and the guest follows it with the new token (port " + (port1 == port2 ? "same" : "new") + ")");
                    }
                    Check(ge.OfType<LinkEvent.HandshakeFailed>().All(e => !e.Reason.Contains(tok1) && !e.Reason.Contains(tok2)), "no event shows a token");
                }
            });

            Test("token: a host answers a wrong or missing token with the word unauthorized only, and nothing shows either token", () =>
            {
                const string Secret = "host-token-SECRET-1", Wrong = "guest-token-WRONG-2";
                var cfg = new LinkConfig("Secret Host") { ControlPort = 0, TimeoutMs = 4000, Token = Secret };
                Check(!cfg.ToString().Contains(Secret), "the config's string form hides the token");
                var he = new List<LinkEvent>();
                using (var host = Link.Host(cfg))
                {
                    // a raw guest with the wrong token, a role and a version that are wrong too: only the word comes back
                    using (var raw = new Raw(host.ControlPort))
                    {
                        raw.Send("{\"type\":\"hello\",\"role\":\"host\",\"game\":\"raw\",\"protocol\":7,\"min_protocol\":7,\"udp_port\":0,\"heartbeat_ms\":0,\"timeout_ms\":2000,\"token\":\"" + Wrong + "\"}\n");
                        string line = null;
                        Until(2000, () => { host.Poll(he); raw.Pump(); line = raw.Line(); return line != null; });
                        Eq(line, "{\"type\":\"bye\",\"reason\":\"unauthorized\"}", "the bye");
                        Check(Until(2000, () => { host.Poll(he); raw.Pump(); return raw.Eof; }), "and the host closes");
                        Check(!raw.AllText().Contains(Secret) && !raw.AllText().Contains(Wrong), "nothing else was sent");
                    }
                    var failed = he.OfType<LinkEvent.HandshakeFailed>().ToList();
                    Eq(failed.Count, 1, "one handshake-failed event");
                    Eq(failed[0].Reason, "unauthorized", "its reason is the word");
                    Eq(he.OfType<LinkEvent.Connected>().Count(), 0, "no session");

                    // the library's own guest with the wrong token: it learns the word and keeps its own token out of everything
                    var gcfg = new LinkConfig("Wrong Guest") { ControlPort = host.ControlPort, TimeoutMs = 4000, Token = Wrong, ReconnectMs = 100 };
                    Check(!gcfg.ToString().Contains(Wrong), "the guest config's string form hides the token");
                    var ge = new List<LinkEvent>();
                    using (var guest = Link.Guest(gcfg))
                    {
                        Check(Until(3000, () => { host.Poll(he); guest.Poll(ge); return ge.OfType<LinkEvent.HandshakeFailed>().Any(); }), "the guest's handshake fails");
                        Check(ge.OfType<LinkEvent.HandshakeFailed>().First().Reason.Contains("unauthorized"), "and says why");
                        Check(!guest.IsConnected && !host.IsConnected, "no session");
                    }
                    foreach (LinkEvent e in he.Concat(ge))
                    {
                        string text = e is LinkEvent.HandshakeFailed ? ((LinkEvent.HandshakeFailed)e).Reason : e is LinkEvent.Lost ? ((LinkEvent.Lost)e).Reason : "";
                        Check(!text.Contains(Secret) && !text.Contains(Wrong), "an event reason shows a token");
                    }

                    // the same guest with the right token gets in
                    gcfg.Token = Secret;
                    using (var guest = Link.Guest(gcfg))
                        Check(Until(3000, () => { host.Poll(); guest.Poll(); return host.IsConnected && guest.IsConnected; }), "the right token connects");
                }
            });

            Test("token: a token that starts with the host's but is longer is not accepted", () =>
            {
                string t256 = new string('t', 256);
                using (var host = Link.Host(new LinkConfig("h") { ControlPort = 0, Token = t256 }))
                {
                    foreach (string shown in new[] { t256 + "t", t256 + "tttt", t256.Substring(1), new string('t', 255) + "u" })
                        using (var raw = new Raw(host.ControlPort))
                        {
                            raw.Send(Raw.HelloLine("guest", 1, 1, raw.UdpPort, 250).Replace("}\n", ",\"token\":\"" + shown + "\"}\n"));
                            Eq(raw.WaitLine(host, 2000), "{\"type\":\"bye\",\"reason\":\"unauthorized\"}", "unauthorized for " + shown.Length + " bytes");
                        }
                    using (var raw = new Raw(host.ControlPort))
                    {
                        raw.Send(Raw.HelloLine("guest", 1, 1, raw.UdpPort, 250).Replace("}\n", ",\"token\":\"" + t256 + "\"}\n"));
                        Check(raw.WaitLine(host, 2000).Contains("\"type\":\"hello\""), "the 256 bytes themselves are accepted");
                    }
                }
            });

            Test("token: the hello codec writes the token only when there is one, and hides it in ToString", () =>
            {
                var h = new Control.Hello { Role = Role.Guest, Game = "g", Protocol = 1, MinProtocol = 1, UdpPort = 5, HeartbeatMs = 250, TimeoutMs = 2000 };
                Check(!h.ToLine().Contains("token"), "no field without a token");
                h.Token = "abc\"d";
                Check(h.ToLine().EndsWith(",\"token\":\"abc\\\"d\"}\n", StringComparison.Ordinal), "escaped, last");
                Check(!h.ToString().Contains("abc"), "ToString");
                var back = (Control.Hello)Control.Parse(h.ToLine());
                Eq(back.Token, "abc\"d", "parsed back");
                Eq(((Control.Hello)Control.Parse(new Control.Hello { Role = Role.Host, Game = "g", Protocol = 1, MinProtocol = 1, UdpPort = 5, HeartbeatMs = 250, TimeoutMs = 2000, Session = 1 }.ToLine())).Token, null, "a host's hello has none");
            });
        }
    }
}
