using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class ScanStitcherTests
{
    static readonly Size TileSize = new(1400, 1000);

    static readonly Similarity[] GridPlacements =
    [
        TestImages.Placement(100, 100, 0,   TileSize), TestImages.Placement(1100, 120, 3,  TileSize),
        TestImages.Placement(110, 700, 180, TileSize), TestImages.Placement(1090, 690, -4, TileSize)
    ];

    /// <summary>PSNR between the stitched output and the artwork mapped into the output frame, over covered pixels.</summary>
    static double Psnr(Mat art, StitchResult result, Similarity[] placements)
    {
        int        i           = Array.FindIndex(result.Transforms.ToArray(), t => t.HasValue);
        Similarity artToOutput = result.Transforms[i]!.Value.After(placements[i].Inverse());

        using var expected = new Mat();
        using Mat m        = artToOutput.ToMat();
        Cv2.WarpAffine(art, expected, m, result.Image.Size(), InterpolationFlags.Cubic);

        using var bgr   = new Mat();
        using var alpha = new Mat();
        Cv2.CvtColor(result.Image, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.ExtractChannel(result.Image, alpha, 3);

        using var diff = new Mat();
        Cv2.Absdiff(bgr, expected, diff);
        diff.ConvertTo(diff, MatType.CV_32FC3);
        Cv2.Multiply(diff, diff, diff);

        Scalar sum   = Cv2.Sum(diff.SetTo(Scalar.All(0), alpha.LessThan(255)));
        int    count = Cv2.CountNonZero(alpha);
        double mse   = (sum.Val0 + sum.Val1 + sum.Val2) / (3.0 * count);

        return 10 * Math.Log10(255 * 255 / mse);
    }

    [Theory]
    [InlineData(SeamMode.OptimalCut)]
    [InlineData(SeamMode.None)]
    public void JoinsGridIntoArtwork(SeamMode seam)
    {
        using Mat art = TestImages.CreateArtwork(2700, 1900);

        Mat[] tiles = GridPlacements.Select(p => TestImages.CutTile(art, p, TileSize)).ToArray();

        try
        {
            using StitchResult result = ScanStitcher.Stitch(tiles,
                                                            new StitchOptions
                                                            {
                                                                MaxDetectionDimension = 1000,
                                                                Seam                  = seam,
                                                                Color                 = ColorMode.None,
                                                                Background            = BackgroundFill.Transparent
                                                            });

            Assert.Equal(4, result.PlacedImages);
            Assert.Empty(result.UnconnectedImages);

            // Canvas must be the bounding box of the ground-truth tile corners in the output frame
            Similarity artToOutput = result.Transforms[0]!.Value.After(GridPlacements[0].Inverse());

            Point2d[] corners = GridPlacements.SelectMany(p => new Point2d[]
                                               {
                                                   new(0, 0), new(TileSize.Width, 0), new(0, TileSize.Height),
                                                   new(TileSize.Width, TileSize.Height)
                                               }.Select(c => artToOutput.After(p).Apply(c)))
                                              .ToArray();

            Assert.InRange(corners.Min(c => c.X),                       -2, 2);
            Assert.InRange(corners.Min(c => c.Y),                       -2, 2);
            Assert.InRange(corners.Max(c => c.X) - result.Image.Width,  -2, 2);
            Assert.InRange(corners.Max(c => c.Y) - result.Image.Height, -2, 2);

            double psnr = Psnr(art, result, GridPlacements);
            Assert.True(psnr > 28, $"PSNR {psnr:F1} dB");
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();
        }
    }

    [Fact]
    public void ReportsUnconnectedScan()
    {
        using Mat art = TestImages.CreateArtwork(4400, 1200);

        Similarity[] placements =
        [
            TestImages.Placement(0,    0, 0, TileSize), TestImages.Placement(900, 0, 2, TileSize),
            TestImages.Placement(3000, 0, 0, TileSize)
        ];

        Mat[] tiles = placements.Select(p => TestImages.CutTile(art, p, TileSize)).ToArray();

        try
        {
            using StitchResult result = ScanStitcher.Stitch(tiles);

            Assert.Equal(2,   result.PlacedImages);
            Assert.Equal([2], result.UnconnectedImages);
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();
        }
    }

    [Fact]
    public void WritesDebugImages()
    {
        using Mat art = TestImages.CreateArtwork();

        Mat[]  tiles = GridPlacements.Take(2).Select(p => TestImages.CutTile(art, p, TileSize)).ToArray();
        string dir   = Path.Combine(Path.GetTempPath(), $"bsj-debug-{Guid.NewGuid():N}");

        try
        {
            using StitchResult result = ScanStitcher.Stitch(tiles,
                                                            new StitchOptions
                                                            {
                                                                DebugDirectory = dir
                                                            });

            Assert.True(File.Exists(Path.Combine(dir, "matches_0_1.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "seams.jpg")));
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();

            if(Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}