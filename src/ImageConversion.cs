using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Supervertaler.Core
{
    /// <summary>
    /// Turns the vector images a Word document carries - EMF and WMF, which is what
    /// a drawing placed from CAD or a draughtsman's tool usually is - into a PNG a
    /// vision model can read.
    ///
    /// <para>No vision API accepts a metafile. Without this the figure pass failed
    /// on exactly the documents it exists for: every row came back "not a format a
    /// vision model accepts", after the images had been extracted perfectly well.
    /// GDI+ renders them and ships with Windows, so nothing is installed for it.</para>
    ///
    /// <para>Scale: one image is decoded and rasterised at a time, never a folder at
    /// once, and only for the two vector formats - a raster image is copied as it is.
    /// A metafile is small; the PNG it becomes is capped at 2200 pixels on its long
    /// edge, which is more than any vision model uses and about 1-3 MB.</para>
    /// </summary>
    public static class ImageConversion
    {
        /// <summary>Smallest long edge to render at: a vector drawing has no natural
        /// pixel size, and a small one rasterised at its nominal size loses the thin
        /// lines and the reference signs, which are the point of looking at it.</summary>
        private const int MinLongEdge = 900;

        /// <summary>Largest long edge: past this, cost and upload time grow and no
        /// vision model sees more detail.</summary>
        private const int MaxLongEdge = 2200;

        /// <summary>True for the image formats no vision model reads: EMF and WMF.</summary>
        public static bool IsVectorFormat(string extensionOrPath)
        {
            if (string.IsNullOrWhiteSpace(extensionOrPath)) return false;
            var ext = extensionOrPath.StartsWith(".", StringComparison.Ordinal)
                ? extensionOrPath
                : Path.GetExtension(extensionOrPath);
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".emf":
                case ".wmf":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The image rendered as a PNG, or null with <paramref name="error"/> set
        /// when it cannot be. Never throws: the caller's fallback is to keep the
        /// original, and a drawing that will not render is a row in a report, not an
        /// exception in a batch.
        /// </summary>
        public static byte[] ToPng(byte[] source, out string error)
        {
            error = null;
            if (source == null || source.Length == 0) { error = "empty file"; return null; }
            try
            {
                using (var ms = new MemoryStream(source, writable: false))
                using (var image = Image.FromStream(ms))
                {
                    int width, height;
                    FitSize(image.Width, image.Height, out width, out height);

                    using (var bmp = new Bitmap(width, height))
                    {
                        bmp.SetResolution(96f, 96f);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            // White, never transparent: a line drawing on a transparent
                            // ground becomes black on black wherever something flattens
                            // it onto dark, and the model then describes an empty image.
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(image, new Rectangle(0, 0, width, height));
                        }
                        using (var outMs = new MemoryStream())
                        {
                            bmp.Save(outMs, ImageFormat.Png);
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Keeps the aspect ratio, brings the long edge inside the readable range.</summary>
        private static void FitSize(int sourceWidth, int sourceHeight, out int width, out int height)
        {
            // A metafile with no usable header: pick a page-shaped default rather than
            // failing, since the drawing itself may still render.
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 20000 || sourceHeight > 20000)
            {
                width = 1600; height = 1200; return;
            }

            double scale = 1.0;
            var longEdge = Math.Max(sourceWidth, sourceHeight);
            if (longEdge < MinLongEdge) scale = (double)MinLongEdge / longEdge;
            else if (longEdge > MaxLongEdge) scale = (double)MaxLongEdge / longEdge;

            width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        }
    }
}
