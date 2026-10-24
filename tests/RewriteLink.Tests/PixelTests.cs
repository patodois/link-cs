using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using static Patodois.RewriteLink.Tests.T;

namespace Patodois.RewriteLink.Tests
{
    internal static unsafe class PixelTests
    {
        private static readonly bool Windows = Path.DirectorySeparatorChar == '\\';

        private static byte[] HeaderBytes(uint slots, uint w, uint h, uint stride, ulong slotSize)
        {
            var b = new byte[64];
            System.Text.Encoding.ASCII.GetBytes("RBFRAME").CopyTo(b, 0);
            Wire.Put32(b, 8, 1); Wire.Put32(b, 12, slots); Wire.Put32(b, 16, w); Wire.Put32(b, 20, h); Wire.Put32(b, 24, stride); Wire.Put32(b, 28, 1);
            Wire.Put64(b, 32, slotSize);
            return b;
        }

        private static bool Refused(byte[] header, long size)
        {
            try { PixelFile.Validate(header, size); return false; } catch (PixelFile.FormatException) { return true; }
        }

        private static bool OpenRefused(string path)
        {
            try { using (PixelFile.Reader.Open(path)) return false; } catch (PixelFile.FormatException) { return true; }
        }

        public static void Run()
        {
            Test("pixel file: colour only round trip, the layout of revision 2", () =>
            {
                string p = TempPath("a.frames");
                using (var w = new PixelFile.Writer(p, 4, 2, 3, false))
                {
                    Eq(w.SlotSize, 128L, "64 slot header + 4*4*2 pixels = 96, rounded up to 128");
                    var px = new byte[32];
                    for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 3);
                    ulong f = w.Write(px, null, 4, 2, 77, 123456789);
                    Eq(f, 1UL, "the first frame is 1");
                    byte[] raw = ReadAll(p);
                    Eq(raw.Length, 64 + 3 * 128, "file length");
                    Eq(Wire.Get32(raw, 8), 1u, "version"); Eq(Wire.Get32(raw, 12), 3u, "slots"); Eq(Wire.Get32(raw, 16), 4u, "width"); Eq(Wire.Get32(raw, 20), 2u, "height");
                    Eq(Wire.Get32(raw, 24), 16u, "stride"); Eq(Wire.Get32(raw, 28), 1u, "format"); Eq(Wire.Get64(raw, 32), 128UL, "slot_size");
                    Eq(Wire.Get32(raw, 40), 0u, "depth_format is 0 in a colour-only file"); Eq(Wire.Get64(raw, 48), 0UL, "depth_offset is 0");
                    Eq(Wire.Get64(raw, 64 + 128 + 8), 1UL, "frame 1 went to slot 1 % 3"); Eq(Wire.Get64(raw, 64 + 128) & 1, 0UL, "the lock is even");
                    using (var r = PixelFile.Reader.Open(p))
                    {
                        Check(!r.HasDepth, "no depth");
                        var fr = r.Read(1, 1, true);
                        Check(fr != null, "frame read");
                        Eq(fr.CameraSeq, 77u, "camera_seq"); Eq(fr.TimeUs, 123456789UL, "time_us"); Check(fr.Rgba.SequenceEqual(px), "pixels"); Check(fr.Depth == null, "no depth array");
                        Check(r.Read(2, 2, false) == null, "a slot that holds another frame is refused");
                        Check(r.Read(1, 99, false) == null, "a slot out of range");
                    }
                }
                Check(!File.Exists(p), "the writer deletes the file on close");
            });

            Test("pixel file: the depth plane at depth_offset, linear f32 metres", () =>
            {
                string p = TempPath("b.frames");
                using (var w = new PixelFile.Writer(p, 5, 3, 3, true))
                {
                    Eq(w.SlotSize, 192L, "colour end 64 + 60 = 124 -> depth_offset 128; plane 60 -> 188 -> slot_size 192");
                    var d = new float[15];
                    for (int i = 0; i < 15; i++) d[i] = i + 0.5f;
                    d[7] = float.PositiveInfinity;
                    w.Write(new byte[60], d, 5, 3, 1, 5);
                    byte[] raw = ReadAll(p);
                    Eq(Wire.Get32(raw, 40), 1u, "depth_format"); Eq(Wire.Get32(raw, 44), 20u, "depth_stride"); Eq(Wire.Get64(raw, 48), 128UL, "depth_offset");
                    using (var r = PixelFile.Reader.Open(p))
                    {
                        Check(r.HasDepth, "has depth");
                        var fr = r.Read(1, 1, true);
                        Check(fr.Depth != null && fr.Depth[14] == 14.5f && fr.Depth[0] == 0.5f, "depth values");
                        Check(float.IsPositiveInfinity(fr.Depth[7]), "+inf survives");
                        Check(r.Read(1, 1, false).Depth == null, "depth not requested");
                    }
                    w.Write(new byte[60], null, 5, 3, 1, 6); // no depth given: +inf everywhere
                    using (var r = PixelFile.Reader.Open(p)) Check(r.Read(2, 2, true).Depth.All(float.IsPositiveInfinity), "+inf everywhere");
                }
            });

            Test("pixel file: an odd lock is skipped, a changed lock discards the copy", () =>
            {
                string p = TempPath("c.frames");
                using (var w = new PixelFile.Writer(p, 2, 2, 2, false))
                using (var r = PixelFile.Reader.Open(p))
                {
                    w.Write(new byte[16], null, 2, 2, 0, 1);
                    Check(r.Read(1, 1, false) != null, "an even lock reads");
                    SetLock(p, 64 + w.SlotSize, 3); // a writer in the middle of a write
                    Check(r.Read(1, 1, false) == null, "an odd lock means the slot is being written: skip");
                    Eq(r.SkippedBusy, 1L, "counted");
                    SetLock(p, 64 + w.SlotSize, 4);
                    Check(r.Read(1, 1, false) != null, "even again");
                }
            });

            Test("pixel file: malformed files are refused, an unknown depth_format reads as colour only", () =>
            {
                string p = TempPath("d.frames");
                using (var w = new PixelFile.Writer(p, 4, 4, 2, true))
                {
                    w.Write(new byte[64], new float[16], 4, 4, 0, 1);
                    Patch(p, 40, 9, 4); // a future depth format
                    using (var r = PixelFile.Reader.Open(p)) { Check(!r.HasDepth, "colour only, not an error"); Check(r.Read(1, 1, true).Depth == null, "no depth delivered"); }
                    Patch(p, 40, 1, 4); Patch(p, 44, 13, 4);
                    Check(OpenRefused(p), "depth_stride != width * 4");
                    Patch(p, 44, 16, 4); Patch(p, 48, 100, 8);
                    Check(OpenRefused(p), "depth_offset is not a multiple of 64");
                    Patch(p, 48, 64, 8);
                    Check(OpenRefused(p), "depth_offset is inside the colour plane");
                    Patch(p, 48, (ulong)w.DepthOffset, 8); Patch(p, 32, 128, 8);
                    Check(OpenRefused(p), "the plane would end after the slot");
                    Patch(p, 32, (ulong)w.SlotSize, 8);
                    using (PixelFile.Reader.Open(p)) { }
                    Patch(p, 0, 'X', 1);
                    Check(OpenRefused(p), "bad magic");
                    Patch(p, 0, 'R', 1); Patch(p, 28, 2, 4);
                    Check(OpenRefused(p), "an unknown format");
                    Patch(p, 28, 1, 4); Patch(p, 8, 2, 4);
                    Check(OpenRefused(p), "version 2");
                }
                Check(OpenRefused("relative.frames"), "a relative path is refused");
                Check(OpenRefused("./relative.frames"), "a relative path is refused");
            });

            Test("pixel file: a truncated copy is refused", () =>
            {
                string p = TempPath("e.frames"), cut = TempPath("e-cut.frames");
                using (var w = new PixelFile.Writer(p, 4, 4, 3, false))
                {
                    byte[] whole = ReadAll(p);
                    File.WriteAllBytes(cut, whole.Take((int)(64 + w.SlotSize)).ToArray()); // the header and one slot of three
                    Check(OpenRefused(cut), "a file shorter than its slots");
                }
                File.WriteAllBytes(cut, new byte[] { (byte)'R', (byte)'B' });
                Check(OpenRefused(cut), "a file shorter than its header");
            });

            Test("review M4: width or height above 16384, or more than 2^26 pixels, is refused; 16384 x 1 and 8192 x 8192 are not", () =>
            {
                foreach (var c in new uint[][]
                {
                    new uint[] { 16384, 1, 1 }, new uint[] { 1, 16384, 1 }, new uint[] { 8192, 8192, 1 }, new uint[] { 16384, 4096, 1 },
                    new uint[] { 16384, 8192, 0 }, new uint[] { 8193, 8192, 0 }, new uint[] { 16385, 1, 0 }, new uint[] { 1, 16385, 0 }, new uint[] { 32768, 1, 0 },
                    new uint[] { 1, 32768, 0 }, new uint[] { 0, 1, 0 }, new uint[] { 1, 0, 0 }, new uint[] { 0x40000000u, 1, 0 }, new uint[] { 0xFFFFFFFFu, 0xFFFFFFFFu, 0 },
                })
                {
                    ulong stride = (ulong)c[0] * 4, slot = (64 + stride * c[1] + 63) & ~63UL;
                    Check(Refused(HeaderBytes(1, c[0], c[1], (uint)stride, slot), (long)(64 + slot)) == (c[2] == 0), c[0] + "x" + c[1] + (c[2] == 0 ? " must be refused" : " must open"));
                }
            });

            Test("review J2: a slot_size that makes slots * slot_size wrap is refused", () =>
            {
                // (2^64 + 128) / 3 = 0x5555555555555580: 3 * that wraps to 128 and it is a multiple of 64, so no other check refuses it
                Check(Refused(HeaderBytes(3, 1, 1, 4, 0x5555555555555580UL), 4096), "the wrapping slot_size");
                Eq(unchecked(3UL * 0x5555555555555580UL), 128UL, "the product wraps to 128, inside the 4096-byte file");
                Check(Refused(HeaderBytes(16, 1, 1, 4, 0xFFFFFFFFFFFFFFC0UL), 1L << 40), "a huge slot_size");
                Check(Refused(HeaderBytes(2, 4, 4, 16, 64), 4096), "slot_size below the colour end");
                var h = HeaderBytes(1, 4, 4, 16, 192);
                Wire.Put32(h, 40, 1); Wire.Put32(h, 44, 16); Wire.Put64(h, 48, 0xFFFFFFFFFFFFFFC0UL);
                Check(Refused(h, 64 + 192), "a depth_offset that overflows");
                Check(Refused(HeaderBytes(0, 4, 4, 16, 192), 4096) && Refused(HeaderBytes(17, 4, 4, 16, 192), 1 << 20), "slots 0 and 17");
            });

            Test("pixel file: stride must be exactly width * 4", () =>
            {
                Check(Refused(HeaderBytes(1, 4, 4, 20, 192), 64 + 192), "stride 20 for width 4");
                Check(Refused(HeaderBytes(1, 4, 4, 12, 192), 64 + 192), "stride 12 for width 4");
                Check(!Refused(HeaderBytes(1, 4, 4, 16, 192), 64 + 192), "stride 16");
                Check(Refused(HeaderBytes(1, 4, 4, 16, 192), 64 + 191), "one byte short");
                var h = HeaderBytes(1, 4, 4, 16, 128);
                Wire.Put32(h, 44, 999); Wire.Put64(h, 48, 12345); // the reserved depth fields of a revision 1 or 2 file
                var l = PixelFile.Validate(h, 64 + 128);
                Eq(l.DepthFormat, 0u, "ignored when depth_format is 0");
            });

            Test("review M4: the writer stops at 16384 too, creates no file, and creates exclusively", () =>
            {
                string p = TempPath("big.frames");
                foreach (var wh in new[] { new[] { 16385, 1 }, new[] { 1, 16385 }, new[] { 16384, 8192 }, new[] { 8193, 8192 }, new[] { 0, 8 } })
                {
                    Throws<ArgumentException>(() => new PixelFile.Writer(p, wh[0], wh[1], 1, false).Dispose(), wh[0] + "x" + wh[1]);
                    Check(!File.Exists(p), "and creates no file");
                }
                Throws<ArgumentException>(() => new PixelFile.Writer(p, 8, 8, 0, false), "0 slots");
                Throws<ArgumentException>(() => new PixelFile.Writer(p, 8, 8, 17, false), "17 slots");
                string q = TempPath("cn.frames");
                using (var w = new PixelFile.Writer(q, 8, 8, 2, false))
                {
                    w.Write(Enumerable.Repeat((byte)0x5A, 8 * 8 * 4).ToArray(), null, 8, 8, 0, 1);
                    byte[] before = ReadAll(q);
                    Throws<IOException>(() => new PixelFile.Writer(q, 4, 4, 3, true), "an existing path is never opened");
                    Check(ReadAll(q).SequenceEqual(before), "and the file is untouched");
                }
                Check(!File.Exists(q), "deleted by Dispose");
                using (var w = PixelFile.Writer.CreateTemp("tag/with:odd chars", 4, 4, 1, false))
                {
                    Check(Path.IsPathRooted(w.Path), "absolute");
                    Check(Path.GetFileName(w.Path).StartsWith("rewrite-bridge-" + Environment.ProcessId + "-tag_with_odd_chars-") && w.Path.EndsWith(".frames"), w.Path);
                }
            });

            Test("review L4: a bad write is refused and leaves the slot lock even and the frame number unused", () =>
            {
                string p = TempPath("w.frames");
                using (var w = new PixelFile.Writer(p, 4, 2, 3, true))
                {
                    var ok = new byte[32]; var okd = new float[8];
                    var bad = new object[][]
                    {
                        new object[] { new byte[5], okd, 4, 2 }, new object[] { ok, new float[3], 4, 2 }, new object[] { new byte[40], new float[10], 5, 2 },
                        new object[] { new byte[48], new float[12], 4, 3 }, new object[] { new byte[0], new float[0], 0, 0 }, new object[] { ok, okd, -1, 2 },
                    };
                    foreach (var a in bad) Throws<ArgumentException>(() => w.Write((byte[])a[0], (float[])a[1], (int)a[2], (int)a[3], 0, 1), a[2] + "x" + a[3]);
                    byte[] raw = ReadAll(p);
                    for (int s = 0; s < 3; s++) Eq(Wire.Get64(raw, 64 + (int)(s * w.SlotSize)) & 1, 0UL, "slot " + s + " lock is even");
                    Eq(w.Write(ok, okd, 4, 2, 0, 1), 1UL, "the refused writes used no frame number");
                }
                using (var w = new PixelFile.Writer(p, 4, 2, 3, false)) Throws<ArgumentException>(() => w.Write(new byte[32], new float[8], 4, 2, 0, 1), "a colour-only file refuses depth");
                var w2 = new PixelFile.Writer(p, 4, 2, 3, false);
                w2.Dispose(); w2.Dispose(); // closing twice is fine
                Throws<ObjectDisposedException>(() => w2.Write(new byte[32], null, 4, 2, 0, 1), "a closed writer refuses writes");
            });

            if (!Windows)
            {
                Test("review M3: a symlink, a directory and a FIFO are refused", () =>
                {
                    string real = TempPath("real.frames"), link = TempPath("link.frames"), fifo = TempPath("fifo.frames");
                    using (var w = new PixelFile.Writer(real, 4, 4, 2, false))
                    {
                        File.CreateSymbolicLink(link, real);
                        Check(OpenRefused(link), "a symlink to " + real + " must be refused");
                        using (PixelFile.Reader.Open(real)) { }
                    }
                    var mk = Process.Start(new ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false });
                    mk.WaitForExit();
                    long t0 = Environment.TickCount64;
                    Check(OpenRefused(fifo), "a FIFO is refused");
                    Check(Environment.TickCount64 - t0 < 2000, "and does not hang the receiver");
                    Check(OpenRefused(Path.GetTempPath()), "a directory");
                    Check(OpenRefused("/dev/null"), "a device");
                });

                Test("review M5: a pixel file truncated under a reader is gone, not a crash", () =>
                {
                    string p = TempPath("trunc.frames");
                    using (var w = new PixelFile.Writer(p, 64, 64, 3, true))
                    {
                        ulong f = w.Write(new byte[64 * 64 * 4], new float[64 * 64], 64, 64, 0, 1);
                        using (var r = PixelFile.Reader.Open(p))
                        {
                            Check(r.Read(f, (int)(f % 3), true) != null, "reads before");
                            Check(!r.IsGone, "not gone");
                            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.SetLength(64); // the peer truncates it
                            Check(r.IsGone, "gone");
                            Check(r.Read(f, (int)(f % 3), true) == null, "the read is skipped, not a SIGBUS");
                            Eq(r.SkippedGone, 1L, "counted");
                        }
                    }
                });
            }

            Test("paths: absolute under the Unix and the Windows rules", () =>
            {
                Check(PixelFile.IsAbsolute("/tmp/x", false) && !PixelFile.IsAbsolute("tmp/x", false) && !PixelFile.IsAbsolute("C:\\x", false), "unix");
                Check(PixelFile.IsAbsolute("C:\\x", true) && PixelFile.IsAbsolute("c:/x", true) && PixelFile.IsAbsolute("\\\\server\\share\\x", true) && PixelFile.IsAbsolute("\\\\?\\C:\\x", true), "windows absolute");
                Check(!PixelFile.IsAbsolute("C:x", true) && !PixelFile.IsAbsolute("\\x", true) && !PixelFile.IsAbsolute("x\\y", true) && !PixelFile.IsAbsolute("", true), "windows relative");
            });

            Test("pixel file: the seqlock under contention, a writer thread against a reader thread, a torn copy is never accepted", () =>
            {
                Contention(true);
                Contention(false); // the same race with no lock check, to show that the writer does tear what is being read
            });
        }

        // ------------------------------------------------------------------ helpers

        private static byte[] ReadAll(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var b = new byte[fs.Length];
                int off = 0;
                while (off < b.Length) off += fs.Read(b, off, b.Length - off);
                return b;
            }
        }

        private static void Patch(string path, int offset, ulong value, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Position = offset;
                for (int i = 0; i < bytes; i++) fs.WriteByte((byte)(value >> (8 * i)));
            }
        }

        private static void SetLock(string path, long offset, ulong value) { Patch(path, (int)offset, value, 8); }

        // ------------------------------------------------------------------ contention

        private const int CW = 96, CH = 64, CSlots = 2; // two slots: the writer laps the reader's slot all the time

        private static void Fill(ulong f, byte[] px, float[] depth)
        {
            for (int i = 0; i < px.Length; i += 8) for (int b = 0; b < 8; b++) px[i + b] = (byte)(f >> (8 * b));
            Array.Fill(depth, (float)f);
        }

        private static bool Whole(ulong f, byte[] px, float[] depth)
        {
            for (int i = 0; i < px.Length; i += 8) for (int b = 0; b < 8; b++) if (px[i + b] != (byte)(f >> (8 * b))) return false;
            foreach (float d in depth) if (d != (float)f) return false;
            return true;
        }

        private static void Contention(bool seqlock)
        {
            string p = TempPath("contention.frames");
            int ms = int.Parse(Environment.GetEnvironmentVariable("RBL_CONTENTION_MS") ?? "1500");
            var w = new PixelFile.Writer(p, CW, CH, CSlots, true);
            var r = PixelFile.Reader.Open(p);
            long announce = 0;
            bool stop = false;
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    var px = new byte[CW * CH * 4]; var depth = new float[CW * CH];
                    ulong f = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        Fill(f + 1, px, depth);
                        f = w.Write(px, depth, CW, CH, (uint)((f + 1) * 3), (f + 1) * 1000 + 7);
                        Volatile.Write(ref announce, (long)f);
                    }
                }
                catch (Exception e) { failure = e; }
            });
            long accepted = 0, rejected = 0, torn = 0, wrong = 0, attempts = 0;
            var rpx = new byte[CW * CH * 4]; var rdepth = new float[CW * CH];
            long depthOffset = (64 + CW * 4 * CH + 63) / 64 * 64;
            using (var mmf = MemoryMappedFile.CreateFromFile(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false))
            using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                writer.Start();
                long end = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < end)
                {
                    ulong f = (ulong)Volatile.Read(ref announce);
                    if (f == 0) continue;
                    attempts++;
                    int slot = (int)(f % CSlots);
                    if (seqlock)
                    {
                        var fr = r.Read(f, slot, true);
                        if (fr == null) { rejected++; continue; }
                        accepted++;
                        if (fr.Number != f || fr.TimeUs != f * 1000 + 7 || fr.CameraSeq != (uint)(f * 3) || fr.Width != CW || fr.Height != CH) wrong++;
                        if (!Whole(f, fr.Rgba, fr.Depth)) torn++;
                    }
                    else
                    {
                        long baseOff = 64 + slot * r.SlotSize;
                        if (view.ReadUInt64(baseOff + 8) != f) { rejected++; continue; }
                        view.ReadArray(baseOff + 64, rpx, 0, rpx.Length);
                        view.ReadArray(baseOff + depthOffset, rdepth, 0, rdepth.Length);
                        accepted++;
                        if (!Whole(f, rpx, rdepth)) torn++;
                    }
                }
                Volatile.Write(ref stop, true);
                writer.Join(5000);
            }
            Console.WriteLine("     " + (seqlock ? "seqlock reader:           " : "unchecked copy (compare): ") + Volatile.Read(ref announce) + " frames written, " + attempts + " reads tried, "
                + accepted + " accepted, " + rejected + " refused, " + torn + " torn"
                + (seqlock ? " (busy " + r.SkippedBusy + ", other frame " + r.SkippedOtherFrame + ", overwritten while copying " + r.SkippedOverwritten + "; wrong header " + wrong + ")" : ""));
            long frames = Volatile.Read(ref announce), busy = r.SkippedBusy, over = r.SkippedOverwritten;
            r.Dispose();
            w.Dispose();
            Check(!writer.IsAlive, "the writer thread ends");
            Check(failure == null, "writer failed: " + failure);
            if (seqlock)
            {
                Check(frames > 1000, "the writer made progress: " + frames);
                Check(accepted > 0, "the reader got frames through");
                Check(busy + over > 0, "the writer never collided with the reader: the test proves nothing");
                Eq(torn, 0L, "torn frames accepted by the seqlock reader");
                Eq(wrong, 0L, "accepted frames with a header that does not match the frame");
            }
        }
    }
}
