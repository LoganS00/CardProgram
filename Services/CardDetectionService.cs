using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace CardProgram.Services
{
    public class CardDetectionService
    {
        // One Piece card aspect ratio is ~2.5 x 3.5 inches
        private const double MinAspect = 0.55;
        private const double MaxAspect = 0.85;
        private const int MinCardPx = 100;

        /// <summary>
        /// Attempts to find and crop the largest card-shaped rectangle in the image.
        /// Returns null if no suitable card shape is found.
        /// </summary>
        public BitmapSource? DetectAndCropCard(BitmapSource source)
        {
            using var mat = BitmapSourceToMat(source);
            using var gray = new Mat();
            using var blurred = new Mat();
            using var edges = new Mat();

            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(5, 5), 0);
            Cv2.Canny(blurred, edges, 30, 120);

            // Dilate to connect nearby edges
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.Dilate(edges, edges, kernel, iterations: 2);

            Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Rect? bestRect = null;
            double bestArea = 0;

            foreach (var contour in contours)
            {
                var peri = Cv2.ArcLength(contour, true);
                var approx = Cv2.ApproxPolyDP(contour, 0.02 * peri, true);

                if (approx.Length != 4) continue;

                var rect = Cv2.BoundingRect(approx);
                if (rect.Width < MinCardPx || rect.Height < MinCardPx) continue;

                double aspect = (double)rect.Width / rect.Height;
                if (aspect < MinAspect || aspect > MaxAspect) continue;

                double area = rect.Width * rect.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestRect = rect;
                }
            }

            if (bestRect == null) return null;

            // Add a small margin
            var r = bestRect.Value;
            int margin = 4;
            r.X = Math.Max(0, r.X - margin);
            r.Y = Math.Max(0, r.Y - margin);
            r.Width = Math.Min(mat.Width - r.X, r.Width + margin * 2);
            r.Height = Math.Min(mat.Height - r.Y, r.Height + margin * 2);

            using var cropped = new Mat(mat, r);
            return MatToBitmapSource(cropped);
        }

        private static Mat BitmapSourceToMat(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
        }

        private static BitmapSource MatToBitmapSource(Mat mat)
        {
            var bytes = mat.ToBytes(".png");
            using var ms = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
    }
}
