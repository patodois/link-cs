using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Patodois.RewriteLink.Tests.T;

namespace Patodois.RewriteLink.Tests
{
    internal static class ControlTests
    {
        // The documents and whether serde_json (the Rust side) accepts them: the table of link-java's tests and link-c's.
        private static readonly object[][] JsonCorpus =
        {
            new object[] { "0", true }, new object[] { "-0", true }, new object[] { "-1", true }, new object[] { "1.5", true }, new object[] { "1e5", true },
            new object[] { "1E+5", true }, new object[] { "1e-5", true }, new object[] { "0e0", true }, new object[] { "-0.0", true }, new object[] { "1e-999", true },
            new object[] { "123456789012345678901234567890", true }, new object[] { "18446744073709551615", true }, new object[] { "18446744073709551616", true },
            new object[] { "01", false }, new object[] { "-01", false }, new object[] { "00", false }, new object[] { "1.", false }, new object[] { "1.e5", false },
            new object[] { "-.5", false }, new object[] { ".5", false }, new object[] { "1e", false }, new object[] { "1.0e+", false }, new object[] { "+1", false },
            new object[] { "-", false }, new object[] { "- 1", false }, new object[] { "0x10", false }, new object[] { "1e999", false }, new object[] { "Infinity", false },
            new object[] { "NaN", false },
            new object[] { "\"\\u0041\"", true }, new object[] { "\"\\u00E9\"", true }, new object[] { "\"\\u0000\"", false }, new object[] { "\"\\/\"", true },
            // revision 5: U+0000 is an error however it is spelled (and u0000 after an escaped backslash is plain text; \u0001 is not NUL)
            new object[] { "\"\\\\u0000\"", true }, new object[] { "\"\\u0001\"", true }, new object[] { "\"\\u00000\"", false }, new object[] { "\"a\\u0000b\"", false },
            new object[] { "{\"\\u0000\":1}", false }, new object[] { "{\"a\":[1,{\"b\":\"\\u0000\"}]}", false }, new object[] { "{\"a\":\"\\u0000\",\"a\":1}", false },
            new object[] { "\"\\ud800\\udc00\"", true }, new object[] { "\"\\ud83d\\ude00\"", true }, new object[] { "\"\\uD834\\uDD1E\"", true },
            new object[] { "\"\\u004\"", false }, new object[] { "\"\\u00zz\"", false }, new object[] { "\"\\u+041\"", false }, new object[] { "\"\\u-041\"", false },
            new object[] { "\"\\u D800\"", false }, new object[] { "\"\\ud800\"", false }, new object[] { "\"\\udc00\"", false }, new object[] { "\"\\ud800x\"", false },
            new object[] { "\"\\ud800\\u0041\"", false }, new object[] { "\"\\ud800\\ud800\"", false }, new object[] { "\"\\udc00\\ud800\"", false },
            new object[] { "\"\\x41\"", false }, new object[] { "\"\\U0041\"", false }, new object[] { "\"a\nb\"", false },
            new object[] { "true", true }, new object[] { "[true,false,null]", true }, new object[] { "\t1\n", true }, new object[] { "truex", false },
            new object[] { "tru", false }, new object[] { "[1,]", false }, new object[] { "[,1]", false }, new object[] { "{\"a\":1,}", false },
            new object[] { "{\"a\":1 \"b\":2}", false }, new object[] { "{'a':1}", false }, new object[] { "\uFEFF1", false },
        };

        public static void Run()
        {
            Test("JSON: accepts and rejects what serde_json does", () =>
            {
                foreach (object[] row in JsonCorpus)
                {
                    string doc = (string)row[0];
                    bool ok = (bool)row[1], got;
                    try { Json.Parse(doc); got = true; } catch (Json.JsonException) { got = false; }
                    Check(got == ok, "\"" + doc.Replace("\n", "\\n") + "\" should be " + (ok ? "accepted" : "rejected"));
                }
                foreach (int n in new[] { 1, 32, 33, 100, 127, 128, 200 })
                {
                    string arrays = new string('[', n) + new string(']', n);
                    string objects = string.Concat(Enumerable.Repeat("{\"a\":", n)) + "1" + new string('}', n);
                    foreach (string doc in new[] { arrays, objects })
                    {
                        bool got;
                        try { Json.Parse(doc); got = true; } catch (Json.JsonException) { got = false; }
                        Eq(got, n <= 127, "nesting " + n);
                    }
                }
            });

            Test("control: the contract's examples parse", () =>
            {
                var g = (Control.Hello)Control.Parse("{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"Warframe rewrite\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":53001,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n");
                Check(g.Role == Role.Guest && g.Game == "Warframe rewrite" && g.Protocol == 1 && g.UdpPort == 53001 && g.HeartbeatMs == 250 && !g.Session.HasValue, "guest hello");
                var h = (Control.Hello)Control.Parse("{\"type\":\"hello\",\"role\":\"host\",\"game\":\"War Thunder rewrite\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":53000,\"heartbeat_ms\":250,\"timeout_ms\":2000,\"session\":2882400001}\r\n");
                Eq(h.Session.Value, 2882400001u, "session");
                var sp = (Control.Spawn)Control.Parse("{\"type\":\"spawn\",\"entity\":7,\"provider\":\"Warframe\",\"asset\":\"/Lotus/Characters/Excalibur\",\"name\":\"Excalibur\"}");
                Check(sp.Entity == 7 && sp.Provider == "Warframe" && sp.Asset == "/Lotus/Characters/Excalibur" && sp.Name == "Excalibur", "spawn");
                Eq(((Control.Despawn)Control.Parse("{\"type\":\"despawn\",\"entity\":7}")).Entity, 7u, "despawn");
                var inp = (Control.Input)Control.Parse("{\"type\":\"input\",\"focus\":\"guest\"}");
                Check(inp.Focus == Role.Guest && inp.Reason == null, "input");
                Eq(((Control.Bye)Control.Parse("{\"type\":\"bye\",\"reason\":\"shutting down\"}")).Reason, "shutting down", "bye");
                Eq(((Control.Bye)Control.Parse("{\"type\":\"bye\"}")).Reason, "", "bye without a reason");
                var fc = (Control.FrameChannel)Control.Parse("{\"type\":\"frame_channel\",\"path\":\"/tmp/x.frames\",\"width\":1280,\"height\":720,\"slots\":3}");
                Check(fc.Path == "/tmp/x.frames" && fc.Width == 1280 && fc.Height == 720 && fc.Slots == 3, "frame_channel");
                Eq(((Control.Unknown)Control.Parse("{\"type\":\"future_thing\",\"x\":1}")).Type, "future_thing", "unknown type");
                Eq(((Control.Despawn)Control.Parse("{\"type\":\"despawn\",\"entity\":7,\"extra\":[1,{\"a\":null}]}")).Entity, 7u, "unknown fields are ignored");
                Eq(((Control.Despawn)Control.Parse("{\"type\":\"despawn\",\"entity\":1,\"entity\":2}")).Entity, 2u, "the last of a repeated key wins");
                Check(!((Control.Hello)Control.Parse("{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"g\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":5,\"heartbeat_ms\":250,\"timeout_ms\":2000,\"session\":null}")).Session.HasValue, "null session");
                Check(Control.Parse("{\"type\":\"hello\\\\u0000x\"}") is Control.Unknown, "a type of a backslash and u0000 is an unknown type, not a NUL");
                var esc = (Control.Spawn)Control.Parse("{\"type\":\"spawn\",\"entity\":1,\"provider\":\"a\\\"b\",\"asset\":\"\\u00e9\\ud83d\\ude00\\n\"}");
                Check(esc.Provider == "a\"b" && esc.Asset == "\u00e9\U0001F600\n", "escapes decode");
                Eq(((Control.Despawn)Control.Parse("{\"type\":\"despawn\",\"entity\":4294967295}")).Entity, uint.MaxValue, "the largest entity id");
            });

            Test("control: broken lines are protocol errors", () =>
            {
                foreach (string bad in new[]
                {
                    "not json", "[]", "{\"no\":\"type\"}", "{\"type\":5}", "{\"type\":\"despawn\"}", "{\"type\":\"despawn\",\"entity\":4294967296}",
                    "{\"type\":\"despawn\",\"entity\":-1}", "{\"type\":\"despawn\",\"entity\":1.5}", "{\"type\":\"despawn\",\"entity\":-0}", "{\"type\":\"despawn\",\"entity\":1e2}",
                    "{\"type\":\"despawn\",\"entity\":\"7\"}", "{\"type\":\"input\",\"focus\":\"both\"}",
                    "{\"type\":\"hello\",\"role\":\"host\",\"game\":\"g\",\"protocol\":70000,\"min_protocol\":1,\"udp_port\":1,\"heartbeat_ms\":1,\"timeout_ms\":1}",
                    "{\"type\":\"hello\",\"role\":\"host\",\"game\":\"g\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":65536,\"heartbeat_ms\":1,\"timeout_ms\":1}",
                    "{\"type\":\"hello\",\"role\":\"nobody\",\"game\":\"g\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":1,\"heartbeat_ms\":1,\"timeout_ms\":1}",
                    "{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\"}", "{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\",\"asset\":\"a\",\"name\":5}",
                    "{\"type\":\"bye\",\"reason\":5}", "{\"type\":\"frame_channel\",\"path\":\"/x\",\"width\":1,\"height\":1}",
                    "{\"type\":\"despawn\",\"entity\":1} trailing", "{\"type\":\"despawn\",\"entity\":1}{", "", "   ", "{", "}", "\"type\"", "null",
                })
                {
                    Throws<WireException>(() => Control.Parse(bad), bad);
                }
                string padded = "{\"type\":\"x\"}" + new string(' ', Wire.MaxLine - 1 - 12);
                Check(Control.Parse(padded) is Control.Unknown, "65535 bytes is fine");
                Throws<WireException>(() => Control.Parse(padded + " "), "65536 bytes is too long");
            });

            Test("control: a U+0000 escape is a protocol error anywhere in a line (revision 5)", () =>
            {
                foreach (string bad in new[]
                {
                    "{\"type\":\"hello\\u0000x\"}",
                    "{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\",\"asset\":\"a\\u0000b\"}",
                    "{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\",\"asset\":\"a\",\"name\":\"\\u0000\"}",
                    "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"g\\u0000\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":5,\"heartbeat_ms\":250,\"timeout_ms\":2000}",
                    "{\"type\":\"bye\",\"reason\":\"\\u0000\"}", "{\"type\":\"input\",\"focus\":\"host\",\"reason\":\"x\\u0000\"}",
                    "{\"type\":\"frame_channel\",\"path\":\"/tmp/a\\u0000b\",\"width\":1,\"height\":1,\"slots\":1}",
                    "{\"type\":\"despawn\",\"entity\":1,\"x\":\"\\u0000\"}", "{\"type\":\"despawn\",\"entity\":1,\"a\\u0000b\":1}",
                    "{\"type\":\"despawn\",\"entity\":1,\"x\":{\"y\":[\"\\u0000\"]}}",
                    "{\"type\":\"despawn\",\"entity\":1,\"x\":\"\\u0000\",\"x\":1}", // the value that ends up in the line is fine, the one overridden is not
                    "{\"type\":\"despawn\",\"entity\":1,\"x\":\"\\ud83d\\u0000\"}",
                })
                    Throws<WireException>(() => Control.Parse(bad), bad);
                // not NUL: the characters of an escaped backslash followed by u0000, and a raw U+0001 escape
                Eq(((Control.Spawn)Control.Parse("{\"type\":\"spawn\",\"entity\":1,\"provider\":\"p\",\"asset\":\"a\\\\u0000b\\u0001\"}")).Asset, "a\\u0000b\u0001", "an escaped backslash before u0000 is not a NUL");
            });

            Test("control: a string handed to the writer has U+0000 replaced by U+FFFD (revision 5)", () =>
            {
                string nul = "a\0b\0", fixedUp = "a\uFFFDb\uFFFD";
                var all = new Control[]
                {
                    new Control.Hello { Role = Role.Guest, Game = nul, Protocol = 1, MinProtocol = 1, UdpPort = 5, HeartbeatMs = 250, TimeoutMs = 2000 },
                    new Control.Spawn(1, nul, nul, nul), new Control.Input(Role.Host, nul), new Control.FrameChannel(nul, 4, 2, 3), new Control.Bye(nul), new Control.Unknown(nul),
                };
                foreach (Control c in all)
                {
                    string line = c.ToLine();
                    Check(line.IndexOf('\0') < 0 && line.IndexOf("\\u0000", StringComparison.Ordinal) < 0, "no NUL in " + c.GetType().Name + ": " + line);
                    Check(line.Contains(fixedUp), "U+FFFD in its place in " + c.GetType().Name + ": " + line);
                    Control.Parse(line); // and a receiver takes what the sender wrote
                }
                var sp = (Control.Spawn)Control.Parse(new Control.Spawn(1, nul, "x\0", null).ToLine());
                Check(sp.Provider == fixedUp && sp.Asset == "x\uFFFD" && sp.Name == null, "the receiver reads the substitute");
                Eq(((Control.Bye)Control.Parse(new Control.Bye("\0").ToLine())).Reason, "\uFFFD", "bye reason");
            });

            Test("control: lines round trip through the writer", () =>
            {
                var all = new Control[]
                {
                    new Control.Hello { Role = Role.Host, Game = "Host \"one\"\\", Protocol = 1, MinProtocol = 1, UdpPort = 53000, HeartbeatMs = 250, TimeoutMs = 2000, Session = 0xABCD0001u },
                    new Control.Spawn(1, "a\"b", "c\\d\n\t\u0001", null), new Control.Spawn(2, "p", "a", "name \u00e9"), new Control.Despawn(4294967295u),
                    new Control.Input(Role.Host, "why"), new Control.Input(Role.Guest, null), new Control.Bye("\u00e9 \u00fc \u65e5\u672c \U0001F600"),
                    new Control.FrameChannel("/tmp/a b.frames", 64, 32, 3), new Control.Unknown("future"),
                };
                foreach (Control c in all)
                {
                    string line = c.ToLine();
                    Check(line.EndsWith("\n") && line.IndexOf('\n') == line.Length - 1, "one line");
                    Eq(Control.Parse(line).ToLine(), line, "round trip " + line.Trim());
                }
            });

            Test("control: UTF-8 is strict like Rust's from_utf8", () =>
            {
                var strict = Control.Utf8Strict;
                foreach (var ok in new[] { "abc", "\u00e9", "\u20ac", "\U0001F600" }) strict.GetString(Encoding.UTF8.GetBytes(ok));
                foreach (var bad in new[] { new byte[] { 0xC0, 0x80 }, new byte[] { 0xC1, 0xBF }, new byte[] { 0xE0, 0x80, 0x80 }, new byte[] { 0xED, 0xA0, 0x80 },
                    new byte[] { 0xF4, 0x90, 0x80, 0x80 }, new byte[] { 0xF5, 0x80, 0x80, 0x80 }, new byte[] { 0x80 }, new byte[] { 0xC3 }, new byte[] { 0xE2, 0x82 }, new byte[] { 0xFF } })
                    Throws<DecoderFallbackException>(() => strict.GetString(bad), BitConverter.ToString(bad));
            });

            Test("fuzz: 100000 mutated control lines only ever throw WireException", () =>
            {
                string[] seeds =
                {
                    "{\"type\":\"hello\",\"role\":\"guest\",\"game\":\"G\",\"protocol\":1,\"min_protocol\":1,\"udp_port\":5,\"heartbeat_ms\":250,\"timeout_ms\":2000}\n",
                    "{\"type\":\"spawn\",\"entity\":7,\"provider\":\"P\",\"asset\":\"/a/b\\u00e9\",\"name\":\"n\"}\n",
                    "{\"type\":\"frame_channel\",\"path\":\"/tmp/x\",\"width\":1,\"height\":2,\"slots\":3}\n", "{\"type\":\"bye\",\"reason\":\"r\"}\n",
                    "{\"type\":\"input\",\"focus\":\"host\",\"extra\":[1,2,{\"a\":[]}]}\n",
                };
                var rnd = new Random(7);
                const string alphabet = "{}[]\",:\\ue0-+.9";
                long ok = 0, bad = 0;
                for (int i = 0; i < 100000; i++)
                {
                    var sb = new StringBuilder(seeds[rnd.Next(5)]);
                    int flips = 1 + rnd.Next(4);
                    for (int k = 0; k < flips && sb.Length > 1; k++)
                    {
                        int at = rnd.Next(sb.Length);
                        switch (rnd.Next(4))
                        {
                            case 0: sb[at] = (char)rnd.Next(0x250); break;
                            case 1: sb[at] = alphabet[rnd.Next(alphabet.Length)]; break;
                            case 2: sb.Remove(at, 1); break;
                            default: sb.Insert(at, (char)rnd.Next(0x250)); break;
                        }
                    }
                    string line = sb.ToString();
                    if (rnd.Next(7) == 0) line = line.Substring(0, rnd.Next(line.Length + 1));
                    try { Control.Parse(line); ok++; } catch (WireException) { bad++; }
                }
                Console.WriteLine("     fuzz: " + ok + " lines parsed, " + bad + " refused");
                Check(bad > 1000, "most mutations are refused");
            });
        }
    }
}
