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

/// <summary>
///     Pastes warped scans onto a canvas. Each overlap is resolved with a graph cut that keeps every pixel from a single
///     scan, routes the cut where the scans agree and prefers whichever scan is in sharper focus.
/// </summary>
internal sealed class SeamCompositor : IDisposable
{
    /// <summary>Longest side of the grid the cut is solved on; overlaps are averaged down to this size.</summary>
    const int GRID_SIZE = 1024;

    /// <summary>Weight of image gradients in the seam cost, so cuts avoid edges and text.</summary>
    const double GRADIENT_WEIGHT = 0.5;

    /// <summary>Cost per pixel of keeping the blurrier scan, scaled by how much blurrier it is.</summary>
    const double FOCUS_WEIGHT = 10;

    /// <summary>Relative sharpness difference below which both scans are considered equally sharp.</summary>
    const double FOCUS_TOLERANCE = 0.25;

    /// <summary>Radius, in full-resolution pixels, over which sharpness is compared; focus changes slowly.</summary>
    const double FOCUS_RADIUS = 48;

    /// <summary>Rows processed at a time when reducing full-resolution maps to the grid.</summary>
    const int STRIP_CELLS = 128;

    const long HARD = 1L << 40;

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

            Rect bounds = Cv2.BoundingRect(overlap);

            if(bounds.Width > 0 && bounds.Height > 0)
            {
                int cell = Math.Max(1, (int)Math.Ceiling((double)Math.Max(bounds.Width, bounds.Height) / GRID_SIZE));

                // Include a margin so the cells holding only one scan, which anchor the cut, are part of the grid
                Rect r = new Rect(bounds.X      - 2 * cell,
                                  bounds.Y      - 2 * cell,
                                  bounds.Width  + 4 * cell,
                                  bounds.Height + 4 * cell)
                   .Intersect(new Rect(0, 0, image.Roi.Width, image.Roi.Height));

                using Mat oldR     = canvasRoi[r];
                using Mat newR     = image.Image[r];
                using Mat oldMaskR = maskRoi[r];
                using Mat newMaskR = image.Mask[r];
                using Mat overlapR = overlap[r];
                using Mat seamNew  = FindSeam(oldR, newR, oldMaskR, newMaskR, overlapR, cell);
                using Mat takeR    = take[r];

                Cv2.BitwiseAnd(seamNew, overlapR, seamNew);
                Cv2.BitwiseOr(takeR, seamNew, takeR);
            }
        }

        image.Image.CopyTo(canvasRoi, take);
        labelRoi.SetTo(Scalar.All(image.Index + 1), take);
        Cv2.BitwiseOr(maskRoi, image.Mask, maskRoi);
    }

    /// <summary>Per-cell maps the cut is computed from.</summary>
    sealed class Grid : IDisposable
    {
        public required Mat SeamCost  { get; init; }
        public required Mat OldCover  { get; init; }
        public required Mat NewCover  { get; init; }
        public required Mat OldDetail { get; init; }
        public required Mat NewDetail { get; init; }

        public void Dispose()
        {
            SeamCost.Dispose();
            OldCover.Dispose();
            NewCover.Dispose();
            OldDetail.Dispose();
            NewDetail.Dispose();
        }
    }

    /// <summary>Returns a mask, the size of <paramref name="oldImg" />, that is 255 where the new image should be used.</summary>
    static Mat FindSeam(Mat oldImg, Mat newImg, Mat oldMask, Mat newMask, Mat overlap, int cell)
    {
        using Grid grid = BuildGrid(oldImg, newImg, oldMask, newMask, overlap, cell);

        int w = grid.SeamCost.Cols;
        int h = grid.SeamCost.Rows;

        // Halftone and moiré make per-cell sharpness noisy; compare it over a wider area
        double sigma = Math.Max(1, FOCUS_RADIUS / cell);
        Cv2.GaussianBlur(grid.OldDetail, grid.OldDetail, new Size(0, 0), sigma);
        Cv2.GaussianBlur(grid.NewDetail, grid.NewDetail, new Size(0, 0), sigma);

        grid.SeamCost.GetArray(out float[] cost);
        grid.OldCover.GetArray(out float[] oldCover);
        grid.NewCover.GetArray(out float[] newCover);
        grid.OldDetail.GetArray(out float[] oldDetail);
        grid.NewDetail.GetArray(out float[] newDetail);

        var node  = new int[w * h];
        var nodes = 0;

        // The cut only runs through the overlap; of the cells holding a single scan, only those bordering the
        // overlap are needed to anchor it
        for(var y = 0; y < h; y++)
        {
            for(var x = 0; x < w; x++)
            {
                int k = y * w + x;

                bool used = IsOverlap(oldCover, newCover, k) ||
                            (oldCover[k] > 0.5f || newCover[k] > 0.5f) &&
                            (x     > 0 && IsOverlap(oldCover, newCover, k - 1) ||
                             x + 1 < w && IsOverlap(oldCover, newCover, k + 1) ||
                             y     > 0 && IsOverlap(oldCover, newCover, k - w) ||
                             y + 1 < h && IsOverlap(oldCover, newCover, k + w));

                node[k] = used ? nodes++ : -1;
            }
        }

        var graph = new GraphCut(nodes, nodes * 2);

        // Edges are cut once per cell of seam length, terminals once per cell of area
        double edgeScale = cell;
        double areaScale = (double)cell * cell;

        for(var k = 0; k < node.Length; k++)
        {
            if(node[k] < 0) continue;

            bool hasOld = oldCover[k] > 0.5f;
            bool hasNew = newCover[k] > 0.5f;

            // Source side keeps the old canvas, sink side takes the new scan
            if(hasOld && !hasNew)
                graph.AddTerminal(node[k], HARD, 0);
            else if(hasNew && !hasOld)
                graph.AddTerminal(node[k], 0, HARD);
            else
            {
                float  sum   = oldDetail[k] + newDetail[k];
                double focus = sum > 0 ? (newDetail[k] - oldDetail[k]) / sum : 0;

                // Taking the new scan costs when it is blurrier, keeping the old one when that is blurrier
                long keepOld = (long)(FOCUS_WEIGHT * areaScale * Math.Max(0, focus  - FOCUS_TOLERANCE));
                long takeNew = (long)(FOCUS_WEIGHT * areaScale * Math.Max(0, -focus - FOCUS_TOLERANCE));
                graph.AddTerminal(node[k], takeNew, keepOld);
            }
        }

        for(var y = 0; y < h; y++)
        {
            for(var x = 0; x < w; x++)
            {
                int k = y * w + x;

                if(node[k] < 0) continue;

                if(x + 1 < w) Connect(graph, node, cost, oldCover, newCover, k, k + 1, edgeScale);

                if(y + 1 < h) Connect(graph, node, cost, oldCover, newCover, k, k + w, edgeScale);
            }
        }

        graph.MaxFlow();

        var labels = new byte[w * h];

        for(var k = 0; k < node.Length; k++)
        {
            if(node[k] >= 0 && graph.IsSink(node[k])) labels[k] = 255;
        }

        using Mat small  = Mat.FromPixelData(h, w, MatType.CV_8UC1, labels);
        var       result = new Mat();
        Cv2.Resize(small, result, oldImg.Size(), 0, 0, InterpolationFlags.Nearest);

        return result;
    }

    static bool IsOverlap(float[] oldCover, float[] newCover, int k) => oldCover[k] > 0.5f && newCover[k] > 0.5f;

    static void Connect(GraphCut graph, int[] node, float[] cost, float[] oldCover, float[] newCover, int a, int b,
                        double   scale)
    {
        if(node[b] < 0) return;

        bool overlapA = IsOverlap(oldCover, newCover, a);
        bool overlapB = IsOverlap(oldCover, newCover, b);

        // Two cells that each hold only one scan are fixed anyway
        if(!overlapA && !overlapB) return;

        double c = overlapA && overlapB
                       ? (cost[a] + cost[b]) / 2
                       : overlapA
                           ? cost[a]
                           : cost[b];

        var w = (long)Math.Ceiling(c * scale);
        graph.AddEdge(node[a], node[b], w, w);
    }

    /// <summary>Reduces full-resolution seam cost, coverage and detail maps to the cut grid, a strip at a time.</summary>
    static Grid BuildGrid(Mat oldImg, Mat newImg, Mat oldMask, Mat newMask, Mat overlap, int cell)
    {
        int gw = (oldImg.Cols + cell - 1) / cell;
        int gh = (oldImg.Rows + cell - 1) / cell;

        var grid = new Grid
        {
            SeamCost  = new Mat(gh, gw, MatType.CV_32FC1, Scalar.All(0)),
            OldCover  = new Mat(gh, gw, MatType.CV_32FC1, Scalar.All(0)),
            NewCover  = new Mat(gh, gw, MatType.CV_32FC1, Scalar.All(0)),
            OldDetail = new Mat(gh, gw, MatType.CV_32FC1, Scalar.All(0)),
            NewDetail = new Mat(gh, gw, MatType.CV_32FC1, Scalar.All(0))
        };

        for(var gy = 0; gy < gh; gy += STRIP_CELLS)
        {
            int cells = Math.Min(STRIP_CELLS, gh - gy);
            int y0    = gy * cell;
            int rows  = Math.Min(cells * cell, oldImg.Rows - y0);
            var strip = new Rect(0, y0, oldImg.Cols, rows);
            var area  = new Rect(0, gy, gw,          cells);

            using Mat o  = oldImg[strip];
            using Mat n  = newImg[strip];
            using Mat om = oldMask[strip];
            using Mat nm = newMask[strip];
            using Mat ov = overlap[strip];

            using Mat oGray = Gray(o);
            using Mat nGray = Gray(n);

            using(Mat cost = SeamCost(o, n, oGray, nGray, ov))
                using(Mat ovF = ToFloat(ov))
                {
                    // Average cost over the overlapping part of each cell only
                    using Mat costSum = Reduce(cost, area.Size);
                    using Mat ovSum   = Reduce(ovF,  area.Size);
                    using Mat target  = grid.SeamCost[area];
                    Cv2.Add(ovSum, Scalar.All(1e-6), ovSum);
                    Cv2.Divide(costSum, ovSum, target);
                }

            ReduceCoverage(om, grid.OldCover[area]);
            ReduceCoverage(nm, grid.NewCover[area]);
            ReduceDetail(oGray, om, grid.OldDetail[area]);
            ReduceDetail(nGray, nm, grid.NewDetail[area]);
        }

        return grid;
    }

    static Mat Gray(Mat bgr)
    {
        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        return gray;
    }

    static Mat ToFloat(Mat mask)
    {
        var f = new Mat();
        mask.ConvertTo(f, MatType.CV_32FC1, 1.0 / 255);

        return f;
    }

    static Mat Reduce(Mat image, Size size)
    {
        var result = new Mat();
        Cv2.Resize(image, result, size, 0, 0, InterpolationFlags.Area);

        return result;
    }

    static void ReduceCoverage(Mat mask, Mat target)
    {
        using(target)
            using(Mat f = ToFloat(mask))
                using(Mat r = Reduce(f, target.Size()))
                    r.CopyTo(target);
    }

    /// <summary>Mean absolute Laplacian over each cell's valid pixels, a measure of how sharp the scan is there.</summary>
    static void ReduceDetail(Mat gray, Mat mask, Mat target)
    {
        using(target)
        {
            using var lap    = new Mat();
            using var absLap = new Mat();
            Cv2.Laplacian(gray, lap, MatType.CV_16S);
            Cv2.ConvertScaleAbs(lap, absLap);
            using var outside = new Mat();
            Cv2.BitwiseNot(mask, outside);
            absLap.SetTo(Scalar.All(0), outside);

            using Mat f     = ToFloat(absLap);
            using Mat cover = ToFloat(mask);
            using Mat sum   = Reduce(f,     target.Size());
            using Mat count = Reduce(cover, target.Size());
            Cv2.Add(count, Scalar.All(1e-6), count);
            Cv2.Divide(sum, count, target, 255);
        }
    }

    /// <summary>Per-pixel cost of cutting between the two scans, zero outside the overlap.</summary>
    static Mat SeamCost(Mat oldImg, Mat newImg, Mat oldGray, Mat newGray, Mat overlap)
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

        using Mat gradOld = GradientMagnitude(oldGray);
        using Mat gradNew = GradientMagnitude(newGray);
        Cv2.ScaleAdd(gradOld, GRADIENT_WEIGHT, cost, cost);
        Cv2.ScaleAdd(gradNew, GRADIENT_WEIGHT, cost, cost);

        // Small constant so the shortest cut is preferred in perfectly flat areas
        Cv2.Add(cost, Scalar.All(1), cost);

        using var outside = new Mat();
        Cv2.BitwiseNot(overlap, outside);
        cost.SetTo(Scalar.All(0), outside);

        return cost;
    }

    static Mat GradientMagnitude(Mat gray)
    {
        using var dx  = new Mat();
        using var dy  = new Mat();
        var       mag = new Mat();

        Cv2.Sobel(gray, dx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(gray, dy, MatType.CV_32F, 0, 1);
        Cv2.Magnitude(dx, dy, mag);

        return mag;
    }
}