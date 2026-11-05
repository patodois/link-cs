using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Patodois.RewriteLink.Tests
{
    internal static class PixelSafety
    {
        private sealed class TruncateAfterLength : FileStream
        {
            private readonly string _path;
            private bool _armed = true;
            public TruncateAfterLength(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1) { _path = path; }
            public override long Length
            {
                get
                {
                    long n = base.Length;
                    if (_armed)
                    {
                        _armed = false;
                        using (var f = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) f.SetLength(0);
                        Console.WriteLine("truncated after successful Length");
                    }
                    return n;
                }
            }
        }
        internal static int Child()
        {
            using (var w = PixelFile.Writer.CreateTemp("truncate", 64, 64, 1, false))
            {
                w.Write(new byte[64 * 64 * 4], null, 64, 64, 0, 1);
                using (var r = PixelFile.Reader.Open(w.Path))
                {
                    var field = typeof(PixelFile.Reader).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic);
                    var old = (FileStream)field.GetValue(r);
                    field.SetValue(r, new TruncateAfterLength(w.Path));
                    old.Dispose();
                    return r.Read(new Wire.FrameReady(1, 0, 0, 64, 64), false) == null ? 0 : 3;
                }
            }
        }
        internal static void Run()
        {
            T.Test("truncation after size cannot terminate the reader", () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { Console.WriteLine("Windows: different mapped-file semantics, Unix race test skipped"); return; }
                var psi = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardOutput = true, RedirectStandardError = true };
                if (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                psi.ArgumentList.Add("--truncate-child");
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Console.WriteLine("truncate child exit=" + p.ExitCode + "\n" + stdout + stderr);
                    T.Check(p.ExitCode == 0, "reader survived the size/access race");
                }
            });
        }
        internal static int Bench()
        {
            int[,] sizes = { {1280,720}, {1920,1080}, {3840,2160}, {8192,8192}, {16384,1}, {16384,4096} };
            for (int i = 0; i < sizes.GetLength(0); i++) for (int dep = 0; dep < 2; dep++)
            {
                int width = sizes[i,0], height = sizes[i,1], pixels = width * height, n = pixels > 10000000 ? 5 : 30;
                var px = new byte[pixels * 4]; Array.Fill(px, (byte)7);
                float[] depth = dep != 0 ? new float[pixels] : null; if (depth != null) Array.Fill(depth, 2f);
                using (var w = PixelFile.Writer.CreateTemp("bench", width, height, 1, dep != 0))
                {
                    w.Write(px, depth, width, height, 0, 1);
                    using (var r = PixelFile.Reader.Open(w.Path))
                    {
                        var ready = new Wire.FrameReady(1, 0, 0, (uint)width, (uint)height);
                        double[] times = new double[n];
                        for (int j = 0; j < n + 2; j++)
                        {
                            GC.Collect(); GC.WaitForPendingFinalizers();
                            long start = Stopwatch.GetTimestamp();
                            var f = r.Read(ready, dep != 0);
                            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (f == null || f.Rgba[0] != 7) throw new Exception("bad benchmark frame");
                            GC.KeepAlive(f);
                            if (j >= 2) times[j-2] = ms;
                        }
                        Array.Sort(times);
                        Console.WriteLine("cs," + width + "," + height + "," + (dep != 0 ? "true" : "false") + "," + n + "," + times[n/2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "," + times[n*95/100].ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            return 0;
        }
    }
}
