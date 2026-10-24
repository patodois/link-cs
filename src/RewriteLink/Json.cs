using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Patodois.RewriteLink
{
    /// <summary>
    /// A strict JSON reader for the control lines. It accepts exactly what the Rust side's serde_json accepts,
    /// because a line that one implementation takes and another refuses is a way to make them disagree: numbers
    /// follow the JSON grammar (no leading zeros, no "1.", no "+1"), "-0" is a float (so no integer), a number
    /// beyond the range of a double is an error, a unicode escape takes exactly four hex digits and surrogate
    /// escapes must come as a valid pair, and arrays and objects nest at most 127 deep.
    ///
    /// Values come back as: string, boxed ulong (a non-negative integer that fits), null, or <see cref="Other"/>
    /// for any other valid value. Only the top-level object's members are kept.
    /// </summary>
    internal static class Json
    {
        public const int MaxNesting = 127;

        public sealed class JsonException : Exception
        {
            public JsonException(string m) : base(m) { }
        }

        /// <summary>Any valid JSON value that is not a string, a non-negative integer or null.</summary>
        public static readonly object Other = new object();

        /// <summary>Parses a document. The result is a Dictionary of the top-level object, or <see cref="Other"/>
        /// or a string, ulong or null if the document is a valid value of another kind.</summary>
        public static object Parse(string s)
        {
            var r = new Reader(s);
            r.Ws();
            object v = r.Value(0, true);
            r.Ws();
            if (r.I != s.Length) throw new JsonException("trailing characters");
            return v;
        }

        public static string Quote(string s)
        {
            var b = new StringBuilder(s.Length + 2);
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        private sealed class Reader
        {
            private readonly string _s;
            public int I;

            public Reader(string s) { _s = s; }

            public void Ws()
            {
                while (I < _s.Length)
                {
                    char c = _s[I];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') I++; else break;
                }
            }

            public object Value(int depth, bool top)
            {
                if (I >= _s.Length) throw new JsonException("unexpected end");
                char c = _s[I];
                if ((c == '{' || c == '[') && depth >= MaxNesting) throw new JsonException("nested too deeply");
                switch (c)
                {
                    case '{': return Obj(depth, top);
                    case '[': Arr(depth); return Other;
                    case '"': return Str();
                    case 't': Literal("true"); return Other;
                    case 'f': Literal("false"); return Other;
                    case 'n': Literal("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw new JsonException("unexpected character '" + c + "'");
                }
            }

            private void Literal(string lit)
            {
                if (string.CompareOrdinal(_s, I, lit, 0, lit.Length) != 0 || I + lit.Length > _s.Length) throw new JsonException("bad literal");
                I += lit.Length;
            }

            private bool Digit() { return I < _s.Length && _s[I] >= '0' && _s[I] <= '9'; }

            /// <summary>number = [-] (0 | 1-9 digits) [. digits] [(e|E) [+|-] digits]  (RFC 8259)</summary>
            private object Number()
            {
                bool neg = false, integer = true;
                long exp10 = 0;
                if (_s[I] == '-') { neg = true; I++; }
                if (!Digit()) throw new JsonException("bad number");
                int intStart = I;
                if (_s[I] == '0') I++; // a leading zero ends the integer part: "01" leaves the 1 behind, which no caller accepts
                else while (Digit()) I++;
                int intEnd = I, fracStart = 0, fracEnd = 0;
                if (I < _s.Length && _s[I] == '.')
                {
                    integer = false;
                    I++;
                    if (!Digit()) throw new JsonException("bad number");
                    fracStart = I;
                    while (Digit()) I++;
                    fracEnd = I;
                }
                if (I < _s.Length && (_s[I] == 'e' || _s[I] == 'E'))
                {
                    integer = false;
                    I++;
                    bool expNeg = false;
                    if (I < _s.Length && (_s[I] == '+' || _s[I] == '-')) { expNeg = _s[I] == '-'; I++; }
                    if (!Digit()) throw new JsonException("bad number");
                    while (Digit())
                    {
                        if (exp10 < 100000) exp10 = exp10 * 10 + (_s[I] - '0');
                        I++;
                    }
                    if (expNeg) exp10 = -exp10;
                }
                CheckRange(intStart, intEnd, fracStart, fracEnd, exp10);
                if (integer && !neg && intEnd - intStart <= 20)
                {
                    ulong v = 0;
                    bool over = false;
                    for (int k = intStart; k < intEnd; k++)
                    {
                        ulong d = (ulong)(_s[k] - '0');
                        if (v > (ulong.MaxValue - d) / 10UL) { over = true; break; }
                        v = v * 10UL + d;
                    }
                    if (!over) return v;
                }
                return Other;
            }

            /// <summary>A number beyond the range of a double is an error (it would be infinity). Decided on the digits, so it
            /// is the same on every runtime (double.Parse overflows differently on .NET Framework and .NET Core).</summary>
            private void CheckRange(int intStart, int intEnd, int fracStart, int fracEnd, long exp10)
            {
                var first = new char[17];
                int nfirst = 0, sigInt = 0;
                bool have = false;
                long dexp = 0;
                for (int k = intStart; k < intEnd; k++)
                {
                    if (!have && _s[k] == '0') continue;
                    have = true;
                    if (nfirst < 17) first[nfirst++] = _s[k];
                    sigInt++;
                }
                if (have)
                {
                    for (int k = fracStart; k < fracEnd && nfirst < 17; k++) first[nfirst++] = _s[k];
                    dexp = sigInt - 1 + exp10;
                }
                else
                {
                    int lead = 0, k = fracStart;
                    for (; k < fracEnd; k++)
                    {
                        if (_s[k] == '0') { lead++; continue; }
                        have = true;
                        break;
                    }
                    if (have)
                    {
                        for (; k < fracEnd && nfirst < 17; k++) first[nfirst++] = _s[k];
                        dexp = -(lead + 1) + exp10;
                    }
                }
                if (!have) return;
                if (dexp > 308) throw new JsonException("number out of range");
                if (dexp == 308)
                {
                    const string max17 = "17976931348623158";
                    for (int m = 0; m < 17; m++)
                    {
                        char c = m < nfirst ? first[m] : '0';
                        if (c > max17[m]) throw new JsonException("number out of range");
                        if (c < max17[m]) return;
                    }
                    throw new JsonException("number out of range");
                }
            }

            private string Str()
            {
                I++; // opening quote
                var b = new StringBuilder();
                while (true)
                {
                    if (I >= _s.Length) throw new JsonException("unterminated string");
                    char c = _s[I++];
                    if (c == '"') return b.ToString();
                    if (c < 0x20) throw new JsonException("control character in string");
                    if (c != '\\') { b.Append(c); continue; }
                    if (I >= _s.Length) throw new JsonException("unterminated escape");
                    char e = _s[I++];
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
                        {
                            char u = Hex4();
                            if (char.IsHighSurrogate(u))
                            {
                                if (I + 1 >= _s.Length || _s[I] != '\\' || _s[I + 1] != 'u') throw new JsonException("lone leading surrogate in hex escape");
                                I += 2;
                                char low = Hex4();
                                if (!char.IsLowSurrogate(low)) throw new JsonException("lone leading surrogate in hex escape");
                                b.Append(u).Append(low);
                            }
                            else if (char.IsLowSurrogate(u)) throw new JsonException("lone trailing surrogate in hex escape");
                            else b.Append(u);
                            break;
                        }
                        default: throw new JsonException("bad escape");
                    }
                }
            }

            /// <summary>Exactly four hex digits, ASCII only (char.IsDigit and int.Parse would take other scripts' digits).</summary>
            private char Hex4()
            {
                if (I + 4 > _s.Length) throw new JsonException("bad \\u escape");
                int v = 0;
                for (int k = 0; k < 4; k++)
                {
                    char c = _s[I + k];
                    int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                    if (d < 0) throw new JsonException("bad \\u escape");
                    v = v * 16 + d;
                }
                I += 4;
                return (char)v;
            }

            private void Arr(int depth)
            {
                I++;
                Ws();
                if (I < _s.Length && _s[I] == ']') { I++; return; }
                while (true)
                {
                    Ws();
                    Value(depth + 1, false);
                    Ws();
                    if (I >= _s.Length) throw new JsonException("unterminated array");
                    char c = _s[I++];
                    if (c == ']') return;
                    if (c != ',') throw new JsonException("expected , or ]");
                }
            }

            private object Obj(int depth, bool top)
            {
                I++;
                Dictionary<string, object> map = top ? new Dictionary<string, object>() : null;
                Ws();
                if (I < _s.Length && _s[I] == '}') { I++; return map ?? Other; }
                while (true)
                {
                    Ws();
                    if (I >= _s.Length || _s[I] != '"') throw new JsonException("expected key");
                    string k = Str();
                    Ws();
                    if (I >= _s.Length || _s[I++] != ':') throw new JsonException("expected :");
                    Ws();
                    object v = Value(depth + 1, false);
                    if (map != null) map[k] = v; // the last of a repeated key wins, as in serde_json
                    Ws();
                    if (I >= _s.Length) throw new JsonException("unterminated object");
                    char c = _s[I++];
                    if (c == '}') return map ?? Other;
                    if (c != ',') throw new JsonException("expected , or }");
                }
            }
        }
    }
}
