using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>Outcome of matching two scans.</summary>
/// <param name="First">Index of the first scan.</param>
/// <param name="Second">Index of the second scan.</param>
/// <param name="Matches">Descriptor matches passing the ratio test.</param>
/// <param name="Inliers">Matches consistent with the estimated transform, 0 if rejected.</param>
public sealed record PairInfo(int First, int Second, int Matches, int Inliers)
{
    public bool Accepted => Inliers > 0;
}

public sealed class StitchResult : IDisposable
{
    /// <summary>Joined image, BGR or BGRA when the background is transparent.</summary>
    public required Mat Image { get; init; }

    /// <summary>Per-scan transform into <see cref="Image" />, null for scans that could not be placed.</summary>
    public required IReadOnlyList<Similarity?> Transforms { get; init; }

    public required IReadOnlyList<PairInfo> Pairs { get; init; }

    /// <summary>Index of the scan whose orientation the output follows.</summary>
    public required int Reference { get; init; }

    /// <summary>Root mean square alignment error between matched points, in pixels.</summary>
    public required double RmsError { get; init; }

    /// <summary>Indices of scans that share no overlap with the rest and were left out.</summary>
    public IEnumerable<int> UnconnectedImages =>
        Transforms.Select((t, i) => (t, i)).Where(x => x.t is null).Select(x => x.i);

    public int PlacedImages => Transforms.Count(t => t.HasValue);

    public void Dispose() => Image.Dispose();
}

public enum StitchStage
{
    Loading,
    Detecting,
    Matching,
    Aligning,
    Warping,
    ColorCorrecting,
    Compositing,
    Done
}

/// <summary>Progress report; <see cref="Current" /> of <see cref="Total" /> steps of <see cref="Stage" />.</summary>
public readonly record struct StitchProgress(StitchStage Stage, int Current, int Total);