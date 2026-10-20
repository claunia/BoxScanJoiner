using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>A scan warped into the output frame, cropped to the area it covers.</summary>
internal sealed class WarpedImage : IDisposable
{
    public required int Index { get; init; }

    /// <summary>BGR pixels covering <see cref="Roi" />.</summary>
    public required Mat Image { get; init; }

    /// <summary>255 where <see cref="Image" /> holds valid scan data.</summary>
    public required Mat Mask { get; init; }

    /// <summary>Area of the output canvas covered by this image.</summary>
    public required Rect Roi { get; init; }

    public void Dispose()
    {
        Image.Dispose();
        Mask.Dispose();
    }
}

/// <summary>Pastes warped scans onto a canvas, cutting each overlap along a minimum-difference seam.</summary>
internal sealed class SeamCompositor : IDisposable
{
    /// <summary>Cost of routing the seam through pixels that are not covered by both images.</summary>
    const float OUTSIDE_OVERLAP_COST = 100000;

    /// <summary>Weight of image gradients in the seam cost, so cuts avoid edges and text.</summary>
    const double GRADIENT_WEIGHT = 0.5;

    public SeamCompositor(Size size)
    {
        Canvas = new Mat(size, MatType.CV_8UC3, Scalar.All(0));
        Mask   = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        Labels = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
    }

    public Mat Canvas { get; }

    /// <summary>255 where the canvas holds scan data.</summary>
    public Mat Mask { get; }

    /// <summary>Index + 1 of the scan each canvas pixel came from, 0 where empty.</summary>
    public Mat Labels { get; }

    public void Dispose()
    {
        Canvas.Dispose();
        Mask.Dispose();
        Labels.Dispose();
    }

    public void Add(WarpedImage image, SeamMode mode)
    {
        using Mat canvasRoi = Canvas[image.Roi];
        using Mat maskRoi   = Mask[image.Roi];
        using Mat labelRoi  = Labels[image.Roi];
        using var take      = new Mat();

        if(mode == SeamMode.None)
            image.Mask.CopyTo(take);
        else
        {
            using var overlap = new Mat();
            using var notOld  = new Mat();
            Cv2.BitwiseAnd(maskRoi, image.Mask, overlap);
            Cv2.BitwiseNot(maskRoi, notOld);
            Cv2.BitwiseAnd(image.Mask, notOld, take);

            Rect r = Cv2.BoundingRect(overlap);

            if(r.Width > 0 && r.Height > 0)
            {
                Point2d direction = NewImageDirection(maskRoi, image.Mask, take);

                using Mat oldR     = canvasRoi[r];
                using Mat newR     = image.Image[r];
                using Mat overlapR = overlap[r];
                using Mat seamNew  = FindSeam(oldR, newR, overlapR, direction);
                using Mat takeR    = take[r];

                Cv2.BitwiseAnd(seamNew, overlapR, seamNew);
                Cv2.BitwiseOr(takeR, seamNew, takeR);
            }
        }

        image.Image.CopyTo(canvasRoi, take);
        labelRoi.SetTo(Scalar.All(image.Index + 1), take);
        Cv2.BitwiseOr(maskRoi, image.Mask, maskRoi);
    }

    /// <summary>Approximate direction from the existing canvas content to the new image.</summary>
    static Point2d NewImageDirection(Mat oldMask, Mat newMask, Mat newOnly)
    {
        using var oldOnly = new Mat();
        using var notNew  = new Mat();
        Cv2.BitwiseNot(newMask, notNew);
        Cv2.BitwiseAnd(oldMask, notNew, oldOnly);

        Point2d? from = Centroid(oldOnly) ?? Centroid(oldMask);
        Point2d? to   = Centroid(newOnly) ?? Centroid(newMask);

        if(from is null || to is null) return new Point2d(1, 0);

        Point2d d = to.Value - from.Value;

        return d.X == 0 && d.Y == 0 ? new Point2d(1, 0) : d;
    }

    static Point2d? Centroid(Mat mask)
    {
        Moments m = Cv2.Moments(mask, true);

        return m.M00 > 0 ? new Point2d(m.M10 / m.M00, m.M01 / m.M00) : null;
    }

    /// <summary>
    ///     Finds a minimum-cost path across the overlap, perpendicular to <paramref name="direction" />, and returns a
    ///     mask that is 255 on the side of the path facing the new image.
    /// </summary>
    static Mat FindSeam(Mat oldImg, Mat newImg, Mat overlap, Point2d direction)
    {
        using Mat cost = SeamCost(oldImg, newImg, overlap);

        // Normalise so the seam runs top to bottom with the new image on the right
        bool transpose = Math.Abs(direction.Y)                   > Math.Abs(direction.X);
        bool flip      = (transpose ? direction.Y : direction.X) < 0;

        if(transpose) Cv2.Transpose(cost, cost);

        if(flip) Cv2.Flip(cost, cost, FlipMode.Y);

        int[] seam   = MinimumPath(cost);
        int   width  = cost.Cols;
        var   pixels = new byte[seam.Length * width];

        for(var y = 0; y < seam.Length; y++) pixels.AsSpan(y * width + seam[y], width - seam[y]).Fill(255);

        Mat labels = Mat.FromPixelData(seam.Length, width, MatType.CV_8UC1, pixels).Clone();

        if(flip) Cv2.Flip(labels, labels, FlipMode.Y);

        if(transpose) Cv2.Transpose(labels, labels);

        return labels;
    }

    static Mat SeamCost(Mat oldImg, Mat newImg, Mat overlap)
    {
        using var diff   = new Mat();
        using var diff32 = new Mat();
        var       cost   = new Mat();

        Cv2.Absdiff(oldImg, newImg, diff);
        diff.ConvertTo(diff32, MatType.CV_32FC3);

        using(var sumKernel = Mat.FromPixelData(1,
                                                3,
                                                MatType.CV_32FC1,
                                                new float[]
                                                {
                                                    1, 1, 1
                                                }))
            Cv2.Transform(diff32, cost, sumKernel);

        using Mat gradOld = GradientMagnitude(oldImg);
        using Mat gradNew = GradientMagnitude(newImg);
        Cv2.ScaleAdd(gradOld, GRADIENT_WEIGHT, cost, cost);
        Cv2.ScaleAdd(gradNew, GRADIENT_WEIGHT, cost, cost);

        // Small constant so the shortest path is preferred in perfectly flat areas
        Cv2.Add(cost, Scalar.All(1), cost);

        using var outside = new Mat();
        Cv2.BitwiseNot(overlap, outside);
        cost.SetTo(Scalar.All(OUTSIDE_OVERLAP_COST), outside);

        return cost;
    }

    static Mat GradientMagnitude(Mat bgr)
    {
        using var gray = new Mat();
        using var dx   = new Mat();
        using var dy   = new Mat();
        var       mag  = new Mat();

        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Sobel(gray, dx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(gray, dy, MatType.CV_32F, 0, 1);
        Cv2.Magnitude(dx, dy, mag);

        return mag;
    }

    /// <summary>Dynamic-programming minimum 8-connected top-to-bottom path; returns its column on every row.</summary>
    static int[] MinimumPath(Mat cost)
    {
        int h = cost.Rows;
        int w = cost.Cols;

        cost.GetArray(out float[] c);

        var acc  = new double[w];
        var next = new double[w];
        var back = new sbyte[h * w];

        for(var x = 0; x < w; x++) acc[x] = c[x];

        for(var y = 1; y < h; y++)
        {
            int rowOffset = y * w;

            for(var x = 0; x < w; x++)
            {
                double best = acc[x];
                sbyte  step = 0;

                if(x > 0 && acc[x - 1] < best)
                {
                    best = acc[x - 1];
                    step = -1;
                }

                if(x < w - 1 && acc[x + 1] < best)
                {
                    best = acc[x + 1];
                    step = 1;
                }

                next[x]             = best + c[rowOffset + x];
                back[rowOffset + x] = step;
            }

            (acc, next) = (next, acc);
        }

        var seam = new int[h];
        var end  = 0;

        for(var x = 1; x < w; x++)
        {
            if(acc[x] < acc[end]) end = x;
        }

        seam[h - 1] = end;

        for(int y = h - 1; y > 0; y--) seam[y - 1] = seam[y] + back[y * w + seam[y]];

        return seam;
    }
}