using Avalonia.Media.Imaging;
using OpenCvSharp;

namespace BoxScanJoiner.Gui;

static class ImageConversion
{
    /// <summary>Converts an OpenCV image to an Avalonia bitmap no larger than <paramref name="maxDimension" />.</summary>
    public static Bitmap ToBitmap(Mat image, int maxDimension)
    {
        double scale = Math.Min(1.0, (double)maxDimension / Math.Max(image.Width, image.Height));

        using var small = new Mat();

        if(scale < 1)
            Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        else
            image.CopyTo(small);

        // Uncompressed PNG is the quickest lossless way across the two libraries
        Cv2.ImEncode(".png", small, out byte[] png, new ImageEncodingParam(ImwriteFlags.PngCompression, 0));

        using var stream = new MemoryStream(png);

        return new Bitmap(stream);
    }

    /// <summary>Loads a small thumbnail of an image file, or null if it cannot be read.</summary>
    public static Bitmap? LoadThumbnail(string path, int maxDimension)
    {
        using Mat image = Cv2.ImRead(path, ImreadModes.ReducedColor4);

        return image.Empty() ? null : ToBitmap(image, maxDimension);
    }
}