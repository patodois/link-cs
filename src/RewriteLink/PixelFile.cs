using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Patodois.RewriteLink
{
    /// <summary>
    /// The shared-memory pixel file of CONTRACT.md section 9.6 (revision 7): a <see cref="Reader"/> for the receiving side (bounded Unix I/O
    /// or a Windows read-only map, checking the slot before presentation) and a <see cref="Writer"/> for the sending side.
    ///
    /// Unix positional observations are not atomic acquire loads from the writer. See PIXEL-IO.md for the revision 7 limits.
    /// THE SEQLOCK AND THE .NET MEMORY MODEL (writers and Windows mapped readers). The writer makes a slot's 64-bit <c>lock</c> odd while it writes and even afterwards; the
    /// reader copies and checks that the lock did not change. The contract asks for these orderings:
    ///
    ///   writer: lock+1 (relaxed); release fence; slot header, pixels, depth; lock+2 (release store)
    ///   reader: l1 = lock (acquire), odd -> skip; copy; acquire fence; l2 = lock; l2 != l1 or another frame -> discard
    ///
    /// In .NET: <c>Volatile.Read</c> is an acquire load and <c>Volatile.Write</c> a release store, and both are also barriers for the JIT, so
    /// it does not move the surrounding memory accesses across them. <c>Thread.MemoryBarrier()</c> is a full fence, stronger than the
    /// acquire and release fences of the contract, for the CPU and for the JIT alike; it is used where the contract has a fence. So:
    ///   - the odd store is a plain 64-bit store, then MemoryBarrier: no data store can become visible before it (the release fence);
    ///   - the even store is Volatile.Write (release): every data store is visible before it; then a MemoryBarrier so it is out before the
    ///     caller sends the Frame datagram;
    ///   - the reader's first load is Volatile.Read (acquire): the copy cannot start before it; after the copy, MemoryBarrier (the acquire
    ///     fence) and then the second load of the lock.
    /// The lock sits at a multiple of 64 bytes from the start of the page-aligned view, so a 64-bit access to it is aligned and atomic in a
    /// 64-bit process (32-bit processes are not a target: a 64-bit load there is not atomic). The reader's mapping is read-only, so no
    /// locked read-modify-write (Interlocked) is used on it; those would fault on a read-only page.
    /// </summary>
    public static unsafe class PixelFile
    {
        private static bool IsWindows { get { return Path.DirectorySeparatorChar == '\\'; } }
        public const int HeaderLen = 64;
        public const int SlotHeaderLen = 64;
        public const uint FormatRgba8 = 1;
        public const uint DepthNone = 0;
        public const uint DepthLinearF32 = 1;

        /// <summary>One frame copied out of a slot. <see cref="Depth"/> is null when it was not asked for or the file has no depth plane.</summary>
        public sealed class Frame
        {
            public ulong Number;
            public ulong TimeUs;
            public uint CameraSeq;
            public int Width, Height;
            public byte[] Rgba;
            public float[] Depth;
        }

        /// <summary>A file that is not a valid pixel file (wrong magic, impossible sizes, not a regular file ...).</summary>
        public sealed class FormatException : IOException
        {
            public FormatException(string m) : base(m) { }
        }

        /// <summary>The numbers of a validated file header.</summary>
        internal struct Layout
        {
            public int Slots, Width, Height, Stride;
            public long SlotSize;
            public uint DepthFormat;
            public long DepthStride, DepthOffset;
        }

        internal static int ProcessPointerSize = IntPtr.Size;

        private static void RequireLittleEndian()
        {
            if (ProcessPointerSize != 8) throw new PlatformNotSupportedException("pixel files require a 64-bit process");
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("the pixel file code needs a little-endian machine");
        }

        /// <summary>Whether <paramref name="path"/> is absolute under the Unix rules or the Windows rules (a drive letter and a separator, or two separators).</summary>
        internal static bool IsAbsolute(string path, bool windowsRules)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!windowsRules) return path[0] == '/';
            if (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                return true;
            return path.Length >= 2 && (path[0] == '\\' || path[0] == '/') && (path[1] == '\\' || path[1] == '/');
        }

        private static bool IsAbsoluteHere(string path) { return IsAbsolute(path, System.IO.Path.DirectorySeparatorChar == '\\'); }

        /// <summary>
        /// Checks a pixel file header against the file's length, as revision 4 and 5 say: the limits come first (width and height 1 to 16384,
        /// at most 2^26 pixels, 1 to 16 slots, stride exactly width * 4), then every size is computed with overflow checks. An unknown
        /// <c>depth_format</c> is not an error: the file reads as colour only. Pure: it reads only the 64 header bytes.
        /// </summary>
        internal static Layout Validate(byte[] h, long size)
        {
            if (size < HeaderLen) throw new FormatException("pixel file shorter than its header");
            if (h[0] != 'R' || h[1] != 'B' || h[2] != 'F' || h[3] != 'R' || h[4] != 'A' || h[5] != 'M' || h[6] != 'E' || h[7] != 0)
                throw new FormatException("bad pixel file magic");
            uint version = Wire.Get32(h, 8), slots = Wire.Get32(h, 12), w = Wire.Get32(h, 16), ht = Wire.Get32(h, 20), stride = Wire.Get32(h, 24), format = Wire.Get32(h, 28);
            ulong slotSize = Wire.Get64(h, 32);
            uint depthFormat = Wire.Get32(h, 40), depthStride = Wire.Get32(h, 44);
            ulong depthOffset = Wire.Get64(h, 48);
            if (version != 1) throw new FormatException("pixel file version " + version);
            if (slots < 1 || slots > 16) throw new FormatException("slots " + slots);
            if (w < 1 || ht < 1 || w > Wire.MaxFrameDim || ht > Wire.MaxFrameDim) throw new FormatException("size " + w + "x" + ht);
            if ((ulong)w * ht > (ulong)Wire.MaxFramePixels) throw new FormatException("size " + w + "x" + ht + " is over " + Wire.MaxFramePixels + " pixels");
            if (format != FormatRgba8) throw new FormatException("pixel format " + format);
            if (stride != w * 4u) throw new FormatException("stride " + stride + " is not width * 4");
            try
            {
                checked
                {
                    // The sizes come from the file, so none of them is trusted: a slot_size picked so that slots * slot_size wraps would
                    // pass an unchecked length test.
                    ulong colourEnd = SlotHeaderLen + (ulong)stride * ht;
                    if (slotSize % 64 != 0 || slotSize < colourEnd) throw new FormatException("slot_size " + slotSize);
                    ulong need = HeaderLen + (ulong)slots * slotSize;
                    if (need > (ulong)size) throw new FormatException("file is shorter than its slots");
                    if (depthFormat == DepthLinearF32)
                    {
                        if (depthStride != w * 4u) throw new FormatException("depth_stride " + depthStride);
                        if (depthOffset % 64 != 0 || depthOffset < colourEnd) throw new FormatException("depth_offset " + depthOffset);
                        ulong planeEnd = depthOffset + (ulong)depthStride * ht;
                        if (planeEnd > slotSize) throw new FormatException("depth plane outside its slot");
                    }
                    else
                    {
                        depthFormat = DepthNone; // unknown format: colour only
                        depthStride = 0;
                        depthOffset = 0;
                    }
                    if (slotSize > long.MaxValue || depthOffset > long.MaxValue) throw new FormatException("the sizes in the pixel file header overflow");
                }
            }
            catch (OverflowException) { throw new FormatException("the sizes in the pixel file header overflow"); }
            return new Layout
            {
                Slots = (int)slots, Width = (int)w, Height = (int)ht, Stride = (int)stride, SlotSize = (long)slotSize,
                DepthFormat = depthFormat, DepthStride = depthStride, DepthOffset = (long)depthOffset,
            };
        }

        // ------------------------------------------------------------------ reader

        /// <summary>Copies a pixel file using pread on Unix and a read-only map on Windows. Not thread-safe: use one reader per thread.</summary>
        public sealed class Reader : IDisposable
        {
            private readonly FileStream _file;
            private SafeFileHandle _readHandle;
            private bool _nativeUnavailable;
            private bool _gone;
            private byte[] _fallbackScratch = new byte[0];
            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _view;
            private readonly byte* _base;
            private readonly long _mapped;
            private readonly Layout _l;
            private bool _disposed;
            private ulong _newest;

            public int Slots { get { return _l.Slots; } }
            public int Width { get { return _l.Width; } }
            public int Height { get { return _l.Height; } }
            public int Stride { get { return _l.Stride; } }
            public long SlotSize { get { return _l.SlotSize; } }
            public bool HasDepth { get { return _l.DepthFormat == DepthLinearF32; } }

            // Diagnostics: why Read returned null.
            public long SkippedBusy, SkippedOtherFrame, SkippedOverwritten, SkippedGone, SkippedWrongSize, SkippedStale;

            private Reader(FileStream file, MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* b, long mapped, Layout l)
            {
                _file = file; _mmf = mmf; _view = view; _base = b; _mapped = mapped; _l = l;
                if (!IsWindows)
                {
                    try { _readHandle = file.SafeFileHandle; }
                    catch (Exception) { _nativeUnavailable = true; }
                }
            }

            /// <summary>
            /// True when the file shrank or cannot be examined. Unix copies use bounded positional
            /// I/O as well, so truncation after this check is a miss. Windows mappings prevent ordinary
            /// truncation. A failed read never advances presentation order.
            /// </summary>
            public bool IsGone
            {
                get
                {
                    if (_disposed || _gone) return true;
                    try { return _file.Length < _mapped; }
                    catch (IOException) { return true; }
                    catch (ObjectDisposedException) { return true; }
                }
            }

            /// <summary>
            /// Opens and checks the file as section 9.6 says: an absolute path to a regular file (a symlink, a directory, a FIFO, a device
            /// or a socket is refused), the header limits and checked arithmetic of <see cref="Validate"/>. The type is checked before the
            /// file is opened: a symlink or directory by its attributes, and a FIFO, device or socket by its size, which is 0 where a pixel
            /// file needs at least 64 bytes, so opening one (which waits for a writer for ever) is never tried. .NET has no non-blocking open
            /// and no fstat to compare with, so a peer that swaps the file after the check can still get through; the contract asks for
            /// O_NONBLOCK and an inode comparison where the language has them.
            /// </summary>
            /// <exception cref="PlatformNotSupportedException">The process is not 64-bit or the machine is not little-endian.
            /// No file is opened and no native pixel I/O is attempted in that case.</exception>
            public static Reader Open(string path)
            {
                RequireLittleEndian();
                if (!IsAbsoluteHere(path)) throw new FormatException("the pixel file path is not absolute: " + path);
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new FormatException("not a regular file: " + path);
                if (new FileInfo(path).Length < HeaderLen) throw new FormatException("not a pixel file (or not a regular file): " + path);
                FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                MemoryMappedFile mmf = null;
                MemoryMappedViewAccessor view = null;
                bool acquired = false;
                try
                {
                    long size = fs.Length;
                    if (size < HeaderLen) throw new FormatException("pixel file shorter than its header");
                    var head = new byte[HeaderLen];
                    int got = 0;
                    while (got < head.Length)
                    {
                        int n = fs.Read(head, got, head.Length - got);
                        if (n == 0) throw new FormatException("pixel header truncated while opening");
                        got += n;
                    }
                    Layout l = Validate(head, size);
                    byte* p = null;
                    if (IsWindows)
                    {
                        // An active Windows mapping prevents ordinary file truncation.
                        mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                        view = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                        acquired = true;
                        p += view.PointerOffset;
                        // The section prevents later truncation; reject an earlier shrink.
                        if (fs.Length < size) throw new FormatException("pixel file shrank before mapping");
                    }
                    return new Reader(fs, mmf, view, p, size, l);
                }
                catch
                {
                    if (acquired) view.SafeMemoryMappedViewHandle.ReleasePointer();
                    if (view != null) view.Dispose();
                    if (mmf != null) mmf.Dispose();
                    fs.Dispose();
                    throw;
                }
            }

            /// <summary>Presentation copy: only a valid slot advances order within this file.
            /// Open a new reader and invalidate the previous image on every channel replacement,
            /// including failed opens. Read remains an unordered low-level copy.</summary>
            public Frame ReadLatest(Wire.FrameReady ready, bool wantDepth)
            {
                if (ready == null) return null;
                if (ready.Frame != 0 && ready.Frame <= _newest) { SkippedStale++; SkippedOtherFrame++; return null; }
                Frame f = Read(ready, wantDepth);
                if (f != null) _newest = f.Number;
                return f;
            }

            /// <summary>
            /// Reads the frame a <c>Frame</c> datagram announces: <see cref="Read(ulong, int, uint, uint, bool)"/> with the datagram's values.
            /// A <see cref="Wire.FrameReady.Slot"/> that does not fit an int is a slot that does not exist.
            /// </summary>
            public Frame Read(Wire.FrameReady ready, bool wantDepth)
            {
                if (ready == null || ready.Slot > int.MaxValue) return null;
                return Read(ready.Frame, (int)ready.Slot, ready.Width, ready.Height, wantDepth);
            }

            /// <summary>
            /// Copies slot <paramref name="slot"/> out if it holds frame <paramref name="frame"/> and was not written while copying; null otherwise
            /// (skip the frame and wait for the next one). <paramref name="width"/> and <paramref name="height"/> are the ones the
            /// <c>Frame</c> datagram carries (CONTRACT.md 9.6, reader step 2, revision 5): the frame is skipped, before any pixel is copied, if
            /// either is 0 or above the file's, or if the slot header's width or height is 0, above the file's, or not equal to the datagram's.
            /// The slot does not have to be <c>frame % slots</c>: the slot is found by the number the datagram names.
            /// </summary>
            public Frame Read(ulong frame, int slot, uint width, uint height, bool wantDepth)
            {
                return ReadSlot(frame, slot, true, width, height, wantDepth);
            }

            /// <summary>
            /// As <see cref="Read(ulong, int, uint, uint, bool)"/> but without the datagram's size, so the slot header's size is checked
            /// against the file's only. Kept so that programs written before revision 5 still build; it is NOT conformant to CONTRACT.md 9.6
            /// (reader step 2). A receiver that has the <c>Frame</c> datagram passes its width and height or uses
            /// <see cref="Read(Wire.FrameReady, bool)"/>, which the contract requires.
            /// </summary>
            public Frame Read(ulong frame, int slot, bool wantDepth)
            {
                return ReadSlot(frame, slot, false, 0, 0, wantDepth);
            }

            // POSIX pread copies into owned buffers. It cannot fault a peer-owned mapping,
            // and SafeFileHandle keeps the descriptor alive for the duration of each call.
            [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
            private static extern IntPtr PRead(SafeFileHandle file, IntPtr buffer, UIntPtr count, long offset);

            internal Func<SafeFileHandle, IntPtr, UIntPtr, long, IntPtr> NativeReadOverride;
            internal Action<long> BeforeCopy;
            internal Action BeforeFrameAllocation;
            internal Action<string> BeforeScratchAllocation;
            public long SkippedAllocation;
            private byte[] _planeScratch = new byte[0];

            private bool ManagedCopyAt(long offset, byte* dst, int count)
            {
                if (_fallbackScratch.Length == 0) { BeforeScratchAllocation?.Invoke("managed"); _fallbackScratch = new byte[64 * 1024]; }
                _file.Position = offset;
                while (count > 0)
                {
                    int n = _file.Read(_fallbackScratch, 0, Math.Min(count, _fallbackScratch.Length));
                    if (n <= 0) return false;
                    fixed (byte* p = _fallbackScratch) Buffer.MemoryCopy(p, dst, count, n);
                    dst += n; count -= n;
                }
                return true;
            }

            private bool CopyAt(long offset, byte* dst, int count)
            {
                bool ok = TryCopyAt(offset, dst, count);
                if (!ok) _gone = true;
                return ok;
            }

            private bool TryCopyAt(long offset, byte* dst, int count)
            {
                try
                {
                    BeforeCopy?.Invoke(offset);
                    if (_nativeUnavailable) return ManagedCopyAt(offset, dst, count);
                    while (count > 0)
                    {
                        long n;
                        try
                        {
                            n = (NativeReadOverride == null ? PRead(_readHandle, (IntPtr)dst, (UIntPtr)(uint)count, offset) : NativeReadOverride(_readHandle, (IntPtr)dst, (UIntPtr)(uint)count, offset)).ToInt64();
                        }
                        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException || e is PlatformNotSupportedException || e is MarshalDirectiveException || e is TypeInitializationException)
                        {
                            _nativeUnavailable = true;
                            return ManagedCopyAt(offset, dst, count);
                        }
                        if (n < 0 && Marshal.GetLastWin32Error() == 4) continue;
                        if (n <= 0) return false;
                        dst += n; offset += n; count -= (int)n;
                    }
                    return true;
                }
                catch (OutOfMemoryException) { throw; } // ReadSlot counts allocation misses without marking the file gone.
                catch (Exception) { return false; } // I/O/provider/handle failures are misses.
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct IoVector { internal IntPtr Base; internal UIntPtr Length; }
            [DllImport("libc",EntryPoint="preadv",SetLastError=true)]
            private static extern IntPtr PReadV(SafeFileHandle file,IoVector* vectors,int count,long offset);
            private byte[] _paddingScratch=new byte[0];
            private bool _vectorsUnavailable;
            internal Func<SafeFileHandle,IntPtr,int,long,IntPtr> NativeVectorReadOverride;

            private bool? VectorRows(long offset,long stride,int row,int height,byte* dst)
            {
                try
                {
                    int gap=(int)stride-row;
                    if(_paddingScratch.Length<gap) { BeforeScratchAllocation?.Invoke("padding"); _paddingScratch=new byte[gap]; }
                    IoVector* vectors=stackalloc IoVector[16];
                    fixed(byte* padding=_paddingScratch)
                    for(int start=0;start<height;start+=8)
                    {
                        int rows=Math.Min(8,height-start),count=rows*2-1,first=0;
                        for(int y=0;y<rows;y++)
                        {
                            vectors[y*2].Base=(IntPtr)(dst+(start+y)*row);vectors[y*2].Length=(UIntPtr)(uint)row;
                            vectors[y*2+1].Base=(IntPtr)padding;vectors[y*2+1].Length=(UIntPtr)(uint)gap;
                        }
                        long at=offset+start*stride;
                        BeforeCopy?.Invoke(at);
                        while(first<count)
                        {
                            long n;
                            try {n=(NativeVectorReadOverride==null ? PReadV(_readHandle,vectors+first,count-first,at) : NativeVectorReadOverride(_readHandle,(IntPtr)(vectors+first),count-first,at)).ToInt64();}
                            catch(Exception e) when(e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException || e is PlatformNotSupportedException || e is MarshalDirectiveException || e is TypeInitializationException)
                            { _vectorsUnavailable=true;return null; }
                            if(n<0 && Marshal.GetLastWin32Error()==4) continue;
                            if(n<=0) return false;
                            at+=n;
                            while(first<count && (ulong)n>=vectors[first].Length.ToUInt64()) {n-=(long)vectors[first].Length.ToUInt64();first++;}
                            if(first<count && n>0) {vectors[first].Base=(IntPtr)((byte*)vectors[first].Base+n);vectors[first].Length=(UIntPtr)(vectors[first].Length.ToUInt64()-(ulong)n);}
                        }
                    }
                    return true;
                }
                catch(OutOfMemoryException) {throw;} // Preserve the recoverable allocation miss.
                catch(Exception) {return false;}
            }

            private bool CopyRows(long offset, long stride, int row, int height, byte* dst)
            {
                if (stride == row) return CopyAt(offset, dst, row * height);
                // Group only bounded strides: extra padding dominates wide-file benchmarks.
                if (stride <= 16 * 1024 && !_nativeUnavailable && !_vectorsUnavailable && height>1 && (height-1)*stride+row <= (long)row*height*4)
                {
                    bool? result=VectorRows(offset,stride,row,height,dst);
                    if(result.HasValue) {if(!result.Value) _gone=true;return result.Value;}
                }
                int span = checked((int)((height - 1) * stride + row));
                int blockBytes = Math.Max(32 << 10, row);
                int blockRows = Math.Min(height, (int)((blockBytes - row) / stride + 1));
                if (stride <= 16 * 1024 && span <= (long)row * height * 4 && blockRows > 1)
                {
                    int capacity = checked((int)((blockRows - 1) * stride + row));
                    if (_planeScratch.Length < capacity) { BeforeScratchAllocation?.Invoke("plane"); _planeScratch = new byte[capacity]; }
                    fixed (byte* p = _planeScratch)
                    {
                        for (int start = 0; start < height; start += blockRows)
                        {
                            int rows = Math.Min(blockRows, height - start);
                            int bytes = checked((int)((rows - 1) * stride + row));
                            if (!CopyAt(offset + start * stride, p, bytes)) return false;
                            for (int y = 0; y < rows; y++) Buffer.MemoryCopy(p + y * stride, dst + (start + y) * row, row, row);
                        }
                    }
                    return true;
                }
                for (int y = 0; y < height; y++) if (!CopyAt(offset + y * stride, dst + y * row, row)) return false;
                return true;
            }
            private Frame ReadFile(ulong frame, int slot, bool haveSize, uint dw, uint dh, bool wantDepth)
            {
                long at = HeaderLen + (long)slot * _l.SlotSize;
                byte[] head = new byte[SlotHeaderLen];
                fixed (byte* p = head) if (!CopyAt(at, p, head.Length)) { SkippedGone++; return null; }
                ulong l1 = Wire.Get64(head, 0); Thread.MemoryBarrier();
                if ((l1 & 1) != 0) { SkippedBusy++; return null; }
                ulong f = Wire.Get64(head, 8), time = Wire.Get64(head, 16);
                uint cam = Wire.Get32(head, 24), w = Wire.Get32(head, 28), h = Wire.Get32(head, 32);
                if (f != frame) { SkippedOtherFrame++; return null; }
                if (w < 1 || h < 1 || w > _l.Width || h > _l.Height || (haveSize && (w != dw || h != dh))) { SkippedWrongSize++; return null; }
                int row = (int)w * 4;
                BeforeFrameAllocation?.Invoke();
                var rgba = new byte[row * (int)h];
                fixed (byte* p = rgba) if (!CopyRows(at + SlotHeaderLen, _l.Stride, row, (int)h, p)) { SkippedGone++; return null; }
                float[] depth = null;
                if (wantDepth && HasDepth)
                {
                    depth = new float[(int)w * (int)h];
                    fixed (float* p = depth) if (!CopyRows(at + _l.DepthOffset, _l.DepthStride, row, (int)h, (byte*)p)) { SkippedGone++; return null; }
                }
                Thread.MemoryBarrier();
                fixed (byte* p = head) if (!CopyAt(at, p, 16)) { SkippedGone++; return null; }
                if (Wire.Get64(head, 0) != l1 || Wire.Get64(head, 8) != frame) { SkippedOverwritten++; return null; }
                return new Frame { Number = f, TimeUs = time, CameraSeq = cam, Width = (int)w, Height = (int)h, Rgba = rgba, Depth = depth };
            }

            private Frame ReadSlot(ulong frame, int slot, bool haveSize, uint dw, uint dh, bool wantDepth)
            {
                try { return ReadSlotInner(frame,slot,haveSize,dw,dh,wantDepth); }
                catch (OutOfMemoryException) { SkippedAllocation++; return null; }
            }

            private Frame ReadSlotInner(ulong frame, int slot, bool haveSize, uint dw, uint dh, bool wantDepth)
            {
                if (_disposed || slot < 0 || slot >= _l.Slots) return null;
                if (frame == 0) { SkippedOtherFrame++; return null; } // never delivered (CONTRACT.md 9.6, reader step 0)
                if (IsGone) { _gone = true; SkippedGone++; return null; }
                if (haveSize && (dw < 1 || dh < 1 || dw > (uint)_l.Width || dh > (uint)_l.Height)) { SkippedWrongSize++; return null; }
                if (_view == null) return ReadFile(frame, slot, haveSize, dw, dh, wantDepth);
                byte* p = _base + HeaderLen + (long)slot * _l.SlotSize; // checked at open: inside the file
                long l1 = Volatile.Read(ref *(long*)p); // step 1: acquire load
                if ((l1 & 1) != 0) { SkippedBusy++; return null; }
                ulong f = *(ulong*)(p + 8), timeUs = *(ulong*)(p + 16);
                uint camSeq = *(uint*)(p + 24), w = *(uint*)(p + 28), h = *(uint*)(p + 32);
                if (f != frame) { SkippedOtherFrame++; return null; }
                // step 2: the slot header's size must be 1 to the file's, and the datagram's, before anything is copied
                if (w < 1 || h < 1 || w > (uint)_l.Width || h > (uint)_l.Height || (haveSize && (w != dw || h != dh))) { SkippedWrongSize++; return null; }
                BeforeFrameAllocation?.Invoke();
                var rgba = new byte[(int)w * (int)h * 4];
                int row = (int)w * 4;
                fixed (byte* dst = rgba)
                {
                    for (int y = 0; y < (int)h; y++)
                        Buffer.MemoryCopy(p + SlotHeaderLen + (long)y * _l.Stride, dst + (long)y * row, row, row);
                }
                float[] depth = null;
                if (wantDepth && HasDepth)
                {
                    depth = new float[(int)w * (int)h];
                    fixed (float* dd = depth)
                    {
                        for (int y = 0; y < (int)h; y++)
                            Buffer.MemoryCopy(p + _l.DepthOffset + (long)y * _l.DepthStride, dd + (long)y * (int)w, row, row);
                    }
                }
                Thread.MemoryBarrier(); // step 3: the acquire fence (a full fence is stronger), then the second load
                long l2 = Volatile.Read(ref *(long*)p);
                if (l2 != l1 || *(ulong*)(p + 8) != frame) { SkippedOverwritten++; return null; } // overwritten while copying
                return new Frame { Number = f, TimeUs = timeUs, CameraSeq = camSeq, Width = (int)w, Height = (int)h, Rgba = rgba, Depth = depth };
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_view != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); }
                if (_mmf != null) _mmf.Dispose();
                _file.Dispose();
            }
        }

        // ------------------------------------------------------------------ writer

        /// <summary>
        /// Creates and fills a pixel file (the sending side). The file is created exclusively (<c>FileMode.CreateNew</c>: the path must not
        /// exist), sized once before it is announced, never truncated or resized, and deleted by <see cref="Dispose"/> (which unmaps it first:
        /// Windows refuses to delete a file its own process maps). Writers are thread-safe, but one thread should do the writing.
        /// </summary>
        public sealed class Writer : IDisposable
        {
            private readonly FileStream _file;
            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _view;
            private readonly byte* _base;
            private readonly bool _depth;
            private readonly long _depthOffset;
            private ulong _frameNo;
            private bool _closed;

            /// <summary>The absolute path, to announce with <see cref="Link.AnnounceFrames(Writer)"/>.</summary>
            public readonly string Path;
            public readonly int Slots, Width, Height, Stride;
            public readonly long SlotSize;
            public bool HasDepth { get { return _depth; } }
            public long DepthOffset { get { return _depthOffset; } }

            public Writer(string path, int width, int height, int slots, bool withDepth)
            {
                RequireLittleEndian();
                if (slots < 1 || slots > 16 || width < 1 || height < 1) throw new ArgumentException("bad pixel file size");
                if (width > Wire.MaxFrameDim || height > Wire.MaxFrameDim) throw new ArgumentException("width and height are at most " + Wire.MaxFrameDim);
                if ((long)width * height > Wire.MaxFramePixels) throw new ArgumentException("width * height is at most " + Wire.MaxFramePixels + " pixels");
                Path = System.IO.Path.GetFullPath(path);
                Width = width; Height = height; Slots = slots; Stride = width * 4; _depth = withDepth;
                long colourEnd = SlotHeaderLen + (long)Stride * height;
                _depthOffset = withDepth ? Round64(colourEnd) : 0;
                SlotSize = Round64(withDepth ? _depthOffset + (long)width * 4 * height : colourEnd);
                long total = HeaderLen + (long)slots * SlotSize;
                _file = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); // IOException if it exists
                bool acquired = false;
                try
                {
                    _file.SetLength(total); // sized once, here, before the path is announced
                    _mmf = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                    _view = _mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
                    byte* p = null;
                    _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                    acquired = true;
                    _base = p + _view.PointerOffset;
                    // the file is new and zero filled
                    byte[] magic = { (byte)'R', (byte)'B', (byte)'F', (byte)'R', (byte)'A', (byte)'M', (byte)'E', 0 };
                    for (int i = 0; i < 8; i++) _base[i] = magic[i];
                    *(uint*)(_base + 8) = 1;
                    *(uint*)(_base + 12) = (uint)slots;
                    *(uint*)(_base + 16) = (uint)width;
                    *(uint*)(_base + 20) = (uint)height;
                    *(uint*)(_base + 24) = (uint)Stride;
                    *(uint*)(_base + 28) = FormatRgba8;
                    *(long*)(_base + 32) = SlotSize;
                    *(uint*)(_base + 40) = withDepth ? DepthLinearF32 : DepthNone;
                    *(uint*)(_base + 44) = withDepth ? (uint)Stride : 0u;
                    *(long*)(_base + 48) = _depthOffset;
                }
                catch
                {
                    if (acquired) _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    if (_view != null) _view.Dispose();
                    if (_mmf != null) _mmf.Dispose();
                    _file.Dispose();
                    try { File.Delete(Path); } catch (IOException) { }
                    throw;
                }
            }

            /// <summary>Creates a writer in the temp directory, named rewrite-bridge-&lt;pid&gt;-&lt;tag&gt;-&lt;random&gt;.frames.</summary>
            public static Writer CreateTemp(string tag, int width, int height, int slots, bool withDepth)
            {
                var clean = new StringBuilder();
                foreach (char c in tag ?? "")
                    clean.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' ? c : '_');
                var rnd = new byte[4];
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(rnd);
                    string name = "rewrite-bridge-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + clean + "-" + Wire.Get32(rnd, 0).ToString("x8") + ".frames";
                    try { return new Writer(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name), width, height, slots, withDepth); }
                    catch (IOException) when (attempt < 63) { }
                }
                throw new IOException("no free pixel file name");
            }

            private static long Round64(long v) { return (v + 63) & ~63L; }

            /// <summary>
            /// Writes the next frame (the first is number 1) and returns its number; its slot is <c>frame % Slots</c>. Then send
            /// <see cref="Wire.FrameReady"/> with <see cref="Link.SendFrame"/>.
            /// </summary>
            /// <param name="rgba">Tightly packed rows, exactly w * h * 4 bytes.</param>
            /// <param name="linearDepth">w * h values in the same row order, or null for "nothing here" (+inf) everywhere; must be null in a file without a depth plane.</param>
            /// <exception cref="ArgumentException">A size is wrong. Nothing is written and no frame number is used.</exception>
            public ulong Write(byte[] rgba, float[] linearDepth, int w, int h, uint cameraSeq, ulong timeUs)
            {
                lock (this)
                {
                    if (_closed) throw new ObjectDisposedException("PixelFile.Writer");
                    if (w < 1 || h < 1 || w > Width || h > Height) throw new ArgumentException("a " + w + "x" + h + " frame does not fit a " + Width + "x" + Height + " pixel file");
                    if (rgba == null || rgba.Length != (long)w * h * 4) throw new ArgumentException("rgba length is not width * height * 4");
                    if (linearDepth != null)
                    {
                        if (!_depth) throw new ArgumentException("the pixel file has no depth plane");
                        if (linearDepth.Length != w * h) throw new ArgumentException("depth length is not width * height");
                    }
                    ulong frame = ++_frameNo;
                    int slot = (int)(frame % (ulong)Slots);
                    byte* p = _base + HeaderLen + (long)slot * SlotSize;
                    long lockWord = Volatile.Read(ref *(long*)p); // only this writer changes it
                    *(long*)p = lockWord + 1;                      // step 1: odd (a plain store) ...
                    Thread.MemoryBarrier();                        // ... then the release fence (a full fence is stronger)
                    try
                    {
                        *(ulong*)(p + 8) = frame;
                        *(ulong*)(p + 16) = timeUs;
                        *(uint*)(p + 24) = cameraSeq;
                        *(uint*)(p + 28) = (uint)w;
                        *(uint*)(p + 32) = (uint)h;
                        int row = w * 4;
                        fixed (byte* src = rgba)
                        {
                            for (int y = 0; y < h; y++) Buffer.MemoryCopy(src + (long)y * row, p + SlotHeaderLen + (long)y * Stride, row, row);
                        }
                        if (_depth)
                        {
                            for (int y = 0; y < h; y++)
                            {
                                float* d = (float*)(p + _depthOffset + (long)y * Stride);
                                if (linearDepth != null) { for (int x = 0; x < w; x++) d[x] = linearDepth[y * w + x]; }
                                else { for (int x = 0; x < w; x++) d[x] = float.PositiveInfinity; } // nothing here
                            }
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref *(long*)p, lockWord + 2); // step 3: even again (release store)
                        Thread.MemoryBarrier();                      // out before the caller sends the Frame datagram
                    }
                    return frame;
                }
            }

            /// <summary>Unmaps the file, closes it and deletes it. Closing twice is harmless.</summary>
            public void Dispose()
            {
                lock (this)
                {
                    if (_closed) return;
                    _closed = true;
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _view.Dispose();
                    _mmf.Dispose();
                    _file.Dispose();
                    try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
