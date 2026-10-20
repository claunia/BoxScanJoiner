namespace BoxScanJoiner.Core;

public enum FeatureDetectorKind
{
    Sift,
    Akaze,
    Orb
}

public enum SeamMode
{
    /// <summary>Hard cut along a minimum-cost path through each overlap.</summary>
    OptimalCut,
    /// <summary>Later images simply overwrite earlier ones.</summary>
    None
}

public enum ColorMode
{
    /// <summary>Per-channel gain and offset fitted so overlaps agree.</summary>
    GainOffset,
    None
}

public enum CropMode
{
    /// <summary>Crop to the box and straighten it, leaving out the scanner lid.</summary>
    Box,
    /// <summary>Keep everything that was scanned.</summary>
    None
}

public enum BackgroundFill
{
    White,
    Black,
    Transparent
}

public sealed record StitchOptions
{
    public FeatureDetectorKind Detector { get; init; } = FeatureDetectorKind.Sift;

    /// <summary>Images are downscaled so their longest side is at most this many pixels for feature detection.</summary>
    public int MaxDetectionDimension { get; init; } = 2000;

    /// <summary>Maximum number of features per image.</summary>
    public int MaxFeatures { get; init; } = 8000;

    /// <summary>Lowe's ratio test threshold.</summary>
    public double RatioThreshold { get; init; } = 0.75;

    /// <summary>Minimum RANSAC inliers for two scans to be considered overlapping.</summary>
    public int MinInliers { get; init; } = 30;

    /// <summary>RANSAC reprojection threshold, in pixels at detection scale.</summary>
    public double RansacThreshold { get; init; } = 3;

    /// <summary>Maximum deviation from scale 1 allowed between two scans.</summary>
    public double MaxScaleDeviation { get; init; } = 0.01;

    /// <summary>
    ///     Minimum fraction of the overlap where two aligned scans must look alike, rejecting matches caused by the
    ///     same artwork printed on different sides of a box.
    /// </summary>
    public double MinOverlapAgreement { get; init; } = 0.7;

    public SeamMode Seam { get; init; } = SeamMode.OptimalCut;

    public ColorMode Color { get; init; } = ColorMode.GainOffset;

    public BackgroundFill Background { get; init; } = BackgroundFill.White;

    public CropMode Crop { get; init; } = CropMode.Box;

    /// <summary>When set, diagnostic images are written to this directory.</summary>
    public string? DebugDirectory { get; init; }
}