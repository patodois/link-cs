using System;

namespace Patodois.RewriteLink.Tests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("RewriteLink tests (" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ", "
                + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")");
            WireTests.Run();
            ControlTests.Run();
            LiveTests.Run();
            PixelTests.Run();
            VectorTests.Run();
            T.Cleanup();
            Console.WriteLine();
            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed == 0 ? 0 : 1;
        }
    }
}
