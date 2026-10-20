using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class OpenCvTests
{
    [Fact]
    public void NativeLibraryLoads() => Assert.StartsWith("4.", Cv2.GetVersionString());
}
