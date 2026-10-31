using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Patodois.RewriteLink
{
    /// <summary>
    /// The authentication token of CONTRACT.md section 6.2 (revision 6): a string of 1 to 256 UTF-8 bytes that a host may require in
    /// the guest's <c>hello</c>. A token is a secret: nothing in this library prints it, puts it in an exception message, an event
    /// reason or a <c>bye</c>.
    /// </summary>
    public static class Tokens
    {
        /// <summary>The longest token, in UTF-8 bytes.</summary>
        public const int MaxBytes = 256;

        /// <summary>
        /// A new token: 32 bytes from the operating system's cryptographic random number generator (<see cref="RandomNumberGenerator"/>),
        /// as 64 lowercase hex digits. A failure of the generator throws; there is no fallback to a weaker source.
        /// </summary>
        public static string Generate()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var s = new StringBuilder(64);
            foreach (byte x in b) s.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return s.ToString();
        }

        /// <summary>
        /// Compares two tokens (their UTF-8 bytes) in time that does not depend on their contents or on where they first differ: the
        /// reference method of the contract. Each is read as if copied into 256 zero bytes, the XOR of all 256 pairs and of the two lengths
        /// is OR-ed together, and the tokens are equal only if that is 0 and neither is longer than 256 bytes. There is no early exit; a
        /// length mismatch does the same work as a match. (The length itself is not secret.) Two empty arrays are equal here, but a
        /// handshake never gets that far: an empty token is refused first.
        /// </summary>
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            int la = a.Length, lb = b.Length;
            int diff = la ^ lb;
            for (int i = 0; i < MaxBytes; i++)
            {
                int x = i < la ? a[i] : 0;
                int y = i < lb ? b[i] : 0;
                diff |= x ^ y;
            }
            int tooLong = (la > MaxBytes ? 1 : 0) | (lb > MaxBytes ? 1 : 0);
            return (diff | tooLong) == 0;
        }

        /// <summary>
        /// Checks a token that is handed to a host, a guest or the endpoint file writer: null passes (it means none); anything else must be 1 to 256
        /// bytes of UTF-8 (bytes, not characters), without U+0000 and without a lone surrogate (which has no UTF-8 form). The exception names
        /// the parameter and the rule, never the token.
        /// </summary>
        internal static void CheckConfigured(string token, string param)
        {
            if (token == null) return;
            if (token.Length == 0) throw new ArgumentException("the token is empty", param);
            if (token.IndexOf('\0') >= 0) throw new ArgumentException("the token contains U+0000", param);
            int n;
            try { n = Control.Utf8Strict.GetByteCount(token); }
            catch (EncoderFallbackException) { throw new ArgumentException("the token is not valid Unicode (a lone surrogate)", param); }
            if (n > MaxBytes) throw new ArgumentException("the token is longer than " + MaxBytes + " bytes of UTF-8", param);
        }

        /// <summary>The UTF-8 bytes of a token that <see cref="CheckConfigured"/> accepted.</summary>
        internal static byte[] Bytes(string token) { return Control.Utf8Strict.GetBytes(token); }
    }
}
