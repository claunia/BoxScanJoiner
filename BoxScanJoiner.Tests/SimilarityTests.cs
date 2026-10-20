using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class SimilarityTests
{
    [Fact]
    public void InverseUndoesTransform()
    {
        var     t = new Similarity(Math.Cos(0.3) * 1.02, Math.Sin(0.3) * 1.02, 40, -25);
        Point2d p = t.Inverse().Apply(t.Apply(new Point2d(123, 456)));

        Assert.Equal(123, p.X, 6);
        Assert.Equal(456, p.Y, 6);
    }

    [Fact]
    public void AfterComposesInOrder()
    {
        var first  = new Similarity(0, 1, 10, 0);
        var second = new Similarity(1, 0, 0,  5);
        var p      = new Point2d(3, 4);

        Point2d expected = second.Apply(first.Apply(p));
        Point2d actual   = second.After(first).Apply(p);

        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }
}