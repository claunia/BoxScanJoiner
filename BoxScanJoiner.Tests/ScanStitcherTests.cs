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
                                                                Background            = BackgroundFill.Transparent,
                                                                Crop                  = CropMode.None
                                                            },
                                                            cancellationToken: TestContext.Current.CancellationToken);

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
            using StitchResult result =
                ScanStitcher.Stitch(tiles, cancellationToken: TestContext.Current.CancellationToken);

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
                                                            },
                                                            cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(dir, "matches_0_1.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "seams.jpg")));
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();

            if(Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PrefersSharperScanInOverlap()
    {
        using Mat art = TestImages.CreateArtwork();

        Similarity placeA = TestImages.Placement(100, 100, 0, TileSize);
        Similarity placeB = TestImages.Placement(800, 100, 0, TileSize);

        using Mat tileA = TestImages.CutTile(art, placeA, TileSize);
        using Mat tileB = TestImages.CutTile(art, placeB, TileSize);

        // Scan B is out of focus everywhere, so the whole overlap should come from A
        Cv2.GaussianBlur(tileB, tileB, new Size(0, 0), 3);

        using StitchResult result = ScanStitcher.Stitch([tileA, tileB],
                                                        new StitchOptions
                                                        {
                                                            Color = ColorMode.None
                                                        },
                                                        cancellationToken: TestContext.Current.CancellationToken);

        Similarity artToOutput = result.Transforms[0]!.Value.After(placeA.Inverse());
        Point2d    topLeft     = artToOutput.Apply(new Point2d(850, 150));
        var        overlap     = new Rect((int)topLeft.X, (int)topLeft.Y, 600, 900);

        using var expected = new Mat();
        using Mat m        = artToOutput.ToMat();
        Cv2.WarpAffine(art, expected, m, result.Image.Size(), InterpolationFlags.Cubic);

        using Mat blurred = expected.Clone();
        Cv2.GaussianBlur(blurred, blurred, new Size(0, 0), 3);

        double sharp  = Cv2.PSNR(new Mat(result.Image, overlap), new Mat(expected, overlap));
        double blurry = Cv2.PSNR(new Mat(blurred,      overlap), new Mat(expected, overlap));

        Assert.True(sharp > blurry + 5, $"overlap PSNR {sharp:F1} dB, blurred scan would give {blurry:F1} dB");
    }

    [Fact]
    public void CropsAndStraightensBoxOnLid()
    {
        // A 2000 x 1200 box, rotated 3 degrees, lying on a white scanner lid
        using Mat box = TestImages.CreateArtwork(2000, 1200, 99);
        Cv2.Rectangle(box, new Rect(0, 0, 2000, 1200), new Scalar(40, 30, 20), 12);

        using var  lid      = new Mat(1800, 2600, MatType.CV_8UC3, Scalar.All(245));
        Similarity boxOnLid = TestImages.Placement(300, 300, 3, new Size(2000, 1200));

        using(Mat m = boxOnLid.ToMat())
            Cv2.WarpAffine(box, lid, m, lid.Size(), InterpolationFlags.Cubic, BorderTypes.Transparent);

        // Four scans covering the whole box with some lid around it
        Similarity[] placements =
        [
            TestImages.Placement(0, 0,   0, TileSize), TestImages.Placement(1150, 0,   0, TileSize),
            TestImages.Placement(0, 750, 0, TileSize), TestImages.Placement(1150, 750, 0, TileSize)
        ];

        Mat[] tiles = placements.Select(p => TestImages.CutTile(lid, p, TileSize)).ToArray();

        try
        {
            using StitchResult result = ScanStitcher.Stitch(tiles,
                                                            cancellationToken: TestContext.Current.CancellationToken);

            Assert.InRange(result.Image.Width,  1980, 2020);
            Assert.InRange(result.Image.Height, 1185, 1215);

            // The box's dark frame runs along every edge once straightened
            Scalar top  = Cv2.Mean(new Mat(result.Image, new Rect(100, 2,   result.Image.Width - 200, 6)));
            Scalar left = Cv2.Mean(new Mat(result.Image, new Rect(2,   100, 6, result.Image.Height - 200)));
            Assert.True(top.Val0  < 80, $"top edge mean {top.Val0:F0}");
            Assert.True(left.Val0 < 80, $"left edge mean {left.Val0:F0}");
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();
        }
    }

    [Fact]
    public void RejectsSameLogoOnDifferentSides()
    {
        using Mat logo  = TestImages.CreateArtwork(500,  400,  7);
        using Mat sideA = TestImages.CreateArtwork(1400, 1000, 1);
        using Mat sideB = TestImages.CreateArtwork(1400, 1000, 2);

        // Same logo, same size, on two different sides of the box
        using(Mat target = sideA[new Rect(800, 300, 500, 400)]) logo.CopyTo(target);

        using(Mat target = sideB[new Rect(100, 300, 500, 400)]) logo.CopyTo(target);

        using StitchResult result = ScanStitcher.Stitch([sideA, sideB],
                                                        cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.PlacedImages);
        Assert.False(result.Pairs[0].Accepted);
        Assert.NotNull(result.Pairs[0].Agreement);
    }
}