using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class PoseGraphTests
{
    static readonly Size TileSize = new(1400, 1000);

    static PoseGraphResult Solve(Mat art, Similarity[] placements, StitchOptions options)
    {
        var features = new ImageFeatures[placements.Length];

        for(var i = 0; i < placements.Length; i++)
        {
            using Mat tile = TestImages.CutTile(art, placements[i], TileSize);
            features[i] = FeatureMatcher.Detect(tile, options);
        }

        List<PairMatch> pairs = [];

        for(var i = 0; i < placements.Length; i++)
        {
            for(int j = i + 1; j < placements.Length; j++)
            {
                if(FeatureMatcher.Match(i, features[i], j, features[j], options, out _, out _) is {} m) pairs.Add(m);
            }
        }

        foreach(ImageFeatures f in features) f.Dispose();

        return PoseGraph.Solve(placements.Length, pairs);
    }

    [Fact]
    public void PlacesGridOfRotatedTiles()
    {
        using Mat art = TestImages.CreateArtwork();

        Similarity[] placements =
        [
            TestImages.Placement(0,  0,   0,   TileSize), TestImages.Placement(1000, 20,  3,  TileSize),
            TestImages.Placement(10, 600, 180, TileSize), TestImages.Placement(990,  590, -4, TileSize)
        ];

        PoseGraphResult result = Solve(art,
                                       placements,
                                       new StitchOptions
                                       {
                                           MaxDetectionDimension = 1000
                                       });

        Assert.Equal(4, result.Order.Length);
        Assert.True(result.RmsError < 2, $"RMS {result.RmsError:F2}");

        Similarity toRef = placements[result.Reference].Inverse();

        for(var i = 0; i < placements.Length; i++)
        {
            Assert.NotNull(result.Poses[i]);
            double err = TestImages.CornerError(toRef.After(placements[i]), result.Poses[i]!.Value, TileSize);
            Assert.True(err < 1.5, $"tile {i} corner error {err:F2}px");
        }
    }

    [Fact]
    public void LeavesDisconnectedTileOut()
    {
        using Mat art = TestImages.CreateArtwork(4400, 1200);

        Similarity[] placements =
        [
            TestImages.Placement(0,    0, 0, TileSize), TestImages.Placement(900, 0, 2, TileSize),
            TestImages.Placement(3000, 0, 0, TileSize)
        ];

        PoseGraphResult result = Solve(art, placements, new StitchOptions());

        Assert.Equal(2, result.Order.Length);
        Assert.Null(result.Poses[2]);
    }
}