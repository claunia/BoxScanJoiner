using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class FeatureMatcherTests
{
    static readonly Size TileSize = new(1400, 1000);

    [Theory]
    [InlineData(FeatureDetectorKind.Sift,  0.0)]
    [InlineData(FeatureDetectorKind.Sift,  4.0)]
    [InlineData(FeatureDetectorKind.Sift,  180.0)]
    [InlineData(FeatureDetectorKind.Akaze, 3.0)]
    [InlineData(FeatureDetectorKind.Orb,   -2.0)]
    public void RecoversTransformBetweenOverlappingTiles(FeatureDetectorKind detector, double degrees)
    {
        using Mat art = TestImages.CreateArtwork();

        Similarity placeA = TestImages.Placement(100, 200, 0,       TileSize);
        Similarity placeB = TestImages.Placement(900, 400, degrees, TileSize);

        using Mat tileA = TestImages.CutTile(art, placeA, TileSize);
        using Mat tileB = TestImages.CutTile(art, placeB, TileSize);

        var options = new StitchOptions
        {
            Detector              = detector,
            MaxDetectionDimension = 1000
        };

        using ImageFeatures fa = FeatureMatcher.Detect(tileA, options);
        using ImageFeatures fb = FeatureMatcher.Detect(tileB, options);

        PairMatch? match = FeatureMatcher.Match(0, fa, 1, fb, options, out _);

        Assert.NotNull(match);

        Similarity expected = placeA.Inverse().After(placeB);

        Assert.True(TestImages.CornerError(expected, match.SecondToFirst, TileSize) < 2.0,
                    $"corner error {TestImages.CornerError(expected, match.SecondToFirst, TileSize):F2}px");
    }

    [Fact]
    public void RejectsNonOverlappingTiles()
    {
        using Mat art = TestImages.CreateArtwork(4000, 1200);

        using Mat tileA = TestImages.CutTile(art, TestImages.Placement(0,    0, 0, TileSize), TileSize);
        using Mat tileB = TestImages.CutTile(art, TestImages.Placement(2500, 0, 0, TileSize), TileSize);

        var options = new StitchOptions();

        using ImageFeatures fa = FeatureMatcher.Detect(tileA, options);
        using ImageFeatures fb = FeatureMatcher.Detect(tileB, options);

        Assert.Null(FeatureMatcher.Match(0, fa, 1, fb, options, out _));
    }
}