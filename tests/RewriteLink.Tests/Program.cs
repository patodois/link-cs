using System;

namespace Patodois.RewriteLink.Tests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length != 0 && args[0] == "--truncate-child") return PixelSafety.Child();
            if (args.Length != 0 && args[0] == "--pixel-bench") return PixelSafety.Bench();
            if (args.Length != 0 && args[0] == "--pixel-safety") { PixelSafety.Run(); return T.Failed == 0 ? 0 : 1; }
            Console.WriteLine("RewriteLink tests (" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ", "
                + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")");
            WireTests.Run();
            ControlTests.Run();
            LiveTests.Run();
            PixelTests.Run();
            PixelSafety.Run();
            TokenTests.Run();
            VectorTests.Run();
            T.Cleanup();
            Console.WriteLine();
            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed == 0 ? 0 : 1;
        }
    }
}
