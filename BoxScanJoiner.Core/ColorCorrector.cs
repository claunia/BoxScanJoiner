using OpenCvSharp;

namespace BoxScanJoiner.Core;

/// <summary>Per-channel linear color correction <c>c' = Gain·c + Offset</c>, in BGR order.</summary>
public sealed record ColorCorrection(double[] Gain, double[] Offset)
{
    public static ColorCorrection Identity => new([1, 1, 1], [0, 0, 0]);

    public double Apply(int channel, double value) => Gain[channel] * value + Offset[channel];
}

/// <summary>Equalizes the colors of overlapping scans so they agree with a reference scan.</summary>
internal static class ColorCorrector
{
    /// <summary>Longest side overlaps are reduced to before sampling.</summary>
    const int SAMPLE_DIMENSION = 400;

    /// <summary>Maximum gray gradient for a pixel to be used, rejecting edges where small misalignments matter.</summary>
    const double MAX_GRADIENT = 12;

    /// <summary>Channel values at or beyond these limits are clipped and carry no gain information.</summary>
    const int MIN_VALUE = 4;
    const int MAX_VALUE = 251;

    /// <summary>Strength of the pull towards gain 1 and offset 0, relative to the data.</summary>
    const double PRIOR_WEIGHT = 0.0005;

    /// <summary>Number of quantiles of each overlap's value distribution that are matched.</summary>
    const int QUANTILES = 49;

    /// <summary>Fits a correction for every image; the image with index <paramref name="reference" /> stays unchanged.</summary>
    public static Dictionary<int, ColorCorrection> Fit(IReadOnlyList<WarpedImage> images, int reference)
    {
        List<Samples> samples = [];

        for(var a = 0; a < images.Count; a++)
        {
            for(int b = a + 1; b < images.Count; b++)
            {
                if(Sample(images[a], images[b]) is {} s) samples.Add(s);
            }
        }

        var column = new Dictionary<int, int>();

        foreach(WarpedImage w in images)
        {
            if(w.Index != reference) column[w.Index] = column.Count * 2;
        }

        var gains   = new Dictionary<int, double[]>();
        var offsets = new Dictionary<int, double[]>();

        foreach(WarpedImage w in images)
        {
            gains[w.Index]   = [1, 1, 1];
            offsets[w.Index] = [0, 0, 0];
        }

        if(column.Count > 0)
        {
            for(var channel = 0; channel < 3; channel++)
            {
                double[] x = Solve(samples, column, channel);

                foreach((int index, int c) in column)
                {
                    gains[index][channel]   = x[c];
                    offsets[index][channel] = x[c + 1];
                }
            }
        }

        return images.ToDictionary(w => w.Index, w => new ColorCorrection(gains[w.Index], offsets[w.Index]));
    }

    public static void Apply(WarpedImage image, ColorCorrection correction)
    {
        using var m = Mat.FromPixelData(3,
                                        4,
                                        MatType.CV_64FC1,
                                        new[,]
                                        {
                                            {
                                                correction.Gain[0], 0, 0, correction.Offset[0]
                                            },
                                            {
                                                0, correction.Gain[1], 0, correction.Offset[1]
                                            },
                                            {
                                                0, 0, correction.Gain[2], correction.Offset[2]
                                            }
                                        });

        Cv2.Transform(image.Image, image.Image, m);
    }

    /// <summary>Corresponding pixel values of two scans where both cover the canvas.</summary>
    sealed class Samples
    {
        public required int    First  { get; init; }
        public required int    Second { get; init; }
        public required byte[] A      { get; init; }
        public required byte[] B      { get; init; }

        public int Count => A.Length / 3;
    }

    static Samples? Sample(WarpedImage a, WarpedImage b)
    {
        Rect r = a.Roi.Intersect(b.Roi);

        if(r.Width < 8 || r.Height < 8) return null;

        Rect ra = r with
        {
            X = r.X - a.Roi.X,
            Y = r.Y - a.Roi.Y
        };

        Rect rb = r with
        {
            X = r.X - b.Roi.X,
            Y = r.Y - b.Roi.Y
        };

        double scale = Math.Min(1.0, (double)SAMPLE_DIMENSION / Math.Max(r.Width, r.Height));
        var    size  = new Size(Math.Max(1, (int)(r.Width * scale)), Math.Max(1, (int)(r.Height * scale)));

        using Mat imgA  = Shrink(a.Image[ra], size);
        using Mat imgB  = Shrink(b.Image[rb], size);
        using Mat maskA = Shrink(a.Mask[ra],  size);
        using Mat maskB = Shrink(b.Mask[rb],  size);

        // Only pixels fully covered by both scans after averaging
        using var valid = new Mat();
        Cv2.Min(maskA, maskB, valid);
        Cv2.Threshold(valid, valid, 254, 255, ThresholdTypes.Binary);

        using Mat flatA = FlatMask(imgA);
        using Mat flatB = FlatMask(imgB);
        Cv2.BitwiseAnd(valid, flatA, valid);
        Cv2.BitwiseAnd(valid, flatB, valid);

        using Mat inRangeA = InRangeMask(imgA);
        using Mat inRangeB = InRangeMask(imgB);
        Cv2.BitwiseAnd(valid, inRangeA, valid);
        Cv2.BitwiseAnd(valid, inRangeB, valid);

        valid.GetArray(out byte[] v);
        imgA.GetArray(out Vec3b[] pa);
        imgB.GetArray(out Vec3b[] pb);

        List<byte> outA = [];
        List<byte> outB = [];

        for(var i = 0; i < v.Length; i++)
        {
            if(v[i] == 0) continue;

            outA.AddRange([pa[i].Item0, pa[i].Item1, pa[i].Item2]);
            outB.AddRange([pb[i].Item0, pb[i].Item1, pb[i].Item2]);
        }

        if(outA.Count == 0) return null;

        return new Samples
        {
            First  = a.Index,
            Second = b.Index,
            A      = outA.ToArray(),
            B      = outB.ToArray()
        };
    }

    static Mat Shrink(Mat image, Size size)
    {
        var result = new Mat();
        Cv2.Resize(image, result, size, 0, 0, InterpolationFlags.Area);

        if(result.Channels() == 3) Cv2.GaussianBlur(result, result, new Size(3, 3), 0);

        return result;
    }

    static Mat FlatMask(Mat bgr)
    {
        using var gray = new Mat();
        using var dx   = new Mat();
        using var dy   = new Mat();
        using var mag  = new Mat();
        var       flat = new Mat();

        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Sobel(gray, dx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(gray, dy, MatType.CV_32F, 0, 1);
        Cv2.Magnitude(dx, dy, mag);
        Cv2.Threshold(mag, mag, MAX_GRADIENT, 255, ThresholdTypes.BinaryInv);
        mag.ConvertTo(flat, MatType.CV_8UC1);

        return flat;
    }

    static Mat InRangeMask(Mat bgr)
    {
        var mask = new Mat();
        Cv2.InRange(bgr, Scalar.All(MIN_VALUE), Scalar.All(MAX_VALUE), mask);

        return mask;
    }

    /// <summary>
    ///     Least-squares fit of gain and offset for one channel from <c>g_i·a + o_i = g_j·b + o_j</c>, applied to
    ///     matching quantiles of the two scans' values in each overlap. Comparing distributions rather than single
    ///     pixels is insensitive to small misalignments, and unlike pixel-wise regression it does not pull the gain
    ///     towards zero when an overlap holds only a narrow range of values.
    /// </summary>
    static double[] Solve(List<Samples> samples, Dictionary<int, int> column, int channel)
    {
        int size = column.Count * 2;
        var ata  = new double[size, size];
        var atb  = new double[size];
        var used = new double[size];

        Span<int>    idx  = stackalloc int[4];
        Span<double> coef = stackalloc double[4];

        foreach(Samples s in samples)
        {
            bool hasI = column.TryGetValue(s.First,  out int ci);
            bool hasJ = column.TryGetValue(s.Second, out int cj);

            double[] a = Quantiles(s.A, channel, s.Count);
            double[] b = Quantiles(s.B, channel, s.Count);

            // Each overlap counts in proportion to its size
            double weight = (double)s.Count / QUANTILES;

            for(var q = 0; q < QUANTILES; q++)
            {
                // Row: [a, 1] for i, [-b, -1] for j; reference values move to the right-hand side
                var    n   = 0;
                double rhs = 0;

                if(hasI)
                {
                    idx[n]    =  ci;
                    coef[n++] =  a[q];
                    idx[n]    =  ci + 1;
                    coef[n++] =  1;
                    used[ci]  += weight;
                }
                else
                    rhs -= a[q];

                if(hasJ)
                {
                    idx[n]    =  cj;
                    coef[n++] =  -b[q];
                    idx[n]    =  cj + 1;
                    coef[n++] =  -1;
                    used[cj]  += weight;
                }
                else
                    rhs += b[q];

                for(var r = 0; r < n; r++)
                {
                    atb[idx[r]] += weight * coef[r] * rhs;

                    for(var c = 0; c < n; c++) ata[idx[r], idx[c]] += weight * coef[r] * coef[c];
                }
            }
        }

        // Priors keep the system well posed and corrections modest; gain is scaled to intensity units
        foreach(int c in column.Values)
        {
            double w = PRIOR_WEIGHT * Math.Max(1, used[c]);
            ata[c, c]         += w * 128 * 128;
            atb[c]            += w * 128 * 128;
            ata[c + 1, c + 1] += w;
        }

        using var lhs  = Mat.FromPixelData(size, size, MatType.CV_64FC1, ata);
        using var rhsM = Mat.FromPixelData(size, 1,    MatType.CV_64FC1, atb);
        using var x    = new Mat();

        if(!Cv2.Solve(lhs, rhsM, x, DecompTypes.Cholesky)) Cv2.Solve(lhs, rhsM, x, DecompTypes.SVD);

        x.GetArray(out double[] result);

        return result;
    }

    /// <summary>Evenly spaced quantiles, from 2% to 98%, of one channel of interleaved BGR samples.</summary>
    static double[] Quantiles(byte[] bgr, int channel, int count)
    {
        // Counting sort, values are bytes
        var histogram = new int[256];

        for(var k = 0; k < count; k++) histogram[bgr[k * 3 + channel]]++;

        var result     = new double[QUANTILES];
        var cumulative = 0;
        var value      = 0;

        for(var q = 0; q < QUANTILES; q++)
        {
            double target = (q + 1) / (QUANTILES + 1.0) * count;

            while(value < 255 && cumulative + histogram[value] < target) cumulative += histogram[value++];

            result[q] = value;
        }

        return result;
    }
}