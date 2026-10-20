using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

public class ColorCorrectorTests
{
    static readonly Size TileSize = new(1400, 1000);

    /// <summary>Per tile BGR gain and offset simulating a scanner's varying color response.</summary>
    static readonly (double[] gain, double[] offset)[] Tweaks =
    [
        ([1.00, 1.00, 1.00], [0, 0, 0]), ([1.06, 0.95, 1.02], [-6, 5, 0]), ([0.94, 1.03, 0.97], [4, -3, 6]),
        ([1.03, 1.03, 0.92], [0, -5, 3])
    ];

    static Mat Tweak(Mat tile, (double[] gain, double[] offset) t)
    {
        var result = new Mat();

        using var m = Mat.FromPixelData(3,
                                        4,
                                        MatType.CV_64FC1,
                                        new[,]
                                        {
                                            {
                                                t.gain[0], 0, 0, t.offset[0]
                                            },
                                            {
                                                0, t.gain[1], 0, t.offset[1]
                                            },
                                            {
                                                0, 0, t.gain[2], t.offset[2]
                                            }
                                        });

        Cv2.Transform(tile, result, m);

        return result;
    }

    [Fact]
    public void CorrectionsUndoScannerColorDifferences()
    {
        using Mat art = TestImages.CreateArtwork(2700, 1900);

        Similarity[] placements =
        [
            TestImages.Placement(100, 100, 0,   TileSize), TestImages.Placement(1100, 120, 3,  TileSize),
            TestImages.Placement(110, 700, 180, TileSize), TestImages.Placement(1090, 690, -4, TileSize)
        ];

        Mat[] tiles = placements.Select((p, i) =>
                                 {
                                     using Mat tile = TestImages.CutTile(art, p, TileSize);

                                     return Tweak(tile, Tweaks[i]);
                                 })
                                .ToArray();

        try
        {
            using StitchResult result = ScanStitcher.Stitch(tiles,
                                                            new StitchOptions
                                                            {
                                                                MaxDetectionDimension = 1000
                                                            },
                                                            cancellationToken: TestContext.Current.CancellationToken);

            (double[] gain, double[] offset) reference = Tweaks[result.Reference];

            for(var i = 0; i < tiles.Length; i++)
            {
                ColorCorrection? correction = result.ColorCorrections[i];
                Assert.NotNull(correction);

                for(var channel = 0; channel < 3; channel++)
                {
                    foreach(double v in new[]
                            {
                                40.0, 128.0, 220.0
                            })
                    {
                        double scanned  = Tweaks[i].gain[channel] * v + Tweaks[i].offset[channel];
                        double expected = reference.gain[channel] * v + reference.offset[channel];
                        double actual   = correction.Apply(channel, scanned);

                        Assert.True(Math.Abs(actual - expected) < 1.5,
                                    $"tile {i} channel {channel} value {v}: {actual:F2} vs {expected:F2}");
                    }
                }
            }
        }
        finally
        {
            foreach(Mat t in tiles) t.Dispose();
        }
    }
}