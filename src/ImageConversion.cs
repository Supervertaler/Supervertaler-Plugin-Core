using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Supervertaler.Core
{
    /// <summary>An image file made ready to show to a vision model.</summary>
    public sealed class VisionImage
    {
        public byte[] Data { get; set; }
        public string MimeType { get; set; }

        /// <summary>What was done to it on the way, when the reader of the result
        /// should know: "first page of 3 only", "scaled down before sending".
        /// Null when the file was sent as it is.</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// Turns the vector images a Word document carries - EMF and WMF, which is what
    /// a drawing placed from CAD or a draughtsman's tool usually is - into a PNG a
    /// vision model can read, and makes any image file fit what a provider accepts.
    ///
    /// <para>No vision API accepts a metafile. Without this the figure pass failed
    /// on exactly the documents it exists for: every row came back "not a format a
    /// vision model accepts", after the images had been extracted perfectly well.
    /// GDI+ renders them and ships with Windows, so nothing is installed for it.</para>
    ///
    /// <para>Scale: one image is decoded and rasterised at a time, never a folder at
    /// once. A file over <see cref="MaxFileBytes"/> is refused before it is read. A
    /// metafile is small; the PNG it becomes is capped at 2200 pixels on its long
    /// edge, which is more than any vision model uses and about 1-3 MB.</para>
    /// </summary>
    public static class ImageConversion
    {
        /// <summary>Smallest long edge to render a vector at: a vector drawing has no natural
        /// pixel size, and a small one rasterised at its nominal size loses the thin
        /// lines and the reference signs, which are the point of looking at it.</summary>
        private const int MinLongEdge = 900;

        /// <summary>Largest long edge: past this, cost and upload time grow and no
        /// vision model sees more detail.</summary>
        private const int MaxLongEdge = 2200;

        /// <summary>A file larger than this is not read at all. Decoding it could
        /// take gigabytes inside the CAT tool's own process.</summary>
        public const long MaxFileBytes = 50L * 1024 * 1024;

        /// <summary>The most bytes sent for one image. Providers refuse an image
        /// over about 5 MB once it is base64-encoded, which adds a third.</summary>
        public const int MaxSendBytes = 3500000;

        /// <summary>Providers refuse an image with an edge over 8000 pixels.</summary>
        private const int MaxProviderEdge = 7900;

        /// <summary>True for the image formats no vision model reads: EMF and WMF.</summary>
        public static bool IsVectorFormat(string extensionOrPath)
        {
            switch (Extension(extensionOrPath))
            {
                case ".emf":
                case ".wmf":
                    return true;
                default:
                    return false;
            }
        }

        private static string Extension(string extensionOrPath)
        {
            if (string.IsNullOrWhiteSpace(extensionOrPath)) return "";
            var ext = extensionOrPath.StartsWith(".", StringComparison.Ordinal)
                ? extensionOrPath
                : Path.GetExtension(extensionOrPath);
            return (ext ?? "").ToLowerInvariant();
        }

        /// <summary>The MIME type a vision API takes the file as, or null when it must be converted first (or cannot be read at all).</summary>
        public static string VisionMime(string extensionOrPath)
        {
            switch (Extension(extensionOrPath))
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }

        /// <summary>True for the formats that are not sent as they are but can be rendered to PNG first.</summary>
        private static bool IsConvertible(string ext)
        {
            switch (ext)
            {
                case ".bmp":
                case ".tif":
                case ".tiff":
                case ".emf":
                case ".wmf":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The image file at <paramref name="path"/>, ready to send: as it is when
        /// a provider takes it, converted to PNG when it is a format none reads
        /// (BMP, TIFF, EMF, WMF), and scaled down when it is too big to send.
        /// Null with <paramref name="error"/> in plain words when it cannot be
        /// used: gone, empty, over <see cref="MaxFileBytes"/>, not an image
        /// format, or not actually an image whatever its name says. Never throws,
        /// and never changes the file.
        ///
        /// <para>Checked before anything is sent, so a text file renamed to .png
        /// costs a row in the report and not a paid request.</para>
        /// </summary>
        public static VisionImage PrepareForVision(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "no file"; return null; }
                var info = new FileInfo(path);
                if (!info.Exists) { error = "the file is no longer there"; return null; }
                if (info.Length == 0) { error = "the file is empty"; return null; }
                if (info.Length > MaxFileBytes)
                {
                    error = "the file is too large to read (" + (info.Length / (1024 * 1024)) + " MB; the limit is "
                          + (MaxFileBytes / (1024 * 1024)) + " MB). Save a smaller copy and run this again.";
                    return null;
                }

                var ext = Extension(path);
                var mime = VisionMime(ext);
                if (mime == null && !IsConvertible(ext))
                {
                    error = "not an image format that can be read (" + (ext.Length > 0 ? ext : "no extension") + ")";
                    return null;
                }

                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex) { error = "could not read the file: " + ex.Message; return null; }

                // GDI+ has no WebP decoder, so a WebP is checked by its header and
                // sent as it is, or not at all.
                if (ext == ".webp")
                {
                    if (!LooksLikeWebp(bytes)) { error = "not a readable image (the file is not a WebP image, whatever its name says)"; return null; }
                    if (bytes.Length > MaxSendBytes)
                    {
                        error = "too large to send (" + (bytes.Length / (1024 * 1024)) + " MB), and a WebP image cannot be made smaller here. Save it as PNG or JPG and run this again.";
                        return null;
                    }
                    return new VisionImage { Data = bytes, MimeType = mime };
                }

                int width, height, pages = 1;
                try
                {
                    using (var ms = new MemoryStream(bytes, writable: false))
                    using (var image = Image.FromStream(ms, false, false))
                    {
                        width = image.Width; height = image.Height;
                        if (ext == ".tif" || ext == ".tiff")
                        {
                            try { pages = image.GetFrameCount(FrameDimension.Page); } catch { pages = 1; }
                        }
                    }
                }
                catch
                {
                    error = "not a readable image (the file is damaged, or is not the kind of image its name says)";
                    return null;
                }

                if (mime != null && bytes.Length <= MaxSendBytes && Math.Max(width, height) <= MaxProviderEdge)
                    return new VisionImage { Data = bytes, MimeType = mime };

                // Converted, or made smaller. PNG first: a line drawing stays sharp
                // and small as PNG. A photograph does not, so JPEG is the fallback.
                var vector = IsVectorFormat(ext);
                string renderError;
                var png = Render(bytes, vector, false, out renderError);
                if (png == null)
                {
                    error = "could not be converted to PNG (" + renderError + "). Open it, save it as PNG, and run this again.";
                    return null;
                }

                var note = pages > 1 ? "first page of " + pages + " only"
                         : mime != null ? "scaled down before sending"
                         : null;
                if (png.Length <= MaxSendBytes) return new VisionImage { Data = png, MimeType = "image/png", Note = note };

                var jpeg = Render(bytes, vector, true, out renderError);
                if (jpeg != null && jpeg.Length <= MaxSendBytes) return new VisionImage { Data = jpeg, MimeType = "image/jpeg", Note = note };

                error = "too large to send even after scaling it down. Save a smaller copy and run this again.";
                return null;
            }
            catch (Exception ex)
            {
                error = "could not read the file: " + ex.Message;
                return null;
            }
        }

        private static bool LooksLikeWebp(byte[] b)
        {
            return b != null && b.Length >= 12
                && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';
        }

        /// <summary>
        /// The image rendered as a PNG, or null with <paramref name="error"/> set
        /// when it cannot be. Never throws: the caller's fallback is to keep the
        /// original, and a drawing that will not render is a row in a report, not an
        /// exception in a batch. For the vector formats: a small one is rendered
        /// larger so its thin lines survive.
        /// </summary>
        public static byte[] ToPng(byte[] source, out string error)
        {
            return Render(source, true, false, out error);
        }

        private static byte[] Render(byte[] source, bool vector, bool asJpeg, out string error)
        {
            error = null;
            if (source == null || source.Length == 0) { error = "empty file"; return null; }
            try
            {
                using (var ms = new MemoryStream(source, writable: false))
                using (var image = Image.FromStream(ms))
                {
                    int width, height;
                    FitSize(image.Width, image.Height, vector, out width, out height);

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
                            if (!asJpeg)
                            {
                                bmp.Save(outMs, ImageFormat.Png);
                            }
                            else
                            {
                                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                using (var p = new EncoderParameters(1))
                                {
                                    p.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                                    bmp.Save(outMs, codec, p);
                                }
                            }
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // GDI+ says this both for a file it cannot decode and for one
                // that really is too big to hold.
                error = "the image could not be decoded, or is too large to hold in memory";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Keeps the aspect ratio, brings the long edge inside the readable range.</summary>
        private static void FitSize(int sourceWidth, int sourceHeight, bool vector, out int width, out int height)
        {
            // A metafile with no usable header: pick a page-shaped default rather than
            // failing, since the drawing itself may still render.
            if (sourceWidth <= 0 || sourceHeight <= 0 || (vector && (sourceWidth > 20000 || sourceHeight > 20000)))
            {
                width = 1600; height = 1200; return;
            }

            double scale = 1.0;
            var longEdge = Math.Max(sourceWidth, sourceHeight);
            if (vector && longEdge < MinLongEdge) scale = (double)MinLongEdge / longEdge;
            else if (longEdge > MaxLongEdge) scale = (double)MaxLongEdge / longEdge;

            width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        }
    }
}
