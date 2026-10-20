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
    ///     Rows whose fine detail is below this fraction of the box interior's are taken to lie beyond the edge: lid,
    ///     shadow or the neighbouring face out of focus.
    /// </summary>
    const double BEYOND_EDGE_DETAIL = 0.15;

    /// <summary>Rows, at preview resolution, over which print texture must continue to count as the box.</summary>
    const int INSIDE_ROWS = 24;

    /// <summary>
    ///     Outside the fold, this fraction of rows (and at least <see cref="MIN_STRAY_ROWS" />) may show texture,
    ///     from thin lines such as the far boundary of a shadow.
    /// </summary>
    const double STRAY_FRACTION = 0.2;

    const int MIN_STRAY_ROWS = 8;


    /// <summary>No side is trimmed by more than this fraction of the box.</summary>
    const double MAX_TRIM = 0.35;

    /// <summary>Each side is examined in this many strips so torn or bulging edges are handled.</summary>
    const int EDGE_STRIPS = 32;

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

        // Images reduced to the preview resolution, and their fine detail, kept for examining the box edges later
        var smalls  = new Mat?[images.Count];
        var details = new (Mat x, Mat y)?[images.Count];

        try
        {
            for(var i = 0; i < images.Count; i++)
            {
                if(poses[i] is not {} pose) continue;

                var small = new Mat();
                smalls[i] = small;
                Cv2.Resize(images[i], small, new Size(), scale, scale, InterpolationFlags.Area);
                details[i] = FineDetail(images[i], small.Size());

                using Mat m = PreviewTransform(toPreview, pose, scale).ToMat();

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

            return FitBox(content, covered, smalls, details, poses, toPreview, scale);
        }
        finally
        {
            foreach(Mat? small in smalls) small?.Dispose();

            foreach((Mat x, Mat y)? detail in details)
            {
                detail?.x.Dispose();
                detail?.y.Dispose();
            }
        }
    }

    /// <summary>Maps a preview-resolution copy of a scan into the preview frame.</summary>
    static Similarity PreviewTransform(Similarity toPreview, Similarity pose, double scale) =>

        // The small image's pixels are full-resolution pixels divided by the scale
        toPreview.After(pose).After(new Similarity(1 / scale, 0, 0, 0));

    static (Similarity frame, Size size)? FitBox(Mat content, Mat covered, Mat?[] smalls, (Mat x, Mat y)?[] details,
                                                 Similarity?[] poses, Similarity toPreview, double scale)
    {
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

        // The lid test keeps shadows and the blurred neighbouring face; trim those back to the box's own edges
        using EdgeMaps maps = MeasureEdges(smalls, details, poses, toPreview, scale, frame, width, height);

        int previewHeight = maps.TextureX.Rows;
        int previewWidth  = maps.TextureX.Cols;

        // Texture is measured along each side; the fold is a step across it
        int top    = BeyondEdge(maps.TextureX, maps.CrispY, false, false);
        int bottom = BeyondEdge(maps.TextureX, maps.CrispY, false, true);
        int left   = BeyondEdge(maps.TextureY, maps.CrispX, true,  false);
        int right  = BeyondEdge(maps.TextureY, maps.CrispX, true,  true);

        double trimTop    = top    * height / previewHeight;
        double trimBottom = bottom * height / previewHeight;
        double trimLeft   = left   * width  / previewWidth;
        double trimRight  = right  * width  / previewWidth;

        frame = new Similarity(1, 0, -trimLeft, -trimTop).After(frame);

        return (frame,
                new Size((int)Math.Round(width  - trimLeft - trimRight),
                         (int)Math.Round(height - trimTop  - trimBottom)));
    }

    /// <summary>
    ///     Mean fine detail of a full-resolution scan over each pixel of a preview of <paramref name="size" />, as
    ///     variation along x and along y. Print in focus is full of halftone dots and sharp strokes, while anything
    ///     out of focus, in shadow or plain lid has almost none, a far clearer difference than the preview itself
    ///     shows. Out-of-focus faces are often streaked parallel to the edge, which only shows as variation across
    ///     it, so each side is judged by the variation along it.
    /// </summary>
    static (Mat alongX, Mat alongY) FineDetail(Mat image, Size size)
    {
        using var gray    = new Mat();
        using var blurred = new Mat();

        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        // Light blur so the scanner's own pixel noise does not count as detail
        Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 0.7);

        // Second derivatives respond to halftone dots and strokes but hardly to the smooth ramps of blurred areas
        return (Derivative(blurred, 2, 0, size), Derivative(blurred, 0, 2, size));
    }

    static Mat Derivative(Mat gray, int xOrder, int yOrder, Size size)
    {
        using var d      = new Mat();
        using var energy = new Mat();
        var       result = new Mat();

        Cv2.Sobel(gray, d, MatType.CV_16S, xOrder, yOrder);
        Cv2.ConvertScaleAbs(d, energy);
        Cv2.Resize(energy, result, size, 0, 0, InterpolationFlags.Area);

        return result;
    }

    /// <summary>Per-pixel edge measures over the box in the output frame, at preview resolution.</summary>
    sealed class EdgeMaps : IDisposable
    {
        /// <summary>Fine detail of the print along x and along y, low beyond the box edges.</summary>
        public required Mat TextureX { get; init; }
        public required Mat TextureY { get; init; }

        /// <summary>Crisp variation along x and along y: in-focus edges such as the box's fold.</summary>
        public required Mat CrispX { get; init; }
        public required Mat CrispY { get; init; }

        public void Dispose()
        {
            TextureX.Dispose();
            TextureY.Dispose();
            CrispX.Dispose();
            CrispY.Dispose();
        }
    }

    /// <summary>
    ///     Measures edges over the box. Each scan is measured inside its own coverage and the strongest response kept,
    ///     so the borders between scans and of the scanned area are not mistaken for box edges or texture.
    /// </summary>
    static EdgeMaps MeasureEdges(Mat?[] smalls, (Mat x, Mat y)?[] details, Similarity?[] poses, Similarity toPreview,
                                 double scale,  Similarity        frame,   double        width, double     height)
    {
        var size = new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));

        Similarity toOutput = new Similarity(scale, 0, 0, 0).After(frame).After(toPreview.Inverse());

        var maps = new EdgeMaps
        {
            TextureX = new Mat(size, MatType.CV_32FC1, Scalar.All(0)),
            TextureY = new Mat(size, MatType.CV_32FC1, Scalar.All(0)),
            CrispX   = new Mat(size, MatType.CV_32FC1, Scalar.All(0)),
            CrispY   = new Mat(size, MatType.CV_32FC1, Scalar.All(0))
        };

        for(var i = 0; i < smalls.Length; i++)
        {
            if(smalls[i] is not {} small || details[i] is not {} detail || poses[i] is not {} pose) continue;

            using Mat m = toOutput.After(PreviewTransform(toPreview, pose, scale)).ToMat();

            using var gray   = new Mat();
            using var warped = new Mat();
            using var valid  = new Mat();
            using var ones   = new Mat(small.Size(), MatType.CV_8UC1, Scalar.All(255));
            Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.WarpAffine(gray, warped, m, size, InterpolationFlags.Linear);
            Cv2.WarpAffine(ones, valid,  m, size, InterpolationFlags.Nearest);
            Cv2.Erode(valid, valid, null, iterations: 3);

            using var outside = new Mat();
            Cv2.BitwiseNot(valid, outside);

            using var f = new Mat();
            warped.ConvertTo(f, MatType.CV_32F);
            Cv2.GaussianBlur(f, f, new Size(0, 0), 1);
            AccumulateMax(f, 1, 0, outside, maps.CrispX);
            AccumulateMax(f, 0, 1, outside, maps.CrispY);

            AccumulateWarped(detail.x, m, size, outside, maps.TextureX);
            AccumulateWarped(detail.y, m, size, outside, maps.TextureY);
        }

        return maps;
    }

    static void AccumulateWarped(Mat source, Mat m, Size size, Mat outside, Mat target)
    {
        using var warped = new Mat();
        using var f      = new Mat();
        Cv2.WarpAffine(source, warped, m, size, InterpolationFlags.Linear);
        warped.ConvertTo(f, MatType.CV_32F);
        f.SetTo(Scalar.All(0), outside);
        Cv2.Max(target, f, target);
    }

    static void AccumulateMax(Mat image, int xOrder, int yOrder, Mat outside, Mat target)
    {
        using var d = new Mat();
        Cv2.Sobel(image, d, MatType.CV_32F, xOrder, yOrder);
        Cv2.Absdiff(d, Scalar.All(0), d);
        d.SetTo(Scalar.All(0), outside);
        Cv2.Max(target, d, target);
    }

    /// <summary>
    ///     Number of rows from one side of the box preview (its top, or its left when <paramref name="columns" /> is
    ///     set, or the opposite side when <paramref name="fromEnd" /> is set) that lie beyond the box's fold edge.
    /// </summary>
    /// <remarks>
    ///     Beyond the fold there is only lid, shadow or the neighbouring face out of focus, so there is almost no
    ///     texture along the edge, while the fold itself is a crisp step. Each strip of the side takes, among its
    ///     strong steps with only featureless area outside them, the innermost one with texture right inside it;
    ///     the far boundaries of shadows and blurred faces are crisp too, but nothing printed follows them. On a
    ///     plain box nothing follows the fold either, and the strongest step is taken. Requiring a featureless
    ///     outside keeps lines in the artwork, such as printed borders, from being taken for the fold.
    /// </remarks>
    static int BeyondEdge(Mat textureMap, Mat crispMap, bool columns, bool fromEnd)
    {
        using Mat along = Orient(textureMap, columns, fromEnd);
        using Mat crisp = Orient(crispMap,   columns, fromEnd);

        int h = along.Rows;
        int w = along.Cols;

        if(h < 4 * INSIDE_ROWS || w < EDGE_STRIPS) return 0;

        // Texture level of the box itself, from its middle rows
        using var rowProfile = new Mat();
        Cv2.Reduce(along, rowProfile, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
        rowProfile.GetArray(out float[] rows);
        float[] interior  = rows[(h / 4)..(h * 3 / 4)].Order().ToArray();
        double  threshold = BEYOND_EDGE_DETAIL * interior[interior.Length / 2];

        int limit  = (int)(h * MAX_TRIM);
        var depths = new int[EDGE_STRIPS];

        using var profile = new Mat();

        for(var k = 0; k < EDGE_STRIPS; k++)
        {
            int x0 = k       * w / EDGE_STRIPS;
            int x1 = (k + 1) * w / EDGE_STRIPS;

            using(Mat strip = along[new Rect(x0, 0, x1 - x0, limit + INSIDE_ROWS + 1)])
                Cv2.Reduce(strip, profile, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

            profile.GetArray(out float[] texture);

            using(Mat strip = crisp[new Rect(x0, 0, x1 - x0, limit + 1)])
                Cv2.Reduce(strip, profile, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

            profile.GetArray(out float[] step);

            depths[k] = FoldInStrip(texture, step, limit, threshold);
        }

        // Median over the strips, so a torn corner or a slight bow does not decide for the whole side
        Array.Sort(depths);

        return depths[EDGE_STRIPS / 2];
    }

    static int FoldInStrip(float[] texture, float[] step, int limit, double threshold)
    {
        // The box starts where print texture begins and continues; thin lines outside it do not last
        double window = 0;

        for(var r = 0; r < INSIDE_ROWS; r++) window += texture[r];

        for(var y = 0; y < limit; y++)
        {
            if(texture[y] > threshold && window / INSIDE_ROWS > threshold) return y;

            window += texture[y + INSIDE_ROWS] - texture[y];
        }

        // No print near this side, as on plain card: the fold is the strongest crisp step with featureless area
        // outside it, allowing for a few thin lines such as the far boundary of a shadow
        var reach    = 0;
        var textured = 0;

        while(reach < limit && textured <= Math.Max(MIN_STRAY_ROWS, STRAY_FRACTION * reach))
        {
            if(texture[reach] > threshold) textured++;

            reach++;
        }

        double strongest = 0;
        var    best      = 0;

        for(var y = 1; y < reach; y++)
        {
            if(step[y] <= strongest) continue;

            strongest = step[y];
            best      = y;
        }

        return best;
    }

    /// <summary>Copies <paramref name="map" /> so the side being examined becomes its top rows.</summary>
    static Mat Orient(Mat map, bool columns, bool fromEnd)
    {
        var oriented = new Mat();

        if(columns)
            Cv2.Transpose(map, oriented);
        else
            map.CopyTo(oriented);

        if(fromEnd) Cv2.Flip(oriented, oriented, FlipMode.X);

        return oriented;
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