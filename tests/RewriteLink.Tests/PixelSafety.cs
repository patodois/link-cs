using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Patodois.RewriteLink.Tests
{
    internal static class PixelSafety
    {
        [DllImport("libc",EntryPoint="pread",SetLastError=true)]
        private static extern IntPtr TestPRead(Microsoft.Win32.SafeHandles.SafeFileHandle file,IntPtr buffer,UIntPtr count,long offset);
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
                    var wrapped = new TruncateAfterLength(w.Path);
                    field.SetValue(r, wrapped);
                    typeof(PixelFile.Reader).GetField("_readHandle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(r, wrapped.SafeFileHandle);
                    old.Dispose();
                    return r.Read(new Wire.FrameReady(1, 0, 0, 64, 64), false) == null ? 0 : 3;
                }
            }
        }
        private sealed class FailingRead : FileStream
        {
            internal FailingRead(string path) : base(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1) { }
            public override int Read(byte[] buffer,int offset,int count) { throw new IOException("injected provider failure"); }
        }

        private static void CallSample(MethodInfo method, object target, object[] args)
        {
            try { method.Invoke(target,args); }
            catch(TargetInvocationException e) when(e.InnerException!=null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }

        private static void ApiTests()
        {
            T.Test("missing native pread falls back without escaping the game loop", () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                foreach (Exception failure in new Exception[] { new DllNotFoundException("injected"), new EntryPointNotFoundException("injected"), new BadImageFormatException("injected"), new PlatformNotSupportedException("injected") })
                using (var w = PixelFile.Writer.CreateTemp("fallback", 2,2,1,true))
                {
                    byte[] colour = new byte[16]; Array.Fill(colour,(byte)7);
                    float[] depth = new float[4]; Array.Fill(depth,2f);
                    w.Write(colour,depth,2,2,0,1);
                    using (var r = PixelFile.Reader.Open(w.Path))
                    {
                        int calls=0;
                        r.NativeReadOverride = (file,buffer,count,offset) => { calls++; throw failure; };
                        var ready=new Wire.FrameReady(1,0,0,2,2);
                        var f=r.ReadLatest(ready,true);
                        T.Check(f!=null && f.Rgba[0]==7 && f.Depth[3]==2f,"managed positional fallback copies both planes");
                        T.Check(r.Read(ready,false)!=null && calls==1,"unavailable native entry is not retried");
                    }
                }
            });
            T.Test("managed fallback I/O exceptions are contained as misses", () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                using(var w=PixelFile.Writer.CreateTemp("fallback-io",2,2,1,false))
                {
                    w.Write(new byte[16],null,2,2,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        var field=typeof(PixelFile.Reader).GetField("_file",BindingFlags.Instance|BindingFlags.NonPublic);
                        var old=(FileStream)field.GetValue(r); field.SetValue(r,new FailingRead(w.Path)); old.Dispose();
                        r.NativeReadOverride=(file,buffer,count,offset)=>{throw new DllNotFoundException("injected");};
                        T.Check(r.ReadLatest(new Wire.FrameReady(1,0,0,2,2),false)==null && r.IsGone,"failed fallback is a miss, not a game-loop exception");
                    }
                }
            });
            T.Test("demo reports an unsupported pixel process without escaping its host loop", () =>
            {
                using(var w=PixelFile.Writer.CreateTemp("demo-unsupported",64,48,3,true))
                using(var host=Link.Host(new LinkConfig("host"){ControlPort=0}))
                using(var guest=Link.Guest(new LinkConfig("guest"){ControlPort=host.ControlPort}))
                {
                    T.Check(T.Until(2000,()=>{host.Poll();guest.Poll();return host.IsConnected&&guest.IsConnected;}),"loopback connected");
                    guest.AnnounceFrames(w);guest.Poll();
                    var program=Type.GetType("Patodois.RewriteLink.Demo.Program, LinkDemo",true);
                    program.GetField("_clock",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Stopwatch.StartNew());
                    var output=new StringWriter();var saved=Console.Out;
                    PixelFile.ProcessPointerSize=4;
                    try
                    {
                        Console.SetOut(output);
                        CallSample(program.GetMethod("RunHost",BindingFlags.Static|BindingFlags.NonPublic),null,new object[]{host,0.1});
                    }
                    finally {Console.SetOut(saved);PixelFile.ProcessPointerSize=IntPtr.Size;}
                    T.Check(output.ToString().Contains("cannot open the guest's pixel file")&&output.ToString().Contains("64-bit"),"demo logs the unsupported process");
                }
            });
            T.Test("interop reports an unsupported pixel process without escaping its event handler", () =>
            {
                using(var w=PixelFile.Writer.CreateTemp("interop-unsupported",64,48,3,true))
                {
                    var type=Type.GetType("Patodois.RewriteLink.InteropPeer.HostPeer, InteropPeer",true);
                    object peer=Activator.CreateInstance(type,new object[]{0,"unsupported-pixels",null});
                    using(var link=(Link)type.BaseType.GetField("Link",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(peer))
                    {
                        var output=new StringWriter();var saved=Console.Out;
                        PixelFile.ProcessPointerSize=4;
                        try
                        {
                            Console.SetOut(output);
                            CallSample(type.GetMethod("OnEvent",BindingFlags.Instance|BindingFlags.NonPublic),peer,new object[]{new LinkEvent.FrameChannel(new Control.FrameChannel(w.Path,64,48,3))});
                        }
                        finally {Console.SetOut(saved);PixelFile.ProcessPointerSize=IntPtr.Size;}
                        T.Check(output.ToString().Contains("cannot open the guest's pixel file")&&output.ToString().Contains("64-bit"),"interop records the unsupported process");
                    }
                }
            });
            T.Test("unsupported 32-bit pixel APIs fail before native calls", () =>
            {
                using(var w=PixelFile.Writer.CreateTemp("pointer-size",2,2,1,false))
                {
                    PixelFile.ProcessPointerSize=4;
                    try {T.Throws<PlatformNotSupportedException>(()=>{using(var r=PixelFile.Reader.Open(w.Path)){}},"64-bit pixel requirement");}
                    finally {PixelFile.ProcessPointerSize=IntPtr.Size;}
                }
            });
            T.Test("frame allocation failures are misses without advancing order", () =>
            {
                using(var w=PixelFile.Writer.CreateTemp("allocation",2,2,1,false))
                {
                    w.Write(new byte[16],null,2,2,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        r.BeforeFrameAllocation=()=>{throw new OutOfMemoryException("injected");};
                        var ready=new Wire.FrameReady(1,0,0,2,2);
                        T.Check(r.ReadLatest(ready,false)==null && !r.IsGone && r.SkippedAllocation==1,"allocation miss keeps a healthy reader");
                        r.BeforeFrameAllocation=null;
                        T.Check(r.ReadLatest(ready,false)!=null,"same frame remains eligible after allocation recovery");
                    }
                }
            });
            foreach (string scratch in new[] { "padding", "plane", "managed" })
            T.Test("scratch allocation failure keeps reader healthy: " + scratch, () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                using(var w=PixelFile.Writer.CreateTemp("scratch-allocation",16,16,1,true))
                {
                    var colour=new byte[8*9*4];var depth=new float[8*9];
                    for(int i=0;i<colour.Length;i++)colour[i]=(byte)(i*37);
                    for(int i=0;i<depth.Length;i++)depth[i]=i+0.5f;
                    w.Write(colour,depth,8,9,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        if(scratch=="plane")r.NativeVectorReadOverride=(file,vectors,count,at)=>{throw new EntryPointNotFoundException("injected");};
                        if(scratch=="managed")r.NativeReadOverride=(file,buffer,count,at)=>{throw new EntryPointNotFoundException("injected");};
                        r.BeforeScratchAllocation=kind=>{if(kind==scratch)throw new OutOfMemoryException("injected " + kind);};
                        var ready=new Wire.FrameReady(1,0,0,8,9);
                        T.Check(r.ReadLatest(ready,true)==null && !r.IsGone && r.SkippedAllocation==1 && r.SkippedGone==0,"scratch OOM is an allocation miss, not a gone file");
                        r.BeforeScratchAllocation=null;
                        var frame=r.ReadLatest(ready,true);
                        T.Check(frame!=null,"same frame succeeds after scratch allocation recovers");
                        for(int i=0;i<colour.Length;i++)T.Check(frame.Rgba[i]==colour[i],"recovered colour");
                        for(int i=0;i<depth.Length;i++)T.Check(frame.Depth[i]==depth[i],"recovered depth");
                    }
                }
            });
            T.Test("short vector reads resume inside each iovec", () =>
            {
                if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))return;
                using(var w=PixelFile.Writer.CreateTemp("short",16,16,1,true))
                {
                    var pixels=new byte[8*9*4];var depth=new float[8*9];
                    for(int j=0;j<pixels.Length;j++)pixels[j]=(byte)(j*37);
                    for(int j=0;j<depth.Length;j++)depth[j]=j+0.5f;
                    w.Write(pixels,depth,8,9,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        int calls=0;
                        r.NativeVectorReadOverride=(file,vectors,count,at)=>{
                            calls++;var pointer=Marshal.ReadIntPtr(vectors);
                            ulong length=(ulong)Marshal.ReadIntPtr(vectors,IntPtr.Size).ToInt64();
                            return TestPRead(file,pointer,(UIntPtr)Math.Min(7UL,length),at);
                        };
                        var got=r.ReadLatest(new Wire.FrameReady(1,0,0,8,9),true);
                        T.Check(got!=null&&System.Linq.Enumerable.SequenceEqual(got.Rgba,pixels)&&System.Linq.Enumerable.SequenceEqual(got.Depth,depth)&&calls>4,"all partial colour/padding/depth reads completed");
                    }
                }
            });
            T.Test("missing preadv falls back and still copies both planes", () =>
            {
                if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))return;
                using(var w=PixelFile.Writer.CreateTemp("no-vector",16,16,1,true))
                {
                    var pixels=new byte[8*9*4];var depth=new float[8*9];
                    for(int j=0;j<pixels.Length;j++)pixels[j]=(byte)(j*37);
                    for(int j=0;j<depth.Length;j++)depth[j]=j+0.5f;
                    w.Write(pixels,depth,8,9,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        int calls=0;r.NativeVectorReadOverride=(file,vectors,count,at)=>{calls++;throw new EntryPointNotFoundException("injected");};
                        var got=r.ReadLatest(new Wire.FrameReady(1,0,0,8,9),true);
                        T.Check(got!=null&&System.Linq.Enumerable.SequenceEqual(got.Rgba,pixels)&&System.Linq.Enumerable.SequenceEqual(got.Depth,depth)&&calls==1,"missing vector entry point uses bounded scalar copies");
                    }
                }
            });
            T.Test("pixel widths can shrink and grow without stale pixels", () =>
            {
                foreach(bool wantDepth in new[]{false,true})
                using(var w=PixelFile.Writer.CreateTemp("widths",16,16,1,true))
                using(var r=PixelFile.Reader.Open(w.Path))
                {
                    int frame=0;
                    foreach(int width in new[]{15,14,15,8,7,16,1,14,15})
                    {
                        frame++;int count=width*9;
                        var pixels=new byte[count*4];var depth=new float[count];
                        for(int j=0;j<pixels.Length;j++) pixels[j]=(byte)(j*37+frame*11);
                        for(int j=0;j<count;j++) depth[j]=j+frame*1000;
                        w.Write(pixels,depth,width,9,0,(ulong)frame);
                        var got=r.ReadLatest(new Wire.FrameReady((ulong)frame,0,0,(uint)width,9),wantDepth);
                        T.Check(got!=null && System.Linq.Enumerable.SequenceEqual(got.Rgba,pixels),"colour frame "+frame+" width "+width);
                        if(wantDepth) T.Check(System.Linq.Enumerable.SequenceEqual(got.Depth,depth),"depth frame "+frame);
                    }
                }
            });
            T.Test("truncation inside colour or depth cannot produce a frame", () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                foreach (bool depth in new[] {false,true})
                using (var w = PixelFile.Writer.CreateTemp("planes",16,8,1,true))
                {
                    w.Write(new byte[128],new float[32],8,4,0,1);
                    using (var r=PixelFile.Reader.Open(w.Path))
                    {
                        long at=PixelFile.HeaderLen+(depth?w.DepthOffset:PixelFile.SlotHeaderLen);
                        r.BeforeCopy=offset => { if(offset==at) using(var f=new FileStream(w.Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) f.SetLength(at+64); };
                        T.Check(r.Read(new Wire.FrameReady(1,0,0,8,4),depth)==null,"partial "+(depth?"depth":"colour")+" copy must be rejected");
                    }
                }
            });
            T.Test("cropped planes use bounded reusable spans", () =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                using(var w=PixelFile.Writer.CreateTemp("gather",16,8,1,true))
                {
                    var colour=new byte[128]; Array.Fill(colour,(byte)7); var depth=new float[32]; Array.Fill(depth,2f);
                    w.Write(colour,depth,8,4,0,1);
                    using(var r=PixelFile.Reader.Open(w.Path))
                    {
                        int calls=0;r.BeforeCopy=_=>calls++;
                        var f=r.Read(new Wire.FrameReady(1,0,0,8,4),true);
                        T.Check(f!=null && f.Rgba[127]==7 && f.Depth[31]==2f && calls==4,"header, two spans and final identity");
                    }
                }
            });
            T.Test("two files have independent presentation order and optional depth", () =>
            {
                using(var a=PixelFile.Writer.CreateTemp("two-a",2,2,3,false))
                using(var b=PixelFile.Writer.CreateTemp("two-b",3,2,3,true))
                {
                    for(int i=0;i<5;i++) a.Write(new byte[16],null,2,2,0,1);
                    b.Write(new byte[24],new float[6],3,2,0,1);
                    using(var ar=PixelFile.Reader.Open(a.Path)) using(var br=PixelFile.Reader.Open(b.Path))
                    {
                        var old=new Wire.FrameReady(5,2,0,2,2); var first=new Wire.FrameReady(1,1,0,3,2);
                        T.Check(ar.ReadLatest(old,false)!=null && br.ReadLatest(old,true)==null,"old file announcement is not a current frame");
                        var f=br.ReadLatest(first,false);
                        T.Check(f!=null && f.Depth==null,"new file starts at 1 and depth can be skipped");
                        T.Check(br.ReadLatest(first,true)==null && br.SkippedStale==1,"duplicate counted");
                        long count=br.SkippedOtherFrame;
                        T.Check(br.ReadLatest(null,false)==null && br.SkippedOtherFrame==count,"null is a miss without counters");
                    }
                }
            });
        }

        internal static void Run()
        {
            ApiTests();
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
            int[,] sizes = { {1280,720,1280,720}, {1920,1080,1920,1080}, {3840,2160,3840,2160}, {8192,8192,8192,8192}, {16384,1,16384,1}, {16384,4096,16384,4096}, {1920,1080,1280,720}, {3840,2160,1920,1080}, {16384,4096,8192,4096}, {16384,4096,1,4096} };
            for (int i = 0; i < sizes.GetLength(0); i++) for (int dep = 0; dep < 2; dep++)
            {
                int fw = sizes[i,0], fh = sizes[i,1], width = sizes[i,2], height = sizes[i,3], pixels = width * height, n = pixels > 10000000 ? 5 : 30;
                if (Environment.GetEnvironmentVariable("RBL_BENCH_CROPPED_ONLY") != null && width==fw) continue;
                var px = new byte[pixels * 4]; Array.Fill(px, (byte)7);
                float[] depth = dep != 0 ? new float[pixels] : null; if (depth != null) Array.Fill(depth, 2f);
                using (var w = PixelFile.Writer.CreateTemp("bench", fw, fh, 1, dep != 0))
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
                        Console.WriteLine("cs," + fw + "," + fh + "," + width + "," + height + "," + (dep != 0 ? "true" : "false") + "," + n + "," + times[n/10].ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "," + times[n/2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + "," + times[n*95/100].ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            return 0;
        }
    }
}
