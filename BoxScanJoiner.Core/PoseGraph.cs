using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>Result of placing every scan in a common coordinate frame.</summary>
internal sealed class PoseGraphResult
{
    /// <summary>Index of the scan whose frame is used as reference.</summary>
    public required int Reference { get; init; }

    /// <summary>Per-scan transform into the reference frame, or null if the scan could not be connected.</summary>
    public required Similarity?[] Poses { get; init; }

    /// <summary>Order in which scans were attached to the spanning tree, starting with the reference.</summary>
    public required int[] Order { get; init; }

    /// <summary>Root mean square distance between matched points after refinement, in pixels.</summary>
    public required double RmsError { get; init; }
}

internal static class PoseGraph
{
    public static PoseGraphResult Solve(int count, IReadOnlyList<PairMatch> pairs)
    {
        if(count == 0) throw new ArgumentException("No images.", nameof(count));

        // Reference is the best connected scan
        var weight = new long[count];

        foreach(PairMatch p in pairs)
        {
            weight[p.First]  += p.Inliers;
            weight[p.Second] += p.Inliers;
        }

        int reference = Array.IndexOf(weight, weight.Max());

        var poses = new Similarity?[count];
        poses[reference] = Similarity.Identity;
        List<int> order = [reference];

        // Prim's maximum spanning tree, chaining pairwise transforms
        while(true)
        {
            PairMatch? best = null;

            foreach(PairMatch p in pairs)
            {
                if(poses[p.First].HasValue == poses[p.Second].HasValue) continue;

                if(best is null || p.Inliers > best.Inliers) best = p;
            }

            if(best is null) break;

            if(poses[best.First] is {} firstPose)
            {
                poses[best.Second] = firstPose.After(best.SecondToFirst);
                order.Add(best.Second);
            }
            else
            {
                poses[best.First] = poses[best.Second]!.Value.After(best.SecondToFirst.Inverse());
                order.Add(best.First);
            }
        }

        Refine(poses, reference, pairs);

        return new PoseGraphResult
        {
            Reference = reference,
            Poses     = poses,
            Order     = order.ToArray(),
            RmsError  = RmsError(poses, pairs)
        };
    }

    /// <summary>
    ///     Jointly re-estimates all poses from every pairwise correspondence. A similarity is linear in its
    ///     parameters, so this is a single linear least-squares problem with the reference pose held fixed.
    /// </summary>
    static void Refine(Similarity?[] poses, int reference, IReadOnlyList<PairMatch> pairs)
    {
        // Map connected, non-reference scans to unknown blocks of 4 parameters
        var column = new int[poses.Length];
        var n      = 0;

        for(var i = 0; i < poses.Length; i++) column[i] = poses[i].HasValue && i != reference ? n++ * 4 : -1;

        if(n == 0) return;

        // Normalise coordinates so rotation and translation parameters are of similar magnitude
        double norm = pairs.SelectMany(p => p.FirstPoints.Concat(p.SecondPoints))
                           .Select(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)))
                           .DefaultIfEmpty(1)
                           .Max();

        if(norm <= 0) norm = 1;

        int size = n * 4;
        var ata  = new double[size, size];
        var atb  = new double[size];

        Span<int>    cols = stackalloc int[8];
        Span<double> row  = stackalloc double[8];

        foreach(PairMatch pair in pairs)
        {
            if(!poses[pair.First].HasValue || !poses[pair.Second].HasValue) continue;

            int ci = column[pair.First];
            int cj = column[pair.Second];

            for(var k = 0; k < pair.Inliers; k++)
            {
                double xi = pair.FirstPoints[k].X  / norm, yi = pair.FirstPoints[k].Y  / norm;
                double xj = pair.SecondPoints[k].X / norm, yj = pair.SecondPoints[k].Y / norm;

                // pose_i(p_i) - pose_j(p_j) = 0, X and Y components
                AddEquation(ata, atb, cols, row, ci, cj, [xi, -yi, 1, 0], [xj, -yj, 1, 0]);
                AddEquation(ata, atb, cols, row, ci, cj, [yi, xi, 0, 1],  [yj, xj, 0, 1]);
            }
        }

        using var lhs = Mat.FromPixelData(size, size, MatType.CV_64FC1, ata);
        using var rhs = Mat.FromPixelData(size, 1,    MatType.CV_64FC1, atb);
        using var x   = new Mat();

        if(!Cv2.Solve(lhs, rhs, x, DecompTypes.Cholesky) && !Cv2.Solve(lhs, rhs, x, DecompTypes.SVD)) return;

        for(var i = 0; i < poses.Length; i++)
        {
            int c = column[i];

            if(c < 0) continue;

            poses[i] = new Similarity(x.At<double>(c),
                                      x.At<double>(c + 1),
                                      x.At<double>(c + 2) * norm,
                                      x.At<double>(c + 3) * norm);
        }
    }

    /// <summary>
    ///     Accumulates <c>coefI·params_i − coefJ·params_j = 0</c> into the normal equations. Scans without a column
    ///     are the reference (identity), whose contribution moves to the right-hand side.
    /// </summary>
    static void AddEquation(double[,]            ata,   double[] atb, Span<int> cols, Span<double> row, int ci, int cj,
                            ReadOnlySpan<double> coefI, ReadOnlySpan<double> coefJ)
    {
        var    used = 0;
        double rhs  = 0;

        // Identity parameters (a=1, b=0, tx=0, ty=0) give the reference its constant term
        if(ci >= 0)
            for(var k = 0; k < 4; k++)
            {
                cols[used]  = ci + k;
                row[used++] = coefI[k];
            }
        else
            rhs -= coefI[0];

        if(cj >= 0)
            for(var k = 0; k < 4; k++)
            {
                cols[used]  = cj + k;
                row[used++] = -coefJ[k];
            }
        else
            rhs += coefJ[0];

        for(var r = 0; r < used; r++)
        {
            atb[cols[r]] += row[r] * rhs;

            for(var c = 0; c < used; c++) ata[cols[r], cols[c]] += row[r] * row[c];
        }
    }

    static double RmsError(Similarity?[] poses, IReadOnlyList<PairMatch> pairs)
    {
        double sum   = 0;
        long   count = 0;

        foreach(PairMatch pair in pairs)
        {
            if(poses[pair.First] is not {} pi || poses[pair.Second] is not {} pj) continue;

            sum   += SquaredError(pi, pj, pair);
            count += pair.Inliers;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>RMS distance between the pair's matched points when the two scans are placed with these poses.</summary>
    public static double PairRmsError(Similarity firstPose, Similarity secondPose, PairMatch pair) =>
        pair.Inliers == 0 ? 0 : Math.Sqrt(SquaredError(firstPose, secondPose, pair) / pair.Inliers);

    static double SquaredError(Similarity firstPose, Similarity secondPose, PairMatch pair)
    {
        double sum = 0;

        for(var k = 0; k < pair.Inliers; k++)
        {
            Point2d a = firstPose.Apply(pair.FirstPoints[k]);
            Point2d b = secondPose.Apply(pair.SecondPoints[k]);
            sum += (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        }

        return sum;
    }
}