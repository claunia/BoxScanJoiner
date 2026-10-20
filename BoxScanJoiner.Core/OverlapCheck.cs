using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>
///     Confirms that two scans really overlap by comparing them across the whole overlap once aligned. A logo or
///     pattern printed on two different sides of a box can produce many consistent matches, but the rest of the
///     supposed overlap will then disagree.
/// </summary>
internal static class OverlapCheck
{
    /// <summary>Longest side of the grayscale previews compared.</summary>
    public const int PREVIEW_SIZE = 1000;

    const int TILE = 32;

    /// <summary>Tiles flatter than this standard deviation carry no evidence either way.</summary>
    const double MIN_TILE_CONTRAST = 6;

    /// <summary>Normalised cross-correlation above which a tile counts as agreeing.</summary>
    const double TILE_AGREEMENT = 0.6;

    /// <summary>Fewer textured tiles than this cannot confirm or refute an overlap.</summary>
    const int MIN_TILES = 8;

    /// <summary>Makes the blurred grayscale preview the check works on, and its scale.</summary>
    public static (Mat preview, double scale) Preview(Mat gray)
    {
        double scale   = Math.Min(1.0, (double)PREVIEW_SIZE / Math.Max(gray.Width, gray.Height));
        var    preview = new Mat();
        Cv2.Resize(gray, preview, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.GaussianBlur(preview, preview, new Size(0, 0), 1.5);

        return (preview, scale);
    }

    /// <summary>
    ///     Fraction of textured tiles in the overlap where the scans agree, or null if the overlap has too little
    ///     texture to tell.
    /// </summary>
    /// <param name="secondToFirst">Full-resolution transform from the second scan to the first.</param>
    public static double? Agreement(Mat        first, double firstScale, Mat second, double secondScale,
                                    Similarity secondToFirst)
    {
        Similarity t = new Similarity(firstScale, 0, 0, 0).After(secondToFirst)
                                                          .After(new Similarity(1 / secondScale, 0, 0, 0));

        using Mat m      = t.ToMat();
        using var warped = new Mat();
        using var valid  = new Mat();
        using var ones   = new Mat(second.Size(), MatType.CV_8UC1, Scalar.All(255));

        Cv2.WarpAffine(second, warped, m, first.Size(), InterpolationFlags.Linear);
        Cv2.WarpAffine(ones,   valid,  m, first.Size(), InterpolationFlags.Nearest);
        Cv2.Erode(valid, valid, null, iterations: 3);

        var textured = 0;
        var agreeing = 0;

        using var result = new Mat();
        int       rows   = first.Rows;
        int       cols   = first.Cols;

        for(var y = 0; y + TILE <= rows; y += TILE)
        {
            for(var x = 0; x + TILE <= cols; x += TILE)
            {
                var rect = new Rect(x, y, TILE, TILE);

                using Mat v = valid[rect];

                if(Cv2.CountNonZero(v) < TILE * TILE) continue;

                using Mat a = first[rect];
                using Mat b = warped[rect];

                Cv2.MeanStdDev(a, out _, out Scalar sa);
                Cv2.MeanStdDev(b, out _, out Scalar sb);

                if(sa.Val0 < MIN_TILE_CONTRAST || sb.Val0 < MIN_TILE_CONTRAST) continue;

                Cv2.MatchTemplate(a, b, result, TemplateMatchModes.CCoeffNormed);
                textured++;

                if(result.At<float>(0, 0) > TILE_AGREEMENT) agreeing++;
            }
        }

        return textured < MIN_TILES ? null : (double)agreeing / textured;
    }
}