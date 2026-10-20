using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>
///     Locates the box among the aligned scans, so the output can be cropped to it and straightened, leaving out
///     the scanner lid around it.
/// </summary>
internal static class BoxFinder
{
    /// <summary>Longest side of the low-resolution preview the box is searched in.</summary>
    const int PREVIEW_SIZE = 1500;

    /// <summary>Minimum brightness (HSV value) of the scanner lid.</summary>
    const int LID_MIN_VALUE = 215;

    /// <summary>Maximum saturation of the scanner lid, which is close to white.</summary>
    const int LID_MAX_SATURATION = 40;

    /// <summary>The box must cover at least this fraction of the scanned area to be trusted.</summary>
    const double MIN_BOX_FRACTION = 0.1;

    /// <summary>
    ///     Returns a transform from the reference frame to an output frame where the box is axis aligned and starts at
    ///     the origin, with the output size; or null if no box could be told apart from the lid.
    /// </summary>
    public static (Similarity frame, Size size)? Find(IReadOnlyList<Mat> images, Similarity?[] poses)
    {
        List<Point2d> corners = [];

        for(var i = 0; i < images.Count; i++)
        {
            if(poses[i] is not {} pose) continue;

            Mat image = images[i];

            corners.AddRange(new Point2d[]
            {
                new(0, 0), new(image.Width, 0), new(0, image.Height), new(image.Width, image.Height)
            }.Select(pose.Apply));
        }

        double minX  = corners.Min(p => p.X);
        double minY  = corners.Min(p => p.Y);
        double spanX = corners.Max(p => p.X) - minX;
        double spanY = corners.Max(p => p.Y) - minY;
        double scale = Math.Min(1.0, PREVIEW_SIZE / Math.Max(spanX, spanY));

        var previewSize = new Size((int)Math.Ceiling(spanX * scale), (int)Math.Ceiling(spanY * scale));
        var toPreview   = new Similarity(scale, 0, -minX * scale, -minY * scale);

        using var content = new Mat(previewSize, MatType.CV_8UC1, Scalar.All(0));
        using var covered = new Mat(previewSize, MatType.CV_8UC1, Scalar.All(0));

        for(var i = 0; i < images.Count; i++)
        {
            if(poses[i] is not {} pose) continue;

            using Mat small = new();
            Cv2.Resize(images[i], small, new Size(), scale, scale, InterpolationFlags.Area);

            // The small image's pixels are full-resolution pixels divided by the scale
            Similarity t = toPreview.After(pose).After(new Similarity(1 / scale, 0, 0, 0));
            using Mat  m = t.ToMat();

            using var warped = new Mat();
            using var valid  = new Mat();
            using var ones   = new Mat(small.Size(), MatType.CV_8UC1, Scalar.All(255));
            Cv2.WarpAffine(small, warped, m, previewSize, InterpolationFlags.Linear);
            Cv2.WarpAffine(ones,  valid,  m, previewSize, InterpolationFlags.Nearest);

            using Mat notLid = NotLid(warped);
            Cv2.BitwiseAnd(notLid, valid, notLid);
            Cv2.BitwiseOr(content, notLid, content);
            Cv2.BitwiseOr(covered, valid,  covered);
        }

        // Remove dust on the lid, then close small gaps such as white areas of the artwork
        using(Mat open = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)))
            Cv2.MorphologyEx(content, content, MorphTypes.Open, open);

        using(Mat close = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(15, 15)))
            Cv2.MorphologyEx(content, content, MorphTypes.Close, close);

        Point[]? box = LargestContour(content);

        if(box is null || Cv2.ContourArea(box) < MIN_BOX_FRACTION * Cv2.CountNonZero(covered)) return null;

        RotatedRect rect   = Cv2.MinAreaRect(box);
        Point2f[]   points = rect.Points();

        // Straighten along whichever side of the rectangle is closer to horizontal
        Point2f e1         = points[1] - points[0];
        Point2f e2         = points[2] - points[1];
        bool    horizontal = Math.Abs(e1.X) >= Math.Abs(e1.Y);
        Point2f across     = horizontal ? e1 : e2;
        double  angle      = NormalizeAngle(Math.Atan2(across.Y, across.X));
        double  width      = Length(horizontal ? e1 : e2) / scale;
        double  height     = Length(horizontal ? e2 : e1) / scale;

        // Centre of the box in the reference frame
        Point2d centre = toPreview.Inverse().Apply(new Point2d(rect.Center.X, rect.Center.Y));

        Similarity frame = new Similarity(1, 0, width / 2, height / 2)
                          .After(new Similarity(Math.Cos(-angle), Math.Sin(-angle), 0,         0))
                          .After(new Similarity(1,                0,                -centre.X, -centre.Y));

        return (frame, new Size((int)Math.Round(width), (int)Math.Round(height)));
    }

    /// <summary>255 where a pixel is not the near-white scanner lid.</summary>
    static Mat NotLid(Mat bgr)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var lid = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, LID_MIN_VALUE), new Scalar(180, LID_MAX_SATURATION, 255), lid);
        Cv2.BitwiseNot(lid, lid);

        return lid;
    }

    static Point[]? LargestContour(Mat mask)
    {
        Cv2.FindContours(mask,
                         out Point[][] contours,
                         out _,
                         RetrievalModes.External,
                         ContourApproximationModes.ApproxSimple);

        return contours.Length == 0 ? null : contours.MaxBy(c => Cv2.ContourArea(c));
    }

    /// <summary>Maps the direction of a line, in radians, to the equivalent angle within ±45°.</summary>
    static double NormalizeAngle(double a)
    {
        while(a > Math.PI   / 4) a -= Math.PI / 2;
        while(a <= -Math.PI / 4) a += Math.PI / 2;

        return a;
    }

    static double Length(Point2f v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
}