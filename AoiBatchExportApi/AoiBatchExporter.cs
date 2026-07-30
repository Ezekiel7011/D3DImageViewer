using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AoiBatchExportApi;

#region Public API Models

/// <summary>
/// Overlay kinds supported by manual image export.
/// </summary>
public enum AoiExportOverlayType { Roi = 0, Measure = 1, Circle = 2, AoiDieCenter = 3, AoiDieCorner = 4, AoiDefect = 5 }

/// <summary>
/// Overlay snapshot supplied by the viewer when exporting the current image with visible overlays.
/// </summary>
public sealed record AoiExportOverlay(int Id, AoiExportOverlayType Type, bool Visible, float X1, float Y1, float X2, float Y2, float Angle, string Name);

/// <summary>
/// Aggregate result returned after a folder batch export.
/// </summary>
public sealed record AoiBatchExportResult(int TotalScanned, int ExportedCount, int SkippedWithoutMetadata, int FailedCount, IReadOnlyList<string> Errors);

/// <summary>
/// Exports AOI images either as fixed report layouts or as original images composited with viewer overlays.
/// </summary>
public static class AoiBatchExporter
{
    #region Supported Formats

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".png", ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".gif", ".wdp", ".jxr", ".webp"
    };

    #endregion

    #region Public Export API

    /// <summary>
    /// Exports all supported images in a folder, rendering AOI metadata when available and copying plain images otherwise.
    /// </summary>
    public static AoiBatchExportResult ExportFolder(string sourceFolder, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentException("sourceFolder is required.", nameof(sourceFolder));
        if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException(sourceFolder);
        if (string.IsNullOrWhiteSpace(destinationFolder)) throw new ArgumentException("destinationFolder is required.", nameof(destinationFolder));
        Directory.CreateDirectory(destinationFolder);

        int total = 0, exported = 0, noMeta = 0, failed = 0;
        var errors = new List<string>();
        var log = new StringBuilder();
        log.AppendLine("AOI Batch Export Log");
        log.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        log.AppendLine("Source: " + sourceFolder);
        log.AppendLine("Destination: " + destinationFolder);
        log.AppendLine();

        // Export each supported image once. Images with AOI metadata are rendered into
        // the fixed report layout; images without metadata are copied through unchanged.
        foreach (var file in Directory.EnumerateFiles(sourceFolder).Where(p => ImageExts.Contains(Path.GetExtension(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            total++;
            try
            {
                if (TryReadMetadata(file, out var meta))
                {
                    string outName = Path.GetFileNameWithoutExtension(file) + "_AOI.jpg";
                    string outPath = UniqueOutputPath(Path.Combine(destinationFolder, outName));
                    ExportFixedLayout(file, outPath, meta!);
                    exported++;
                }
                else
                {
                    noMeta++;
                    log.AppendLine("NO_METADATA: " + file);
                    string outPath = UniqueOutputPath(Path.Combine(destinationFolder, Path.GetFileName(file)));
                    File.Copy(file, outPath, overwrite: false);
                    exported++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                string msg = $"{Path.GetFileName(file)} : {ex.Message}";
                errors.Add(msg);
                log.AppendLine("FAILED: " + file);
                log.AppendLine("        " + ex.Message);
            }
        }

        log.AppendLine();
        log.AppendLine($"Scanned: {total}");
        log.AppendLine($"Exported: {exported}");
        log.AppendLine($"NoMetadata: {noMeta}");
        log.AppendLine($"Failed: {failed}");
        File.WriteAllText(Path.Combine(destinationFolder, "BatchExportLog.txt"), log.ToString(), Encoding.UTF8);

        return new AoiBatchExportResult(total, exported, noMeta, failed, errors);
    }

    /// <summary>
    /// Saves one image with the provided visible overlays drawn directly in original image coordinates.
    /// </summary>
    public static void ExportImageWithOverlays(string imagePath, string destinationPath, IEnumerable<AoiExportOverlay> overlays)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("imagePath is required.", nameof(imagePath));
        if (!File.Exists(imagePath)) throw new FileNotFoundException(imagePath);
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("destinationPath is required.", nameof(destinationPath));
        EnsureParentDirectory(destinationPath);

        using var src = Image.FromFile(imagePath);
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, 0, 0, src.Width, src.Height);

        foreach (var o in overlays.Where(o => o.Visible))
            DrawOverlayOnOriginal(g, o);

        SaveImageByExtension(bmp, destinationPath);
    }

    /// <summary>
    /// Attempts to export one image and returns a user-displayable message instead of throwing.
    /// </summary>
    public static bool TryExportImage(string imagePath, string destinationPath, out string message)
    {
        try
        {
            EnsureParentDirectory(destinationPath);
            if (TryReadMetadata(imagePath, out var meta))
                ExportFixedLayout(imagePath, destinationPath, meta!);
            else
            {
                using var src = Image.FromFile(imagePath);
                SaveImageByExtension(src, destinationPath);
            }
            message = destinationPath;
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    #endregion

    #region Fixed Layout Rendering

    private static void ExportFixedLayout(string imagePath, string destinationPath, AoiMeta meta)
    {
        // Fixed-size canvas used by batch export: image preview on the left,
        // compact metadata summary on the right, AOI overlays mapped into preview space.
        const int canvasW = 1600;
        const int canvasH = 900;
        const int outer = 24;
        const int gap = 22;
        const int rightW = 430;

        using var image = Image.FromFile(imagePath);
        using var bmp = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.FromArgb(18, 22, 26));

        var leftRect = new RectangleF(outer, outer, canvasW - rightW - gap - outer * 2, canvasH - outer * 2);
        var rightRect = new RectangleF(leftRect.Right + gap, outer, rightW, canvasH - outer * 2);

        using var leftBg = new SolidBrush(Color.FromArgb(10, 10, 10));
        using var rightBg = new SolidBrush(Color.FromArgb(20, 26, 32));
        using var panelPen = new Pen(Color.FromArgb(70, 90, 105), 1.3f);
        g.FillRectangle(leftBg, leftRect); g.DrawRectangle(panelPen, leftRect.X, leftRect.Y, leftRect.Width, leftRect.Height);
        g.FillRectangle(rightBg, rightRect); g.DrawRectangle(panelPen, rightRect.X, rightRect.Y, rightRect.Width, rightRect.Height);

        var fitted = FitRect(image.Width, image.Height, leftRect);
        g.DrawImage(image, fitted);

        float sx = fitted.Width / image.Width;
        float sy = fitted.Height / image.Height;
        Func<double, float> mapX = x => fitted.X + (float)x * sx;
        Func<double, float> mapY = y => fitted.Y + (float)y * sy;

        if (meta.DiePos is { } die)
            DrawDieCorners(g, mapX(die.X), mapY(die.Y), (float)(die.W * sx), (float)(die.H * sy));
        if (meta.DieCenterX.HasValue && meta.DieCenterY.HasValue)
            DrawDieCenter(g, mapX(meta.DieCenterX.Value), mapY(meta.DieCenterY.Value));
        foreach (var d in meta.Defects)
            DrawDefect(g, mapX(d.Rect.X), mapY(d.Rect.Y), (float)(d.Rect.W * sx), (float)(d.Rect.H * sy), d.Index, drawLabel: false);

        DrawRightInfo(g, rightRect, Path.GetFileName(imagePath), meta);

        EnsureParentDirectory(destinationPath);
        SaveJpeg(bmp, destinationPath, 92L);
    }

    #endregion

    #region File and Encoding Helpers

    private static void EnsureParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    private static string UniqueOutputPath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void SaveImageByExtension(Image image, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg") SaveJpeg(image, path, 92L);
        else if (ext == ".bmp") image.Save(path, ImageFormat.Bmp);
        else image.Save(path, ImageFormat.Png);
    }

    private static void SaveJpeg(Image image, string path, long quality)
    {
        EnsureParentDirectory(path);
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (codec == null)
        {
            image.Save(path, ImageFormat.Jpeg);
            return;
        }
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        image.Save(path, codec, ep);
    }

    #endregion

    #region Overlay Drawing Helpers

    private static void DrawOverlayOnOriginal(Graphics g, AoiExportOverlay o)
    {
        // Manual overlay export draws in original image coordinates, unlike the fixed
        // batch layout which maps image coordinates into a fitted preview rectangle.
        switch (o.Type)
        {
            case AoiExportOverlayType.AoiDieCenter:
                DrawDieCenter(g, o.X1, o.Y1);
                break;
            case AoiExportOverlayType.AoiDieCorner:
                DrawDieCorners(g, Math.Min(o.X1, o.X2), Math.Min(o.Y1, o.Y2), Math.Abs(o.X2 - o.X1), Math.Abs(o.Y2 - o.Y1));
                break;
            case AoiExportOverlayType.AoiDefect:
                DrawDefect(g, Math.Min(o.X1, o.X2), Math.Min(o.Y1, o.Y2), Math.Abs(o.X2 - o.X1), Math.Abs(o.Y2 - o.Y1), o.Id, drawLabel: false);
                break;
            case AoiExportOverlayType.Measure:
                using (var pen = new Pen(Color.Cyan, 2.0f)) g.DrawLine(pen, o.X1, o.Y1, o.X2, o.Y2);
                break;
            case AoiExportOverlayType.Circle:
                DrawRotatedEllipse(g, o.X1, o.Y1, Math.Abs(o.X2 - o.X1), Math.Abs(o.Y2 - o.Y1), o.Angle, Color.Yellow);
                break;
            default:
                DrawRotatedRect(g, o.X1, o.Y1, Math.Abs(o.X2 - o.X1), Math.Abs(o.Y2 - o.Y1), o.Angle, Color.Yellow);
                break;
        }
    }

    private static void DrawRotatedRect(Graphics g, float x, float y, float w, float h, float angle, Color color)
    {
        using var pen = new Pen(color, 2.0f);
        var state = g.Save();
        g.TranslateTransform(x + w / 2f, y + h / 2f);
        g.RotateTransform(angle);
        g.DrawRectangle(pen, -w / 2f, -h / 2f, w, h);
        g.Restore(state);
    }

    private static void DrawRotatedEllipse(Graphics g, float x, float y, float w, float h, float angle, Color color)
    {
        using var pen = new Pen(color, 2.0f);
        var state = g.Save();
        g.TranslateTransform(x + w / 2f, y + h / 2f);
        g.RotateTransform(angle);
        g.DrawEllipse(pen, -w / 2f, -h / 2f, w, h);
        g.Restore(state);
    }

    private static void DrawRightInfo(Graphics g, RectangleF rect, string fileName, AoiMeta meta)
    {
        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var valueFont = new Font("Consolas", 10, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.White);
        using var labelBrush = new SolidBrush(Color.FromArgb(128, 210, 255));
        using var valueBrush = new SolidBrush(Color.White);
        using var linePen = new Pen(Color.FromArgb(60, 85, 100), 1f);

        float x = rect.X + 16;
        float y = rect.Y + 14;
        g.DrawString("AOI Export", titleFont, titleBrush, x, y);
        y += 30;
        g.DrawString(fileName, valueFont, valueBrush, new RectangleF(x, y, rect.Width - 32, 34));
        y += 38;
        g.DrawLine(linePen, x, y, rect.Right - 16, y);
        y += 12;

        DrawKv(g, "Created", meta.Created, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Result", meta.Result, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Die XYT", meta.DieXYT, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Chip H/W", meta.ChipHW, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Defects", meta.Defects.Count.ToString(CultureInfo.InvariantCulture), x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Error", meta.Error, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush);
        DrawKv(g, "Path", meta.Path, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush, 48);
        DrawKv(g, "Recipe", meta.Recipe, x, ref y, rect.Width - 32, labelFont, valueFont, labelBrush, valueBrush, 48);
        y += 6;
        g.DrawLine(linePen, x, y, rect.Right - 16, y);
        y += 12;
        g.DrawString("AOI Objects", labelFont, labelBrush, x, y);
        y += 24;

        foreach (var d in meta.Defects.Take(18))
        {
            string txt = $"Defect {d.Index}   Bin {d.Bin}   {d.AlgoIds}";
            g.DrawString(txt, valueFont, valueBrush, new RectangleF(x, y, rect.Width - 32, 18));
            y += 18;
        }
        if (meta.Defects.Count > 18)
        {
            g.DrawString($"... {meta.Defects.Count - 18} more", valueFont, valueBrush, x, y);
        }
    }

    private static void DrawKv(Graphics g, string key, string value, float x, ref float y, float width, Font labelFont, Font valueFont, Brush labelBrush, Brush valueBrush, float valueHeight = 22)
    {
        g.DrawString(key, labelFont, labelBrush, x, y);
        y += 18;
        g.DrawString(string.IsNullOrWhiteSpace(value) ? "--" : value, valueFont, valueBrush, new RectangleF(x, y, width, valueHeight));
        y += valueHeight + 8;
    }

    private static RectangleF FitRect(int iw, int ih, RectangleF bounds)
    {
        float scale = Math.Min(bounds.Width / iw, bounds.Height / ih);
        float w = iw * scale, h = ih * scale;
        return new RectangleF(bounds.X + (bounds.Width - w) / 2f, bounds.Y + (bounds.Height - h) / 2f, w, h);
    }

    private static void DrawDefect(Graphics g, float x, float y, float w, float h, int index, bool drawLabel)
    {
        using var pen = new Pen(Color.FromArgb(255, 60, 60), Math.Max(1.6f, Math.Min(3.2f, Math.Min(w, h) * 0.03f)));
        g.DrawRectangle(pen, x, y, w, h);
        if (!drawLabel) return;
        using var font = new Font("Consolas", 9, FontStyle.Bold);
        using var bg = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using var fg = new SolidBrush(Color.White);
        string txt = index.ToString(CultureInfo.InvariantCulture);
        var sz = g.MeasureString(txt, font);
        g.FillRectangle(bg, x, Math.Max(0, y - sz.Height - 2), sz.Width + 8, sz.Height + 2);
        g.DrawString(txt, font, fg, x + 4, Math.Max(0, y - sz.Height - 1));
    }

    private static void DrawDieCenter(Graphics g, float cx, float cy)
    {
        using var pen = new Pen(Color.FromArgb(255, 230, 80), 2f);
        float r = 12f;
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
        g.DrawLine(pen, cx, cy - 7, cx, cy + 7);
    }

    private static void DrawDieCorners(Graphics g, float x, float y, float w, float h)
    {
        using var pen = new Pen(Color.Lime, 2.2f);
        float l = Math.Max(16f, Math.Min(w, h) * 0.12f);
        // TL
        g.DrawLine(pen, x, y, x + l, y); g.DrawLine(pen, x, y, x, y + l);
        // TR
        g.DrawLine(pen, x + w, y, x + w - l, y); g.DrawLine(pen, x + w, y, x + w, y + l);
        // BL
        g.DrawLine(pen, x, y + h, x + l, y + h); g.DrawLine(pen, x, y + h, x, y + h - l);
        // BR
        g.DrawLine(pen, x + w, y + h, x + w - l, y + h); g.DrawLine(pen, x + w, y + h, x + w, y + h - l);
    }

    #endregion

    #region Metadata Model and Parsing

    private sealed record AoiRect(double X, double Y, double W, double H);
    private sealed record AoiDefectRec(int Index, AoiRect Rect, double WidthUm, double HeightUm, string Bin, string AlgoIds);
    private sealed class AoiMeta
    {
        public string Created = "--";
        public string Result = "--";
        public string DieXYT = "--";
        public string ChipHW = "--";
        public string Error = "--";
        public string Path = "--";
        public string Recipe = "--";
        public string Source = "--";
        public AoiRect? DiePos;
        public double? DieCenterX;
        public double? DieCenterY;
        public readonly List<AoiDefectRec> Defects = new();
    }

    private static bool TryReadMetadata(string file, out AoiMeta meta)
    {
        meta = new AoiMeta { Path = file };
        bool found = ViewerMetadataLoader.TryLoad(file, out var genericMeta);
        if (!found) return false;

        meta.Source = genericMeta.SourceKind;
        meta.Created = genericMeta.GetProperty("Created");
        meta.Result = genericMeta.GetProperty("Result");
        meta.DieXYT = genericMeta.GetProperty("Die XYT");
        meta.ChipHW = genericMeta.GetProperty("Chip H/W");
        meta.Error = genericMeta.GetProperty("Error");
        meta.Recipe = genericMeta.GetProperty("Recipe");

        if (double.TryParse(genericMeta.GetProperty("DieCenterX", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var dcx) &&
            double.TryParse(genericMeta.GetProperty("DieCenterY", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var dcy))
        {
            meta.DieCenterX = dcx;
            meta.DieCenterY = dcy;
        }

        int defectIndex = 1;
        foreach (var overlay in genericMeta.Overlays)
        {
            if (overlay.Kind == ViewerOverlayKind.Rectangle && overlay.Rect is { } rect)
            {
                var r = new AoiRect(rect.X, rect.Y, rect.W, rect.H);
                if (overlay.Category.Equals("die", StringComparison.OrdinalIgnoreCase) || overlay.Name.Contains("die", StringComparison.OrdinalIgnoreCase))
                {
                    meta.DiePos ??= r;
                    continue;
                }

                if (overlay.Category.Equals("defect", StringComparison.OrdinalIgnoreCase) || overlay.Name.Contains("defect", StringComparison.OrdinalIgnoreCase))
                {
                    double wu = ParseMetaDouble(overlay.Attributes.GetValueOrDefault("WidthUm", "0"));
                    double hu = ParseMetaDouble(overlay.Attributes.GetValueOrDefault("HeightUm", "0"));
                    string bin = overlay.Attributes.GetValueOrDefault("BinCode", overlay.Attributes.GetValueOrDefault("bin", "--"));
                    string algo = overlay.Attributes.GetValueOrDefault("AlgoIds", overlay.Attributes.GetValueOrDefault("AlgoID", "--"));
                    meta.Defects.Add(new AoiDefectRec(defectIndex++, r, wu, hu, bin, algo));
                }
            }
            else if ((overlay.Kind is ViewerOverlayKind.Point or ViewerOverlayKind.Crosshair) && overlay.Points.Count > 0)
            {
                if (overlay.Category.Equals("die-center", StringComparison.OrdinalIgnoreCase) || overlay.Name.Contains("center", StringComparison.OrdinalIgnoreCase))
                {
                    meta.DieCenterX = overlay.Points[0].X;
                    meta.DieCenterY = overlay.Points[0].Y;
                }
            }
        }
        return true;
    }

    private static double ParseMetaDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    #endregion
}

#endregion
