using BoxScanJoiner.Core;
using OpenCvSharp;

namespace BoxScanJoiner.Tests;

/// <summary>Procedurally generated "box artwork" and tiles cut from it with known transforms.</summary>
static class TestImages
{
    public static Mat CreateArtwork(int width = 2400, int height = 1600, int seed = 1234)
    {
        var rng = new Random(seed);
        var img = new Mat(height, width, MatType.CV_8UC3, new Scalar(235, 240, 245));

        for(var i = 0; i < 400; i++)
        {
            var color = new Scalar(rng.Next(256), rng.Next(256), rng.Next(256));
            var p     = new Point(rng.Next(width), rng.Next(height));

            switch(rng.Next(4))
            {
                case 0:
                    Cv2.Rectangle(img,
                                  new Rect(p.X, p.Y, rng.Next(10, 200), rng.Next(10, 200)),
                                  color,
                                  rng.Next(2) == 0 ? -1 : rng.Next(1, 6));

                    break;
                case 1:
                    Cv2.Circle(img, p, rng.Next(5, 100), color, rng.Next(2) == 0 ? -1 : rng.Next(1, 6));

                    break;
                case 2:
                    Cv2.Line(img, p, new Point(rng.Next(width), rng.Next(height)), color, rng.Next(1, 5));

                    break;
                default:
                    Cv2.PutText(img,
                                $"BOX {rng.Next(100000)}",
                                p,
                                HersheyFonts.HersheySimplex,
                                rng.NextDouble() * 2 + 0.5,
                                color,
                                rng.Next(1, 4));

                    break;
            }
        }

        using var noise = new Mat(height, width, MatType.CV_8UC3);
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(4, 4, 4));
        Cv2.Add(img, noise, img);

        return img;
    }

    /// <summary>
    ///     Cuts a tile of <paramref name="size" /> whose pixel (x, y) shows artwork at <c>tileToArtwork(x, y)</c>.
    /// </summary>
    public static Mat CutTile(Mat artwork, Similarity tileToArtwork, Size size)
    {
        var       tile = new Mat();
        using Mat m    = tileToArtwork.ToMat();

        Cv2.WarpAffine(artwork,
                       tile,
                       m,
                       size,
                       InterpolationFlags.Cubic | InterpolationFlags.WarpInverseMap,
                       BorderTypes.Constant,
                       new Scalar(255, 255, 255));

        return tile;
    }

    /// <summary>Tile-to-artwork transform rotating the tile by <paramref name="degrees" /> about its centre.</summary>
    public static Similarity Placement(double x, double y, double degrees, Size tileSize)
    {
        double rad = degrees * Math.PI / 180;
        double a   = Math.Cos(rad);
        double b   = Math.Sin(rad);
        double cx  = tileSize.Width  / 2.0;
        double cy  = tileSize.Height / 2.0;

        // Rotate around tile centre, then move tile centre to (x + cx, y + cy)
        return new Similarity(a, b, x + cx - (a * cx - b * cy), y + cy - (b * cx + a * cy));
    }

    /// <summary>Largest distance between where two transforms send the corners of <paramref name="size" />.</summary>
    public static double CornerError(Similarity expected, Similarity actual, Size size)
    {
        Point2d[] corners = [new(0, 0), new(size.Width, 0), new(0, size.Height), new(size.Width, size.Height)];

        return corners.Max(c =>
        {
            Point2d e = expected.Apply(c);
            Point2d g = actual.Apply(c);

            return Math.Sqrt((e.X - g.X) * (e.X - g.X) + (e.Y - g.Y) * (e.Y - g.Y));
        });
    }
}