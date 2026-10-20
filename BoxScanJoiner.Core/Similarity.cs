using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>
///     2D similarity transform (rotation, uniform scale and translation):
///     <c>x' = A·x − B·y + Tx</c>, <c>y' = B·x + A·y + Ty</c>.
/// </summary>
public readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    public static Similarity Identity => new(1, 0, 0, 0);

    /// <summary>Uniform scale factor.</summary>
    public double Scale => Math.Sqrt(A * A + B * B);

    /// <summary>Rotation angle in degrees.</summary>
    public double AngleDegrees => Math.Atan2(B, A) * 180 / Math.PI;

    public Point2d Apply(Point2d p) => new(A * p.X - B * p.Y + Tx, B * p.X + A * p.Y + Ty);

    /// <summary>Returns the transform that applies <paramref name="first" /> and then this one.</summary>
    public Similarity After(Similarity first) => new(A * first.A                 - B * first.B,
                                                     B * first.A                 + A * first.B,
                                                     A * first.Tx - B * first.Ty + Tx,
                                                     B * first.Tx                + A * first.Ty + Ty);

    public Similarity Inverse()
    {
        double d  = A * A + B * B;
        double ia = A  / d;
        double ib = -B / d;

        return new Similarity(ia, ib, -(ia * Tx - ib * Ty), -(ib * Tx + ia * Ty));
    }

    /// <summary>Converts to a 2x3 affine matrix usable by <see cref="Cv2.WarpAffine" />.</summary>
    public Mat ToMat()
    {
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, A);
        m.Set(0, 1, -B);
        m.Set(0, 2, Tx);
        m.Set(1, 0, B);
        m.Set(1, 1, A);
        m.Set(1, 2, Ty);

        return m;
    }

    /// <summary>Reads a 2x3 partial affine matrix as returned by <see cref="Cv2.EstimateAffinePartial2D" />.</summary>
    public static Similarity FromMat(Mat m) =>
        new(m.At<double>(0, 0), m.At<double>(1, 0), m.At<double>(0, 2), m.At<double>(1, 2));
}