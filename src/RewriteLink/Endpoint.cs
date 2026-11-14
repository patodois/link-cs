using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Patodois.RewriteLink
{
    /// <summary>
    /// The endpoint file could not be written, or read (missing, wrong kind of file, wrong permissions, wrong contents). The message says what
    /// was wrong; it never contains the file's contents or a token.
    /// </summary>
    public sealed class EndpointException : Exception
    {
        public EndpointException(string message) : base(message) { }
    }

    /// <summary>What <see cref="Endpoint.Read"/> found: the host's control port and token. The token is a secret, so <see cref="ToString"/> hides it.</summary>
    public sealed class EndpointInfo
    {
        public readonly int ControlPort;
        public readonly string Token;

        public EndpointInfo(int controlPort, string token) { ControlPort = controlPort; Token = token; }

        public override string ToString() { return "EndpointInfo { ControlPort = " + ControlPort + ", Token = <redacted> }"; }
    }

    /// <summary>
    /// The endpoint file of CONTRACT.md section 6.2 (revision 6): <c>{"control_port":N,"token":"..."}</c>, which a host writes after it has bound its
    /// control port and a guest reads before it connects. <see cref="LinkConfig.EndpointFile"/> makes <see cref="Link"/> do both; these are the
    /// pieces, for a launcher or a test.
    ///
    /// Writing is atomic and private from the moment the data exists. On Unix the library cannot ask <c>netstandard2.0</c> for a file created with
    /// mode 0600 (and P/Invoke of the variadic <c>open</c> is wrong on Apple arm64), so it makes a directory next to the target with mode 0700,
    /// creates the temporary file in it, sets it to 0600 before the first byte is written, renames it over the target and removes the directory:
    /// nobody but the owner can reach the file in between, whatever the umask. The .NET 7+ calls (<c>Directory.CreateDirectory(path, UnixFileMode)</c>,
    /// <c>File.SetUnixFileMode</c>, <c>File.Move(from, to, true)</c>) are found by reflection and used when they exist; otherwise (Mono, Unity,
    /// .NET Framework on Unix, or when a test forces it) the same steps use <c>mkdir</c>, <c>chmod</c> and <c>rename</c> from libc. On Windows
    /// there are no mode bits: the file is created with <see cref="FileMode.CreateNew"/> beside the target and moved over it (the per-user ACL of
    /// the directory, which the contract puts under the local application data, keeps others out). Replacing an existing file there is
    /// <see cref="File.Replace(string, string, string)"/>; when the target does not exist yet, <see cref="File.Move"/>, with the tiny window in
    /// which another writer could create it in between (then the Replace is tried once).
    /// </summary>
    public static class Endpoint
    {
        /// <summary>The longest file a reader accepts, in bytes (the newline included).</summary>
        public const int MaxFileBytes = 4096;

        // ------------------------------------------------------------------------------------------ writing

        /// <summary>
        /// Writes the endpoint file at <paramref name="path"/>, replacing an existing file atomically; the directory must exist. The temporary file is
        /// removed if anything fails. Throws <see cref="ArgumentException"/> for a bad port or token, <see cref="EndpointException"/> if the file cannot be
        /// written; no message contains the token.
        /// </summary>
        public static void Write(string path, int controlPort, string token)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("the path is empty", "path");
            if (controlPort < 1 || controlPort > 65535) throw new ArgumentOutOfRangeException("controlPort", "the control port must be 1 to 65535");
            if (token == null) throw new ArgumentNullException("token");
            Tokens.CheckConfigured(token, "token");
            byte[] data = Control.Utf8Strict.GetBytes("{\"control_port\":" + controlPort.ToString(CultureInfo.InvariantCulture) + ",\"token\":" + Json.Quote(token) + "}\n");
            try { WriteAtomic(Path.GetFullPath(path), data); }
            catch (EndpointException) { throw; }
            catch (Exception e)
            {
                if (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is System.Security.SecurityException)
                    throw new EndpointException("cannot write the endpoint file: " + e.Message);
                throw new EndpointException("cannot write the endpoint file (" + e.GetType().Name + ")");
            }
        }

        private static void WriteAtomic(string full, byte[] data)
        {
            string dir = Path.GetDirectoryName(full), name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) throw new EndpointException("cannot write the endpoint file: not a file path");
            if (!Directory.Exists(dir)) throw new EndpointException("cannot write the endpoint file: the directory does not exist"); // none is created
            string stem = "." + name + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "." + Tokens.RandomHex(8);
            string tmpName = stem + ".tmp";
            if (!Posix.IsUnix)
            {
                string tmp = Path.Combine(dir, tmpName);
                try
                {
                    WriteNew(tmp, data, false);
                    if (File.Exists(full)) File.Replace(tmp, full, null);
                    else
                    {
                        try { File.Move(tmp, full); }
                        catch (IOException) { if (!File.Exists(full)) throw; File.Replace(tmp, full, null); }
                    }
                }
                catch { TryDelete(tmp); throw; }
                return;
            }
            string priv = Path.Combine(dir, stem + ".d"), inner = Path.Combine(priv, tmpName);
            try
            {
                Posix.MakePrivateDirectory(priv);
                WriteNew(inner, data, true);
                Posix.Rename(inner, full);
            }
            catch { TryDelete(inner); TryDeleteDir(priv); throw; }
            TryDeleteDir(priv);
        }

        /// <summary>Creates <paramref name="file"/> (it must not exist), makes it owner-only before writing if <paramref name="unix"/>, writes and flushes.</summary>
        private static void WriteNew(string file, byte[] data, bool unix)
        {
            using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (unix) Posix.Chmod(file, 384); // 0600, before anything is written
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
        }

        private static void TryDelete(string file) { try { File.Delete(file); } catch (Exception) { } }
        private static void TryDeleteDir(string dir) { try { Directory.Delete(dir); } catch (Exception) { } }

        // ------------------------------------------------------------------------------------------ reading

        /// <summary>
        /// Reads and checks the endpoint file: the path names a regular file (symbolic links are followed; a directory, or a FIFO or device, which
        /// <c>netstandard2.0</c> can only tell by its size of 0, is refused before it is opened); on Unix its permissions grant nothing to the group or
        /// to others (using <c>File.GetUnixFileMode</c>, .NET 7+; runtimes without a permission API refuse endpoint files); it is at most 4096 bytes;
        /// its text is one control line (strict UTF-8, one trailing newline allowed) holding an object with a <c>control_port</c> integer 1 to 65535 and
        /// a <c>token</c> string of 1 to 256 bytes; other fields are ignored. Throws <see cref="EndpointException"/> otherwise, with a message that
        /// never contains the file's contents.
        /// </summary>
        public static EndpointInfo Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("the path is empty", "path");
            try { return ReadChecked(path); }
            catch (EndpointException) { throw; }
            catch (Exception e)
            {
                if (e is FileNotFoundException || e is DirectoryNotFoundException) throw new EndpointException("the endpoint file does not exist");
                throw new EndpointException("cannot read the endpoint file: " + e.GetType().Name); // the type only: never the contents
            }
        }

        /// <summary>Tests only: runs between the checks on the path and the open.</summary>
        internal static Action<string> BeforeOpen;

        private static EndpointInfo ReadChecked(string path)
        {
            if (Directory.Exists(path)) throw new EndpointException("the endpoint file is not a regular file");
            if (!File.Exists(path)) throw new EndpointException("the endpoint file does not exist");
            // Before opening: opening a FIFO for reading blocks until a writer comes. A regular endpoint file is never empty.
            long size = new FileInfo(path).Length;
            if (size == 0) throw new EndpointException("the endpoint file is empty or not a regular file");
            var buf = new byte[MaxFileBytes + 1];
            int n = 0;
            BeforeOpen?.Invoke(path);
            // Non-blocking where libc's open can be called: a FIFO put there after the checks above is not waited on.
            using (var fs = Posix.OpenReadNonBlocking(path, 4096) ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!fs.CanSeek) throw new EndpointException("the endpoint file is empty or not a regular file");
                if (Posix.IsUnix)
                {
                    int mode = Posix.Permissions(fs.SafeFileHandle);
                    if (mode < 0) throw new EndpointException("cannot verify the endpoint file permissions on this runtime");
                    if ((mode & 63) != 0) throw new EndpointException("the endpoint file's permissions allow the group or others to access it");
                }
                while (n < buf.Length)
                {
                    int r = fs.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
            }
            if (n == 0) throw new EndpointException("the endpoint file is empty");
            if (n > MaxFileBytes) throw new EndpointException("the endpoint file is longer than " + MaxFileBytes + " bytes");
            // one control line: the one newline, with its \r, may only be the last thing
            int end = n, nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nl >= 0)
            {
                if (nl != n - 1) throw new EndpointException("the endpoint file is more than one line");
                end = nl;
                if (end > 0 && buf[end - 1] == (byte)'\r') end--;
            }
            string text;
            try { text = Control.Utf8Strict.GetString(buf, 0, end); }
            catch (DecoderFallbackException) { throw new EndpointException("the endpoint file is not UTF-8"); }
            object v;
            try { v = Json.Parse(text); }
            catch (Json.JsonException) { throw new EndpointException("the endpoint file is not a JSON object"); }
            var o = v as Dictionary<string, object>;
            if (o == null) throw new EndpointException("the endpoint file is not a JSON object");
            object port, token;
            if (!o.TryGetValue("control_port", out port) || !(port is ulong) || (ulong)port < 1 || (ulong)port > 65535)
                throw new EndpointException("the endpoint file has no valid \"control_port\" (an integer, 1 to 65535)");
            string t = o.TryGetValue("token", out token) ? token as string : null;
            if (t == null) throw new EndpointException("the endpoint file has no \"token\" string");
            int tb = Control.Utf8Strict.GetByteCount(t); // the parser has refused lone surrogates
            if (tb < 1 || tb > Tokens.MaxBytes) throw new EndpointException("the endpoint file's \"token\" is not 1 to " + Tokens.MaxBytes + " bytes");
            return new EndpointInfo((int)(ulong)port, t);
        }

        // ------------------------------------------------------------------------------------------ deleting

        /// <summary>Removes the endpoint file, best effort: true if a file was removed, false if there was none or it could not be removed (no exception).</summary>
        public static bool Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// A closing host's removal: deletes the file only while it is still this host's, that is, while a guest would accept it (<see cref="Read"/>)
        /// and it holds <paramref name="controlPort"/> and the token <paramref name="token"/> (compared in constant time). A file that another host has
        /// written since, one that is not an endpoint file and one that cannot be read stay. Best effort, never throws.
        /// </summary>
        internal static bool DeleteIfOwn(string path, int controlPort, byte[] token)
        {
            if (string.IsNullOrEmpty(path) || token == null) return false;
            try
            {
                EndpointInfo info = Read(path);
                bool samePort = info.ControlPort == controlPort;
                bool sameToken = Tokens.Equal(Tokens.Bytes(info.Token), token);
                if (!(samePort & sameToken)) return false;
            }
            catch (Exception) { return false; }
            return Delete(path);
        }
    }

    /// <summary>
    /// The Unix calls of <see cref="Endpoint"/>. On .NET 7+ they are the runtime's own, reached by reflection because the library targets
    /// <c>netstandard2.0</c>; everywhere else (and when a test sets <see cref="ForcePInvoke"/>) they are libc's <c>mkdir</c>, <c>chmod</c> and
    /// <c>rename</c>. Files are never created through libc's <c>open</c>: its mode is a variadic argument, which P/Invoke cannot pass on Apple
    /// arm64. Reading uses <c>open(path, flags)</c> (<see cref="OpenReadNonBlocking"/>), which has two named arguments and no variadic one.
    /// </summary>
    internal static class Posix
    {
        /// <summary>Tests only: use the libc calls even where the runtime has its own.</summary>
        public static volatile bool ForcePInvoke;

        // Tests only: emulate a runtime without either Unix permission API.
        internal static bool ForceNoPermissionApi;

        /// <summary>Tests only: which path the last call took, "runtime" or "libc".</summary>
        public static volatile string LastPath = "";

        /// <summary>Not Windows: the one platform test of the library (the link, the endpoint file and the pixel file use it).</summary>
        public static readonly bool IsUnix = Environment.OSVersion.Platform != PlatformID.Win32NT && Environment.OSVersion.Platform != PlatformID.Win32Windows
            && Environment.OSVersion.Platform != PlatformID.Win32S && Environment.OSVersion.Platform != PlatformID.WinCE;

        private static readonly Type ModeType = typeof(File).Assembly.GetType("System.IO.UnixFileMode");
        private static readonly MethodInfo CreateDirectoryMode = ModeType == null ? null : typeof(Directory).GetMethod("CreateDirectory", new[] { typeof(string), ModeType });
        private static readonly MethodInfo SetMode = ModeType == null ? null : typeof(File).GetMethod("SetUnixFileMode", new[] { typeof(string), ModeType });
        private static readonly MethodInfo GetModeHandle = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(SafeFileHandle) });
        private static readonly MethodInfo MoveOverwrite = typeof(File).GetMethod("Move", new[] { typeof(string), typeof(string), typeof(bool) });

        [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
        private static extern int c_mkdir(string path, uint mode);

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int c_chmod(string path, uint mode);

        [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
        private static extern int c_rename(string from, string to);

        // open(2) with its two named arguments only: the mode, a variadic argument, is read only with O_CREAT, which is never passed here.
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int c_open(string path, int flags);

        /// <summary>O_RDONLY | O_NONBLOCK | O_CLOEXEC where the values are known (Linux and macOS on x86 and ARM), else 0: no native open.</summary>
        private static readonly int ReadNonBlockingFlags = ReadFlags();
        private static volatile bool _openUnavailable;

        private static int ReadFlags()
        {
            if (!IsUnix) return 0;
            Architecture a = RuntimeInformation.ProcessArchitecture;
            if (a != Architecture.X64 && a != Architecture.Arm64 && a != Architecture.X86 && a != Architecture.Arm) return 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return 0x800 | 0x80000;  // O_NONBLOCK | O_CLOEXEC; O_RDONLY is 0
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 0x4 | 0x1000000;    // the same on macOS
            return 0;
        }

        /// <summary>
        /// Opens <paramref name="path"/> for reading with <c>O_NONBLOCK</c>, so a FIFO opens at once instead of waiting for a writer (CONTRACT.md
        /// 6.2 and 9.6); the caller checks what it got. Null where libc's <c>open</c> cannot be called (Windows, another Unix or architecture, no
        /// binding): the caller then opens the path as before. Throws <see cref="IOException"/> if the open fails.
        /// </summary>
        internal static FileStream OpenReadNonBlocking(string path, int bufferSize)
        {
            if (ReadNonBlockingFlags == 0 || _openUnavailable) return null;
            int fd;
            try { fd = c_open(path, ReadNonBlockingFlags); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException || e is PlatformNotSupportedException || e is MarshalDirectiveException || e is TypeInitializationException)
            {
                _openUnavailable = true;
                return null;
            }
            if (fd < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno == 2) throw new FileNotFoundException("open failed (errno 2)"); // ENOENT, 2 on Linux and macOS
                throw new IOException("open failed (errno " + errno.ToString(CultureInfo.InvariantCulture) + ")");
            }
            var handle = new SafeFileHandle(new IntPtr(fd), true);
            try { return new FileStream(handle, FileAccess.Read, bufferSize); }
            catch { handle.Dispose(); throw; }
        }

        private static bool Runtime(MethodInfo m) { return !ForcePInvoke && m != null; }

        private static object Invoke(MethodInfo m, params object[] args)
        {
            try { return m.Invoke(null, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }

        private static void Fail(string what)
        {
            throw new IOException(what + " failed (errno " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>Creates <paramref name="dir"/> with mode 0700; it must not exist.</summary>
        public static void MakePrivateDirectory(string dir)
        {
            if (Runtime(CreateDirectoryMode) && SetMode != null)
            {
                LastPath = "runtime";
                if (Directory.Exists(dir) || File.Exists(dir)) throw new IOException("the private directory exists already");
                object mode = Enum.ToObject(ModeType, 448); // UserRead | UserWrite | UserExecute
                Invoke(CreateDirectoryMode, dir, mode);
                Invoke(SetMode, dir, mode); // the umask may only have removed bits, but say it
                return;
            }
            LastPath = "libc";
            if (c_mkdir(dir, 448) != 0) Fail("mkdir");
            if (c_chmod(dir, 448) != 0) Fail("chmod");
        }

        public static void Chmod(string path, int mode)
        {
            if (Runtime(SetMode))
            {
                LastPath = "runtime";
                Invoke(SetMode, path, Enum.ToObject(ModeType, mode));
                return;
            }
            LastPath = "libc";
            if (c_chmod(path, (uint)mode) != 0) Fail("chmod");
        }

        /// <summary>Renames <paramref name="from"/> over <paramref name="to"/>, replacing it (atomic on one file system).</summary>
        public static void Rename(string from, string to)
        {
            if (Runtime(MoveOverwrite))
            {
                LastPath = "runtime";
                Invoke(MoveOverwrite, from, to, true);
                return;
            }
            LastPath = "libc";
            if (c_rename(from, to) != 0) Fail("rename");
        }

        /// <summary>The permission bits (0 to 0777) of the open file, or -1 where the runtime cannot tell (before .NET 7).</summary>
        public static int Permissions(SafeFileHandle h)
        {
            if (ForceNoPermissionApi) return -1;
            if (GetModeHandle != null) return Convert.ToInt32(Invoke(GetModeHandle, h), CultureInfo.InvariantCulture) & 511;
            return -1;
        }
    }
}
