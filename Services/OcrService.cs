using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CardProgram.Services
{
    public record CardOcrResult(string CardName, string RawText);

    public class OcrService
    {
        private readonly OcrEngine _engine;

        // One Piece card number e.g. OP01-001, ST03-010, EB01-005, P-001
        private static readonly Regex CardNumberRegex = new(@"\b([A-Z]{1,3}\d{2}-\d{3}[A-Z]?)\b", RegexOptions.IgnoreCase);

        // Words that appear on card bodies — not names
        private static readonly Regex NoisePattern = new(
            @"(?i)\b(cost|power|counter|life|don|attribute|type|effect|trigger|blocker|rush|banish|once|per|turn|your|opponent|when|this|card|play|area|all|rest|active|you|may|trash|deck|hand|character|event|stage|leader)\b");

        public OcrService()
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));
        }

        public async Task<CardOcrResult> ReadCardAsync(BitmapSource source)
        {
            // Run OCR at multiple contrast levels and on cropped regions
            var texts = new List<string>();

            texts.Add(await RunOcrAsync(Preprocess(source, 1.0f)));   // original
            texts.Add(await RunOcrAsync(Preprocess(source, 1.8f)));   // high contrast
            texts.Add(await RunOcrAsync(Preprocess(source, 2.5f)));   // very high contrast

            // Also OCR just the bottom 40% — where name banner + card number live
            var bottom = CropFraction(source, 0.60, 1.0);
            texts.Add(await RunOcrAsync(Preprocess(bottom, 1.8f)));

            // And top 20% — some card styles put the name at the top
            var top = CropFraction(source, 0.0, 0.20);
            texts.Add(await RunOcrAsync(Preprocess(top, 1.8f)));

            var combined = string.Join("\n", texts);

            // Card number is most reliable — use it to anchor the search
            var cardNumber = CardNumberRegex.Match(combined);
            var name = ExtractBestName(combined);

            // If we found a card number, prefer "name + number" or just number
            var query = name;
            if (cardNumber.Success)
            {
                query = !string.IsNullOrWhiteSpace(name)
                    ? $"{name} {cardNumber.Value}"
                    : cardNumber.Value;
            }

            return new CardOcrResult(query.Trim(), combined);
        }

        private static string ExtractBestName(string allText)
        {
            var lines = allText.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string best = string.Empty;
            int bestScore = 0;

            foreach (var line in lines)
            {
                if (line.Length < 3 || line.Length > 45) continue;
                if (CardNumberRegex.IsMatch(line)) continue;
                if (Regex.IsMatch(line, @"^[\d\W]+$")) continue;

                // Skip lines dominated by game mechanic words
                if (NoisePattern.Matches(line).Count >= 2) continue;

                int letters = 0;
                foreach (char c in line) if (char.IsLetter(c)) letters++;
                double ratio = (double)letters / line.Length;
                if (ratio < 0.55) continue;

                // Score: reward proper-case words (character names are usually title case)
                int score = letters;
                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var w in words)
                    if (w.Length > 1 && char.IsUpper(w[0])) score += 5;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = line;
                }
            }

            return best;
        }

        private async Task<string> RunOcrAsync(BitmapSource source)
        {
            try
            {
                var soft = await ToSoftwareBitmapAsync(source);
                var result = await _engine.RecognizeAsync(soft);
                return result.Text;
            }
            catch { return string.Empty; }
        }

        private static BitmapSource CropFraction(BitmapSource source, double fromY, double toY)
        {
            try
            {
                int y = (int)(source.PixelHeight * fromY);
                int h = (int)(source.PixelHeight * (toY - fromY));
                if (h <= 0) return source;
                return new CroppedBitmap(source,
                    new System.Windows.Int32Rect(0, y, source.PixelWidth, h));
            }
            catch { return source; }
        }

        private static BitmapSource Preprocess(BitmapSource source, float contrast)
        {
            try
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                using var ms = new MemoryStream();
                enc.Save(ms);
                ms.Position = 0;

                using var orig = new Bitmap(ms);
                int w = orig.Width * 2, h = orig.Height * 2;

                using var scaled = new Bitmap(w, h);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(orig, 0, 0, w, h);
                }

                using var result = new Bitmap(w, h);
                using (var g = Graphics.FromImage(result))
                {
                    float t = (1f - contrast) / 2f;
                    var cm = new ColorMatrix(new[]
                    {
                        new[] { contrast, 0, 0, 0, 0 },
                        new[] { 0f, contrast, 0, 0, 0 },
                        new[] { 0f, 0f, contrast, 0, 0 },
                        new[] { 0f, 0f, 0f, 1f, 0 },
                        new[] { t, t, t, 0, 1f }
                    });
                    var ia = new ImageAttributes();
                    ia.SetColorMatrix(cm);
                    g.DrawImage(scaled, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }

                using var outMs = new MemoryStream();
                result.Save(outMs, ImageFormat.Png);
                outMs.Position = 0;
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                    outMs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            catch { return source; }
        }

        private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource source)
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
    }
}
