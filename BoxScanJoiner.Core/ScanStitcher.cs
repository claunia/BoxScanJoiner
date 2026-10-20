using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>Thrown when a scan cannot be read.</summary>
public sealed class ImageLoadException(string path) : IOException(path)
{
    public string Path { get; } = path;
}

/// <summary>Joins overlapping flatbed scans of one side of a box into a single image.</summary>
public static class ScanStitcher
{
    /// <summary>Pixels at the edge of each scan that are ignored, hiding interpolation and lid shadow fringes.</summary>
    const int EDGE_MARGIN = 2;

    /// <summary>Longest side of the downscaled debug images.</summary>
    const int DEBUG_MAX_DIMENSION = 4000;

    public static StitchResult Stitch(IReadOnlyList<string>      paths, StitchOptions? options = null,
                                      IProgress<StitchProgress>? progress          = null,
                                      CancellationToken          cancellationToken = default)
    {
        var images = new Mat?[paths.Count];
        var loaded = 0;

        try
        {
            Parallel.For(0,
                         paths.Count,
                         new ParallelOptions
                         {
                             CancellationToken = cancellationToken
                         },
                         i =>
                         {
                             Mat image = Cv2.ImRead(paths[i]);

                             if(image.Empty())
                             {
                                 image.Dispose();

                                 throw new ImageLoadException(paths[i]);
                             }

                             images[i] = image;

                             progress?.Report(new StitchProgress(StitchStage.Loading,
                                                                 Interlocked.Increment(ref loaded),
                                                                 paths.Count));
                         });

            return Stitch(images.Select(i => i!).ToArray(), options, progress, cancellationToken);
        }
        catch(AggregateException e) when(e.InnerExceptions.Count == 1)
        {
            throw e.InnerExceptions[0];
        }
        finally
        {
            foreach(Mat? image in images) image?.Dispose();
        }
    }

    /// <summary>Joins already loaded 8-bit BGR scans. The caller keeps ownership of <paramref name="images" />.</summary>
    public static StitchResult Stitch(IReadOnlyList<Mat>         images, StitchOptions? options = null,
                                      IProgress<StitchProgress>? progress          = null,
                                      CancellationToken          cancellationToken = default)
    {
        if(images.Count == 0) throw new ArgumentException("At least one image is required.", nameof(images));

        options ??= new StitchOptions();

        var features = new ImageFeatures[images.Count];

        try
        {
            DetectFeatures(images, features, options, progress, cancellationToken);

            (List<PairMatch> matches, List<PairInfo> pairInfo) =
                MatchPairs(features, options, progress, cancellationToken);

            progress?.Report(new StitchProgress(StitchStage.Aligning, 0, 1));
            PoseGraphResult graph = PoseGraph.Solve(images.Count, matches);
            progress?.Report(new StitchProgress(StitchStage.Aligning, 1, 1));

            pairInfo = pairInfo.Select(p => Describe(p, matches, graph.Poses)).ToList();

            (Similarity?[] canvasPoses, Size canvasSize) = Frame(images, graph.Poses, options.Crop);

            if(options.DebugDirectory is not null) WriteMatchImages(images, features, matches, options.DebugDirectory);

            List<WarpedImage> warped = Warp(images, canvasPoses, canvasSize, graph.Order, progress, cancellationToken);

            try
            {
                var corrections = new ColorCorrection?[images.Count];

                if(options.Color != ColorMode.None && warped.Count > 1)
                {
                    progress?.Report(new StitchProgress(StitchStage.ColorCorrecting, 0, warped.Count));

                    Dictionary<int, ColorCorrection> fitted = ColorCorrector.Fit(warped, graph.Reference);

                    for(var i = 0; i < warped.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        ColorCorrection correction = fitted[warped[i].Index];
                        corrections[warped[i].Index] = correction;

                        if(warped[i].Index != graph.Reference) ColorCorrector.Apply(warped[i], correction);

                        progress?.Report(new StitchProgress(StitchStage.ColorCorrecting, i + 1, warped.Count));
                    }
                }

                using var compositor = new SeamCompositor(canvasSize);

                for(var i = 0; i < warped.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    compositor.Add(warped[i], options.Seam);
                    progress?.Report(new StitchProgress(StitchStage.Compositing, i + 1, warped.Count));
                }

                if(options.DebugDirectory is not null) WriteSeamImage(compositor, options.DebugDirectory);

                Mat output = Finish(compositor, options.Background);
                progress?.Report(new StitchProgress(StitchStage.Done, 1, 1));

                return new StitchResult
                {
                    Image            = output,
                    Transforms       = canvasPoses,
                    ColorCorrections = corrections,
                    Pairs            = pairInfo,
                    Reference        = graph.Reference,
                    RmsError         = graph.RmsError
                };
            }
            finally
            {
                foreach(WarpedImage w in warped) w.Dispose();
            }
        }
        finally
        {
            foreach(ImageFeatures? f in features) f?.Dispose();
        }
    }

    static void DetectFeatures(IReadOnlyList<Mat>         images,   ImageFeatures[]   features, StitchOptions options,
                               IProgress<StitchProgress>? progress, CancellationToken cancellationToken)
    {
        var done = 0;

        Parallel.For(0,
                     images.Count,
                     new ParallelOptions
                     {
                         CancellationToken = cancellationToken
                     },
                     i =>
                     {
                         features[i] = FeatureMatcher.Detect(images[i], options);

                         progress?.Report(new StitchProgress(StitchStage.Detecting,
                                                             Interlocked.Increment(ref done),
                                                             images.Count));
                     });
    }

    static (List<PairMatch> matches, List<PairInfo> info) MatchPairs(ImageFeatures[] features, StitchOptions options,
                                                                     IProgress<StitchProgress>? progress,
                                                                     CancellationToken cancellationToken)
    {
        List<(int i, int j)> pairs = [];

        for(var i = 0; i < features.Length; i++)
        {
            for(int j = i + 1; j < features.Length; j++) pairs.Add((i, j));
        }

        var results = new PairMatch?[pairs.Count];
        var info    = new PairInfo[pairs.Count];
        var done    = 0;

        Parallel.For(0,
                     pairs.Count,
                     new ParallelOptions
                     {
                         CancellationToken = cancellationToken
                     },
                     k =>
                     {
                         (int i, int j) = pairs[k];

                         results[k] = FeatureMatcher.Match(i,
                                                           features[i],
                                                           j,
                                                           features[j],
                                                           options,
                                                           out int good,
                                                           out double? agreement);

                         info[k] = new PairInfo(i, j, good, results[k]?.Inliers ?? 0)
                         {
                             Agreement = agreement
                         };

                         progress?.Report(new StitchProgress(StitchStage.Matching,
                                                             Interlocked.Increment(ref done),
                                                             pairs.Count));
                     });

        return (results.OfType<PairMatch>().ToList(), info.ToList());
    }

    static PairInfo Describe(PairInfo info, List<PairMatch> matches, Similarity?[] poses)
    {
        PairMatch? match = matches.Find(m => m.First == info.First && m.Second == info.Second);

        if(match is null) return info;

        return info with
        {
            Transform = match.SecondToFirst,
            Scale = match.SecondToFirst.Scale,
            AngleDegrees = match.SecondToFirst.AngleDegrees,
            FitError = PoseGraph.PairRmsError(Similarity.Identity, match.SecondToFirst, match),
            AlignedError = poses[info.First] is {} pi && poses[info.Second] is {} pj
                               ? PoseGraph.PairRmsError(pi, pj, match)
                               : 0
        };
    }

    static Point2d[] Corners(Mat image) =>
    [
        new(0, 0), new(image.Width, 0), new(0, image.Height), new(image.Width, image.Height)
    ];

    /// <summary>Chooses the output frame: the straightened box, or everything scanned.</summary>
    static (Similarity?[] poses, Size size) Frame(IReadOnlyList<Mat> images, Similarity?[] poses, CropMode crop)
    {
        if(crop != CropMode.Box || BoxFinder.Find(images, poses) is not var (frame, size))
            return PlaceOnCanvas(images, poses);

        return (poses.Select(p => p.HasValue ? frame.After(p.Value) : (Similarity?)null).ToArray(), size);
    }

    /// <summary>Shifts every pose so all placed scans land in positive coordinates, and sizes the canvas.</summary>
    static (Similarity?[] poses, Size size) PlaceOnCanvas(IReadOnlyList<Mat> images, Similarity?[] poses)
    {
        List<Point2d> corners = [];

        for(var i = 0; i < images.Count; i++)
        {
            if(poses[i] is {} pose) corners.AddRange(Corners(images[i]).Select(pose.Apply));
        }

        double minX = Math.Floor(corners.Min(p => p.X));
        double minY = Math.Floor(corners.Min(p => p.Y));
        double maxX = Math.Ceiling(corners.Max(p => p.X));
        double maxY = Math.Ceiling(corners.Max(p => p.Y));

        var shift = new Similarity(1, 0, -minX, -minY);

        return (poses.Select(p => p.HasValue ? shift.After(p.Value) : (Similarity?)null).ToArray(),
                new Size(maxX - minX, maxY - minY));
    }

    static List<WarpedImage> Warp(IReadOnlyList<Mat>         images, Similarity?[] poses, Size canvasSize, int[] order,
                                  IProgress<StitchProgress>? progress, CancellationToken cancellationToken)
    {
        List<WarpedImage> warped = [];
        var               canvas = new Rect(0, 0, canvasSize.Width, canvasSize.Height);

        try
        {
            foreach(int i in order)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Similarity pose   = poses[i]!.Value;
                Point2d[]  placed = Corners(images[i]).Select(pose.Apply).ToArray();

                var x0 = (int)Math.Floor(placed.Min(p => p.X));
                var y0 = (int)Math.Floor(placed.Min(p => p.Y));
                var x1 = (int)Math.Ceiling(placed.Max(p => p.X));
                var y1 = (int)Math.Ceiling(placed.Max(p => p.Y));

                Rect roi = Rect.FromLTRB(x0, y0, x1, y1).Intersect(canvas);

                // Entirely cropped away
                if(roi.Width <= 0 || roi.Height <= 0) continue;

                using Mat m = new Similarity(1, 0, -roi.X, -roi.Y).After(pose).ToMat();

                var image = new Mat();

                Cv2.WarpAffine(images[i], image, m, roi.Size, InterpolationFlags.Lanczos4, BorderTypes.Replicate);

                using var source = new Mat(images[i].Size(), MatType.CV_8UC1, Scalar.All(0));

                using(Mat inner = source[new Rect(EDGE_MARGIN,
                                                  EDGE_MARGIN,
                                                  Math.Max(0, images[i].Width  - 2 * EDGE_MARGIN),
                                                  Math.Max(0, images[i].Height - 2 * EDGE_MARGIN))])
                    inner.SetTo(Scalar.All(255));

                var mask = new Mat();
                Cv2.WarpAffine(source, mask, m, roi.Size, InterpolationFlags.Nearest);

                warped.Add(new WarpedImage
                {
                    Index = i,
                    Image = image,
                    Mask  = mask,
                    Roi   = roi
                });

                progress?.Report(new StitchProgress(StitchStage.Warping, warped.Count, order.Length));
            }
        }
        catch
        {
            foreach(WarpedImage w in warped) w.Dispose();

            throw;
        }

        return warped;
    }

    static Mat Finish(SeamCompositor compositor, BackgroundFill background)
    {
        var output = new Mat();

        if(background == BackgroundFill.Transparent)
        {
            Cv2.CvtColor(compositor.Canvas, output, ColorConversionCodes.BGR2BGRA);
            Cv2.InsertChannel(compositor.Mask, output, 3);

            return output;
        }

        compositor.Canvas.CopyTo(output);

        using var empty = new Mat();
        Cv2.BitwiseNot(compositor.Mask, empty);
        output.SetTo(background == BackgroundFill.White ? Scalar.All(255) : Scalar.All(0), empty);

        return output;
    }

    static void WriteMatchImages(IReadOnlyList<Mat> images, ImageFeatures[] features, List<PairMatch> matches,
                                 string             directory)
    {
        Directory.CreateDirectory(directory);

        foreach(PairMatch match in matches)
        {
            using Mat a   = Downscale(images[match.First],  features[match.First].Scale);
            using Mat b   = Downscale(images[match.Second], features[match.Second].Scale);
            using var vis = new Mat();

            // Matches were computed with the second image as query
            DMatch[] swapped = match.InlierMatches.Select(m => new DMatch(m.TrainIdx, m.QueryIdx, m.Distance))
                                    .ToArray();

            Cv2.DrawMatches(a,
                            features[match.First].KeyPoints,
                            b,
                            features[match.Second].KeyPoints,
                            swapped,
                            vis,
                            flags: DrawMatchesFlags.NotDrawSinglePoints);

            Cv2.ImWrite(System.IO.Path.Combine(directory, $"matches_{match.First}_{match.Second}.jpg"), vis);
        }
    }

    static void WriteSeamImage(SeamCompositor compositor, string directory)
    {
        Directory.CreateDirectory(directory);

        using var vis      = compositor.Canvas.Clone();
        using var dilated  = new Mat();
        using var eroded   = new Mat();
        using var boundary = new Mat();
        using Mat kernel   = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

        Cv2.Dilate(compositor.Labels, dilated, kernel);
        Cv2.Erode(compositor.Labels, eroded, kernel);
        Cv2.Compare(dilated, eroded, boundary, CmpTypes.NE);
        Cv2.Dilate(boundary, boundary, kernel, iterations: 2);
        vis.SetTo(new Scalar(0, 0, 255), boundary);

        double scale = Math.Min(1.0, (double)DEBUG_MAX_DIMENSION / Math.Max(vis.Width, vis.Height));

        using Mat small = Downscale(vis, scale);
        Cv2.ImWrite(System.IO.Path.Combine(directory, "seams.jpg"), small);
    }

    static Mat Downscale(Mat image, double scale)
    {
        var result = new Mat();

        if(scale < 1)
            Cv2.Resize(image, result, new Size(), scale, scale, InterpolationFlags.Area);
        else
            image.CopyTo(result);

        return result;
    }
}