using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace BoxScanJoiner.Core;

/// <summary>Keypoints and descriptors of one scan, detected on a downscaled copy.</summary>
internal sealed class ImageFeatures : IDisposable
{
    public required KeyPoint[] KeyPoints   { get; init; }
    public required Mat        Descriptors { get; init; }

    /// <summary>Detection scale relative to the full-resolution image (≤ 1).</summary>
    public required double Scale { get; init; }

    /// <summary>Small blurred grayscale copy used to verify overlaps.</summary>
    public required Mat Preview { get; init; }

    /// <summary>Scale of <see cref="Preview" /> relative to the full-resolution image.</summary>
    public required double PreviewScale { get; init; }

    public void Dispose()
    {
        Descriptors.Dispose();
        Preview.Dispose();
    }
}

/// <summary>Verified correspondence between two scans.</summary>
internal sealed class PairMatch
{
    public required int First  { get; init; }
    public required int Second { get; init; }

    /// <summary>Maps full-resolution coordinates of <see cref="Second" /> into <see cref="First" />.</summary>
    public required Similarity SecondToFirst { get; init; }

    public required int Matches { get; init; }

    /// <summary>Inlier points in full-resolution coordinates of the first image.</summary>
    public required Point2d[] FirstPoints { get; init; }

    /// <summary>Inlier points in full-resolution coordinates of the second image.</summary>
    public required Point2d[] SecondPoints { get; init; }

    /// <summary>Inlier matches between detection-scale keypoints, for debug drawing.</summary>
    public required DMatch[] InlierMatches { get; init; }

    public int Inliers => FirstPoints.Length;
}

internal static class FeatureMatcher
{
    public static ImageFeatures Detect(Mat image, StitchOptions options)
    {
        double scale = Math.Min(1.0, (double)options.MaxDetectionDimension / Math.Max(image.Width, image.Height));

        using var gray = new Mat();

        if(image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image,
                         gray,
                         image.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);

        using var small = new Mat();

        if(scale < 1)
            Cv2.Resize(gray, small, new Size(), scale, scale, InterpolationFlags.Area);
        else
            gray.CopyTo(small);

        using Feature2D detector    = CreateDetector(options);
        var             descriptors = new Mat();
        detector.DetectAndCompute(small, null, out KeyPoint[] keyPoints, descriptors);

        (Mat preview, double previewScale) = OverlapCheck.Preview(small);

        return new ImageFeatures
        {
            KeyPoints    = keyPoints,
            Descriptors  = descriptors,
            Scale        = scale,
            Preview      = preview,
            PreviewScale = previewScale * scale
        };
    }

    /// <summary>Matches two scans and estimates the similarity between them, or returns null if they do not overlap.</summary>
    public static PairMatch? Match(int     first, ImageFeatures a, int second, ImageFeatures b, StitchOptions options,
                                   out int goodMatches, out double? agreement)
    {
        goodMatches = 0;
        agreement   = null;

        if(a.KeyPoints.Length < 2 || b.KeyPoints.Length < 2) return null;

        using DescriptorMatcher matcher = CreateMatcher(options);

        // Query with B, train with A, so each DMatch has QueryIdx in B and TrainIdx in A
        DMatch[][] knn = matcher.KnnMatch(b.Descriptors, a.Descriptors, 2);

        List<DMatch> good = [];

        foreach(DMatch[] m in knn)
        {
            if(m.Length < 2) continue;

            if(m[0].Distance < options.RatioThreshold * m[1].Distance) good.Add(m[0]);
        }

        goodMatches = good.Count;

        if(good.Count < options.MinInliers) return null;

        Point2d[] ptsA = good.Select(m => ToFullRes(a.KeyPoints[m.TrainIdx].Pt, a.Scale)).ToArray();
        Point2d[] ptsB = good.Select(m => ToFullRes(b.KeyPoints[m.QueryIdx].Pt, b.Scale)).ToArray();

        using var from    = InputArray.Create(ptsB);
        using var to      = InputArray.Create(ptsA);
        using var inliers = new Mat();

        // Threshold is given at detection scale; points are full-resolution
        double threshold = options.RansacThreshold / Math.Min(a.Scale, b.Scale);

        using Mat? model = Cv2.EstimateAffinePartial2D(from,
                                                       to,
                                                       inliers,
                                                       RobustEstimationAlgorithms.RANSAC,
                                                       threshold,
                                                       5000,
                                                       0.999);

        if(model is null || model.Empty()) return null;

        Similarity transform = Similarity.FromMat(model);

        if(Math.Abs(transform.Scale - 1) > options.MaxScaleDeviation) return null;

        List<Point2d> inA       = [];
        List<Point2d> inB       = [];
        List<DMatch>  inMatches = [];

        for(var i = 0; i < good.Count; i++)
        {
            if(inliers.At<byte>(i) == 0) continue;

            inA.Add(ptsA[i]);
            inB.Add(ptsB[i]);
            inMatches.Add(good[i]);
        }

        if(inA.Count < options.MinInliers) return null;

        agreement = OverlapCheck.Agreement(a.Preview, a.PreviewScale, b.Preview, b.PreviewScale, transform);

        if(agreement < options.MinOverlapAgreement) return null;

        return new PairMatch
        {
            First         = first,
            Second        = second,
            SecondToFirst = transform,
            Matches       = good.Count,
            FirstPoints   = inA.ToArray(),
            SecondPoints  = inB.ToArray(),
            InlierMatches = inMatches.ToArray()
        };
    }

    static Point2d ToFullRes(Point2f p, double scale) => new(p.X / scale, p.Y / scale);

    static Feature2D CreateDetector(StitchOptions options) => options.Detector switch
                                                              {
                                                                  FeatureDetectorKind.Akaze => AKAZE.Create(),
                                                                  FeatureDetectorKind.Orb =>
                                                                      ORB.Create(options.MaxFeatures),
                                                                  _ => SIFT.Create(options.MaxFeatures)
                                                              };

    static DescriptorMatcher CreateMatcher(StitchOptions options) => options.Detector == FeatureDetectorKind.Sift
                                                                         ? new FlannBasedMatcher()
                                                                         : new BFMatcher(NormTypes.Hamming);
}