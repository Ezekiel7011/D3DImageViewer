using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AoiBatchExportApi;

#region Metadata Model

/// <summary>
/// Normalized overlay shape kinds that can be loaded from embedded or sidecar metadata.
/// </summary>
public enum ViewerOverlayKind
{
    Rectangle,
    Polygon,
    Point,
    Crosshair,
    Circle,
    Polyline
}

/// <summary>
/// Image-space point used by metadata overlays.
/// </summary>
public readonly record struct ViewerPoint(double X, double Y);

/// <summary>
/// Image-space rectangle used by metadata overlays.
/// </summary>
public readonly record struct ViewerRect(double X, double Y, double W, double H);

/// <summary>
/// One normalized metadata overlay with optional geometry and source attributes.
/// </summary>
public sealed class ViewerOverlay
{
    public ViewerOverlayKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public ViewerRect? Rect { get; set; }
    public List<ViewerPoint> Points { get; } = new();
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Normalized metadata package used by the viewer and batch exporter.
/// </summary>
public sealed class ViewerMetadata
{
    public string SourcePath { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ViewerOverlay> Overlays { get; } = new();
    public List<string> Warnings { get; } = new();

    public string GetProperty(string key, string fallback = "--") =>
        Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}

/// <summary>
/// Loads AOI/viewer metadata from embedded image payloads or nearby sidecar files.
/// </summary>
public static class ViewerMetadataLoader
{
    #region Loader Entry Point

    /// <summary>
    /// Attempts to load metadata for an image and normalizes all supported formats into a shared model.
    /// </summary>
    public static bool TryLoad(string imagePath, out ViewerMetadata metadata)
    {
        metadata = new ViewerMetadata
        {
            SourcePath = imagePath,
            SourceKind = "none"
        };
        metadata.Properties["Path"] = imagePath;

        try
        {
            if (TryReadEmbeddedMetadata(imagePath, out var embedded, out var embeddedSource))
            {
                metadata.SourceKind = embeddedSource;
                metadata.Properties["Source"] = embeddedSource;
                ParseText(embedded, metadata, embeddedSource);
                NormalizeSummary(metadata);
                return true;
            }

            foreach (var candidate in EnumerateSidecarCandidates(imagePath))
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate, Encoding.UTF8);
                metadata.SourceKind = candidate;
                metadata.Properties["Source"] = candidate;
                ParseText(text, metadata, candidate);
                NormalizeSummary(metadata);
                return true;
            }
        }
        catch (Exception ex)
        {
            metadata.Warnings.Add(ex.Message);
            metadata.Properties["Error"] = ex.Message;
            NormalizeSummary(metadata);
            return true;
        }

        metadata.Properties["Error"] = "Metadata not found.";
        NormalizeSummary(metadata);
        return false;
    }

    #endregion

    #region Sidecar Discovery and Format Routing

    private static IEnumerable<string> EnumerateSidecarCandidates(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        yield return Path.Combine(dir, stem + ".meta.json");
        yield return Path.Combine(dir, stem + ".overlay.json");
        yield return Path.ChangeExtension(imagePath, ".json");
        yield return Path.Combine(dir, stem + ".overlay.csv");
        yield return Path.ChangeExtension(imagePath, ".csv");
        yield return Path.ChangeExtension(imagePath, ".txt");
        yield return Path.ChangeExtension(imagePath, ".aoi");
    }

    private static void ParseText(string text, ViewerMetadata metadata, string source)
    {
        // Route by format first, then normalize to one shared ViewerMetadata shape.
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("{"))
        {
            ParseJson(text, metadata);
            return;
        }
        if (LooksLikeCsv(text))
        {
            ParseCsv(text, metadata);
            return;
        }
        ParseKeyValueText(text, metadata);
    }

    private static bool LooksLikeCsv(string text)
    {
        var first = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return false;
        return first.Contains(',') && Regex.IsMatch(first, "(^|,)(type|kind|x|y|width|height|points|rect|bbox)(,|$)", RegexOptions.IgnoreCase);
    }

    #endregion

    #region JSON Parsing

    private static void ParseJson(string json, ViewerMetadata metadata)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            metadata.Warnings.Add("JSON root is not an object.");
            return;
        }

        if (TryGetProperty(root, "properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
                metadata.Properties[p.Name] = ToDisplayString(p.Value);
        }

        // Support both the generic overlay schema and the older AOI-specific schema.
        CopyRootScalarProperties(root, metadata);
        ParseGenericOverlayArrays(root, metadata);
        ParseLegacyAoiJson(root, metadata);
    }

    private static void CopyRootScalarProperties(JsonElement root, ViewerMetadata metadata)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            if (IsKnownInternalArrayName(p.Name)) continue;
            metadata.Properties[p.Name] = ToDisplayString(p.Value);
        }
    }

    private static bool IsKnownInternalArrayName(string name) =>
        name.Equals("overlays", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("objects", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("annotations", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("defectList", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("drawPolygon", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("drawPolygons", StringComparison.OrdinalIgnoreCase);

    private static void ParseGenericOverlayArrays(JsonElement root, ViewerMetadata metadata)
    {
        foreach (var name in new[] { "overlays", "objects", "annotations", "shapes" })
        {
            if (!TryGetProperty(root, name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            int index = 1;
            foreach (var item in arr.EnumerateArray())
            {
                if (TryParseOverlayObject(item, out var overlay, metadata.Warnings))
                {
                    if (string.IsNullOrWhiteSpace(overlay.Name)) overlay.Name = $"{overlay.Kind} {index}";
                    metadata.Overlays.Add(overlay);
                    index++;
                }
            }
        }
    }

    private static bool TryParseOverlayObject(JsonElement item, out ViewerOverlay overlay, List<string> warnings)
    {
        overlay = new ViewerOverlay();
        if (item.ValueKind != JsonValueKind.Object) return false;

        string type = GetString(item, "type", GetString(item, "kind", "rectangle")).Trim().ToLowerInvariant();
        overlay.Name = GetString(item, "name", GetString(item, "label", ""));
        overlay.Category = GetString(item, "category", GetString(item, "class", ""));

        foreach (var p in item.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            overlay.Attributes[p.Name] = ToDisplayString(p.Value);
        }
        if (TryGetProperty(item, "attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in attrs.EnumerateObject()) overlay.Attributes[p.Name] = ToDisplayString(p.Value);
        }

        if (type is "polygon" or "poly" or "roi_polygon")
        {
            overlay.Kind = ViewerOverlayKind.Polygon;
            if (TryReadPointsFromObject(item, overlay.Points) && overlay.Points.Count >= 3) return true;
            warnings.Add($"Polygon '{overlay.Name}' has less than 3 valid points.");
            return false;
        }

        if (type is "point" or "crosshair" or "center")
        {
            overlay.Kind = type == "point" ? ViewerOverlayKind.Point : ViewerOverlayKind.Crosshair;
            if (TryReadPointFromObject(item, out var pt))
            {
                overlay.Points.Add(pt);
                return true;
            }
            warnings.Add($"Point '{overlay.Name}' does not contain valid x/y.");
            return false;
        }

        overlay.Kind = ViewerOverlayKind.Rectangle;
        if (TryReadRectFromObject(item, out var rect))
        {
            overlay.Rect = rect;
            return true;
        }
        warnings.Add($"Rectangle '{overlay.Name}' does not contain valid geometry.");
        return false;
    }

    private static void ParseLegacyAoiJson(JsonElement root, ViewerMetadata metadata)
    {
        // Legacy AOI payloads store die, defect, and polygon data in separate fields.
        // Keep the original properties while also building overlay records for display.
        MapLegacyProperty(metadata, root, "created_time", "Created");
        if (TryGetProperty(root, "detectResult", out var result)) metadata.Properties["Result"] = ToDisplayString(result);
        MapLegacyProperty(metadata, root, "errMsg", "Error");
        MapLegacyProperty(metadata, root, "recipe_path", "Recipe");

        double dcx = GetDouble(root, "dieCenterX"), dcy = GetDouble(root, "dieCenterY"), dct = GetDouble(root, "dieCenterT");
        if (dcx != 0 || dcy != 0 || dct != 0)
        {
            metadata.Properties["Die XYT"] = $"{dcx:0.####}, {dcy:0.####}, {dct:0.####}";
            metadata.Properties["DieCenterX"] = dcx.ToString(CultureInfo.InvariantCulture);
            metadata.Properties["DieCenterY"] = dcy.ToString(CultureInfo.InvariantCulture);
            if (dct != 0) metadata.Properties["DieCenterT"] = dct.ToString(CultureInfo.InvariantCulture);
            metadata.Overlays.Add(new ViewerOverlay
            {
                Kind = ViewerOverlayKind.Crosshair,
                Name = "Die Center",
                Category = "die-center"
            });
            metadata.Overlays[^1].Points.Add(new ViewerPoint(dcx, dcy));
        }

        double chipH = GetDouble(root, "dChipH_mm"), chipW = GetDouble(root, "dChipW_mm");
        if (chipH > 0 || chipW > 0) metadata.Properties["Chip H/W"] = $"{chipH:0.####} / {chipW:0.####} mm";

        if (TryGetProperty(root, "diePos", out var diePosEl) && TryReadRect(diePosEl, out var dieRect))
        {
            metadata.Overlays.Add(new ViewerOverlay
            {
                Kind = ViewerOverlayKind.Rectangle,
                Name = "Die Position",
                Category = "die",
                Rect = dieRect
            });
        }

        if (TryGetProperty(root, "defectList", out var defects) && defects.ValueKind == JsonValueKind.Array)
        {
            int index = 1;
            foreach (var d in defects.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                JsonElement rectEl = default;
                bool hasRect = TryGetProperty(d, "Rect", out rectEl) || TryGetProperty(d, "rect", out rectEl) || TryGetProperty(d, "bbox", out rectEl);
                if (!hasRect || !TryReadRect(rectEl, out var r) || r.W <= 0 || r.H <= 0)
                {
                    metadata.Warnings.Add($"Legacy defect {index} has invalid Rect.");
                    continue;
                }

                var overlay = new ViewerOverlay
                {
                    Kind = ViewerOverlayKind.Rectangle,
                    Name = $"Defect {index}",
                    Category = "defect",
                    Rect = r
                };
                overlay.Attributes["Index"] = index.ToString(CultureInfo.InvariantCulture);
                overlay.Attributes["WidthUm"] = GetString(d, "WidthUm", "0");
                overlay.Attributes["HeightUm"] = GetString(d, "HeightUm", "0");
                overlay.Attributes["BinCode"] = GetString(d, "BinCode", "--");
                overlay.Attributes["AlgoID"] = GetString(d, "AlgoID", "");
                overlay.Attributes["AlgoIds"] = FormatAlgoIds(overlay.Attributes["AlgoID"]);
                metadata.Overlays.Add(overlay);
                index++;
            }
        }

        foreach (var pts in EnumerateLegacyPolygons(root))
        {
            var overlay = new ViewerOverlay
            {
                Kind = ViewerOverlayKind.Polygon,
                Name = $"Draw Polygon {metadata.Overlays.Count(o => o.Kind == ViewerOverlayKind.Polygon) + 1}",
                Category = "polygon"
            };
            overlay.Points.AddRange(pts);
            metadata.Overlays.Add(overlay);
        }
    }

    private static void MapLegacyProperty(ViewerMetadata metadata, JsonElement root, string from, string to)
    {
        if (TryGetProperty(root, from, out var value)) metadata.Properties[to] = ToDisplayString(value);
    }

    private static IEnumerable<List<ViewerPoint>> EnumerateLegacyPolygons(JsonElement root)
    {
        if (!TryGetProperty(root, "drawPolygon", out var arr) && !TryGetProperty(root, "drawPolygons", out arr)) yield break;
        if (arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in arr.EnumerateArray())
        {
            JsonElement pointsEl = item;
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(item, "points", out var p) || TryGetProperty(item, "nodes", out p) || TryGetProperty(item, "vertices", out p)) pointsEl = p;
            }
            var pts = new List<ViewerPoint>();
            ReadPointArray(pointsEl, pts);
            if (pts.Count >= 3) yield return pts;
        }
    }

    private static bool TryReadPointsFromObject(JsonElement item, List<ViewerPoint> points)
    {
        if (TryGetProperty(item, "points", out var pointsEl) || TryGetProperty(item, "vertices", out pointsEl) || TryGetProperty(item, "nodes", out pointsEl))
            return ReadPointArray(pointsEl, points);
        return false;
    }

    private static bool ReadPointArray(JsonElement pointsEl, List<ViewerPoint> points)
    {
        if (pointsEl.ValueKind == JsonValueKind.String)
        {
            foreach (var pair in pointsEl.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryReadPointString(pair, out var pt)) points.Add(pt);
            }
            return points.Count > 0;
        }
        if (pointsEl.ValueKind != JsonValueKind.Array) return false;
        foreach (var p in pointsEl.EnumerateArray())
            if (TryReadPoint(p, out var pt)) points.Add(pt);
        return points.Count > 0;
    }

    private static bool TryReadPointFromObject(JsonElement item, out ViewerPoint point)
    {
        if (TryReadNumberProperty(item, "x", out var x) && TryReadNumberProperty(item, "y", out var y))
        {
            point = new ViewerPoint(x, y);
            return true;
        }
        if (TryGetProperty(item, "point", out var p) && TryReadPoint(p, out point)) return true;
        point = default;
        return false;
    }

    private static bool TryReadPoint(JsonElement e, out ViewerPoint point)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            var nums = e.EnumerateArray().Select(v => TryReadDouble(v, out var d) ? (double?)d : null).Where(v => v.HasValue).Select(v => v!.Value).Take(2).ToArray();
            if (nums.Length >= 2)
            {
                point = new ViewerPoint(nums[0], nums[1]);
                return true;
            }
        }
        if (e.ValueKind == JsonValueKind.Object && TryReadPointFromObject(e, out point)) return true;
        if (e.ValueKind == JsonValueKind.String && TryReadPointString(e.GetString() ?? "", out point)) return true;
        point = default;
        return false;
    }

    private static bool TryReadPointString(string text, out ViewerPoint point)
    {
        var nums = ExtractNumbers(text).Take(2).ToArray();
        if (nums.Length >= 2)
        {
            point = new ViewerPoint(nums[0], nums[1]);
            return true;
        }
        point = default;
        return false;
    }

    private static bool TryReadRectFromObject(JsonElement item, out ViewerRect rect)
    {
        foreach (var name in new[] { "rect", "Rect", "bbox", "boundingBox" })
            if (TryGetProperty(item, name, out var r) && TryReadRect(r, out rect)) return true;

        if (TryReadNumberProperty(item, "x", out var x) && TryReadNumberProperty(item, "y", out var y))
        {
            if ((TryReadNumberProperty(item, "width", out var w) || TryReadNumberProperty(item, "w", out w)) &&
                (TryReadNumberProperty(item, "height", out var h) || TryReadNumberProperty(item, "h", out h)))
            {
                rect = new ViewerRect(x, y, w, h);
                return true;
            }
        }

        if (TryReadNumberProperty(item, "left", out var left) && TryReadNumberProperty(item, "top", out var top) &&
            TryReadNumberProperty(item, "right", out var right) && TryReadNumberProperty(item, "bottom", out var bottom))
        {
            rect = new ViewerRect(left, top, right - left, bottom - top);
            return true;
        }

        rect = default;
        return false;
    }

    private static bool TryReadRect(JsonElement e, out ViewerRect rect)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var nums = ExtractNumbers(e.GetString() ?? "").Take(4).ToArray();
            if (nums.Length >= 4)
            {
                rect = new ViewerRect(nums[0], nums[1], nums[2], nums[3]);
                return true;
            }
        }
        if (e.ValueKind == JsonValueKind.Array)
        {
            var nums = e.EnumerateArray().Select(v => TryReadDouble(v, out var d) ? (double?)d : null).Where(v => v.HasValue).Select(v => v!.Value).Take(4).ToArray();
            if (nums.Length >= 4)
            {
                rect = new ViewerRect(nums[0], nums[1], nums[2], nums[3]);
                return true;
            }
        }
        if (e.ValueKind == JsonValueKind.Object) return TryReadRectFromObject(e, out rect);
        rect = default;
        return false;
    }

    private static IEnumerable<double> ExtractNumbers(string text)
    {
        foreach (Match m in Regex.Matches(text, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?"))
        {
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                yield return d;
            }
        }
    }

    #endregion

    #region CSV and Key-Value Parsing

    private static void ParseCsv(string text, ViewerMetadata metadata)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return;
        var headers = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToList();
        for (int i = 1; i < lines.Count; i++)
        {
            var values = SplitCsvLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Count && c < values.Count; c++)
            {
                row[headers[c]] = values[c].Trim();
            }

            string type = GetRow(row, "type", GetRow(row, "kind", "rectangle")).ToLowerInvariant();
            var overlay = new ViewerOverlay
            {
                Kind = type.Contains("poly") ? ViewerOverlayKind.Polygon : ViewerOverlayKind.Rectangle,
                Name = GetRow(row, "name", GetRow(row, "label", $"CSV Object {i}")),
                Category = GetRow(row, "category", GetRow(row, "class", ""))
            };
            foreach (var kv in row)
            {
                overlay.Attributes[kv.Key] = kv.Value;
            }

            if (overlay.Kind == ViewerOverlayKind.Polygon)
            {
                var ptsText = GetRow(row, "points", "");
                foreach (var pair in ptsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryReadPointString(pair, out var pt))
                    {
                        overlay.Points.Add(pt);
                    }
                }

                if (overlay.Points.Count >= 3)
                {
                    metadata.Overlays.Add(overlay);
                }
                else
                {
                    metadata.Warnings.Add($"CSV row {i + 1}: polygon needs at least 3 points.");
                }
            }
            else if (TryReadRectFromRow(row, out var rect))
            {
                overlay.Rect = rect;
                metadata.Overlays.Add(overlay);
            }
            else
            {
                metadata.Warnings.Add($"CSV row {i + 1}: rectangle geometry not found.");
            }
        }
    }

    private static bool TryReadRectFromRow(Dictionary<string, string> row, out ViewerRect rect)
    {
        if (TryParseRowDouble(row, "x", out var x) && TryParseRowDouble(row, "y", out var y) &&
            (TryParseRowDouble(row, "width", out var w) || TryParseRowDouble(row, "w", out w)) &&
            (TryParseRowDouble(row, "height", out var h) || TryParseRowDouble(row, "h", out h)))
        {
            rect = new ViewerRect(x, y, w, h);
            return true;
        }
        foreach (var key in new[] { "rect", "bbox" })
        {
            if (!row.TryGetValue(key, out var text)) continue;
            var nums = ExtractNumbers(text).Take(4).ToArray();
            if (nums.Length >= 4)
            {
                rect = new ViewerRect(nums[0], nums[1], nums[2], nums[3]);
                return true;
            }
        }
        rect = default;
        return false;
    }

    private static bool TryParseRowDouble(Dictionary<string, string> row, string key, out double value)
    {
        if (row.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        value = 0;
        return false;
    }

    private static string GetRow(Dictionary<string, string> row, string key, string fallback) => row.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static void ParseKeyValueText(string text, ViewerMetadata metadata)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line, @"^\s*([A-Za-z0-9_ /.-]+)\s*[:=,]\s*(.+?)\s*$");
        if (m.Success) metadata.Properties[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
        }
    }

    #endregion

    #region Normalization and JSON Helpers

    private static void NormalizeSummary(ViewerMetadata metadata)
    {
        int rectangles = metadata.Overlays.Count(o => o.Kind == ViewerOverlayKind.Rectangle);
        int polygons = metadata.Overlays.Count(o => o.Kind == ViewerOverlayKind.Polygon);
        int points = metadata.Overlays.Count(o => o.Kind is ViewerOverlayKind.Point or ViewerOverlayKind.Crosshair);
        int defects = metadata.Overlays.Count(o => o.Kind == ViewerOverlayKind.Rectangle && o.Category.Equals("defect", StringComparison.OrdinalIgnoreCase));
        metadata.Properties["Objects"] = metadata.Overlays.Count.ToString(CultureInfo.InvariantCulture);
        metadata.Properties["Rectangles"] = rectangles.ToString(CultureInfo.InvariantCulture);
        metadata.Properties["Polygons"] = polygons.ToString(CultureInfo.InvariantCulture);
        metadata.Properties["Points"] = points.ToString(CultureInfo.InvariantCulture);
        metadata.Properties["Defects"] = defects.ToString(CultureInfo.InvariantCulture);
        metadata.Properties["Warnings"] = metadata.Warnings.Count.ToString(CultureInfo.InvariantCulture);
        if (!metadata.Properties.ContainsKey("Created")) metadata.Properties["Created"] = metadata.GetProperty("created", metadata.GetProperty("created_time", "--"));
        if (!metadata.Properties.ContainsKey("Result")) metadata.Properties["Result"] = metadata.GetProperty("result", metadata.GetProperty("detectResult", "--"));
        if (!metadata.Properties.ContainsKey("Recipe")) metadata.Properties["Recipe"] = metadata.GetProperty("recipe", metadata.GetProperty("recipe_path", "--"));
        if (!metadata.Properties.ContainsKey("Error")) metadata.Properties["Error"] = metadata.Warnings.Count == 0 ? "--" : string.Join("; ", metadata.Warnings.Take(3));
    }

    private static bool TryGetProperty(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryReadNumberProperty(JsonElement e, string name, out double value)
    {
        if (TryGetProperty(e, name, out var v) && TryReadDouble(v, out value)) return true;
        value = 0;
        return false;
    }

    private static string GetString(JsonElement e, string name, string fallback)
    {
        if (!TryGetProperty(e, name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString();
    }

    private static double GetDouble(JsonElement e, string name) => TryReadNumberProperty(e, name, out var d) ? d : 0;

    private static bool TryReadDouble(JsonElement e, out double value)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value)) return true;
        return double.TryParse(e.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ToDisplayString(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => e.ToString()
        };
    }

    private static string FormatAlgoIds(string algo)
    {
        if (!long.TryParse(algo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0) return "--";
        var ids = new List<string>();
        for (int i = 0; i < 63; i++)
        {
            if ((v & (1L << i)) != 0)
            {
                ids.Add("AlgoID:" + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        return ids.Count == 0 ? "--" : string.Join(", ", ids);
    }

    #endregion

    #region Embedded Metadata Readers

    private static bool TryReadEmbeddedMetadata(string file, out string json, out string source)
    {
        json = "";
        source = "";
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var len = new FileInfo(file).Length;
        if (ext is not ".jpg" and not ".jpeg" && len > 200L * 1024 * 1024) return false;
        byte[] bytes = File.ReadAllBytes(file);
        string? xmp = ReadGmmJsonFromJpgXmp(bytes);
        if (!string.IsNullOrWhiteSpace(xmp))
        {
            json = xmp;
            source = "Legacy XMP APP1 / AOIJson";
            return true;
        }

        string? appended = ReadOldAppendMetadata(bytes);
        if (!string.IsNullOrWhiteSpace(appended))
        {
            json = appended;
            source = "Legacy appended AOI block";
            return true;
        }

        return false;
    }

    private static string? ReadGmmJsonFromJpgXmp(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int pos = FindPattern(bytes, header, 0);
        if (pos < 0) return null;
        int xmpStart = pos + header.Length;
        int segStart = pos - 4;
        if (segStart < 0 || bytes[segStart] != 0xFF || bytes[segStart + 1] != 0xE1) return null;
        int segLength = (bytes[segStart + 2] << 8) | bytes[segStart + 3];
        int segEnd = segStart + 2 + segLength;
        if (segEnd > bytes.Length || xmpStart >= segEnd) return null;
        string xmp = Encoding.UTF8.GetString(bytes, xmpStart, segEnd - xmpStart);
        foreach (var tag in new[] { "AOIJson", "MetadataJson", "OverlayJson" })
        {
            var m = Regex.Match(xmp, $"<[^>]*:{tag}><!\\[CDATA\\[(.*?)\\]\\]></[^>]*:{tag}>", RegexOptions.Singleline);
            if (m.Success) return m.Groups[1].Value.Replace("]]><![CDATA[", "").Trim();
        }
        const string beginTag = "<gmm:AOIJson><![CDATA[";
        const string endTag = "]]></gmm:AOIJson>";
        int b = xmp.IndexOf(beginTag, StringComparison.Ordinal);
        if (b < 0) return null;
        b += beginTag.Length;
        int e = xmp.IndexOf(endTag, b, StringComparison.Ordinal);
        if (e < 0) return null;
        return xmp.Substring(b, e - b).Replace("]]]]><![CDATA[>", "]]>").Trim();
    }

    private static string? ReadOldAppendMetadata(byte[] bytes)
    {
        byte[] begin = Encoding.UTF8.GetBytes("\nAOI_BEGIN\n");
        byte[] end = Encoding.UTF8.GetBytes("\nAOI_END\n");
        int s = FindPattern(bytes, begin, 0);
        if (s < 0) return null;
        s += begin.Length;
        int e = FindPattern(bytes, end, s);
        if (e < 0 || e <= s) return null;
        return Encoding.UTF8.GetString(bytes, s, e - s);
    }

    private static int FindPattern(byte[] data, byte[] pattern, int start)
    {
        for (int i = Math.Max(0, start); i <= data.Length - pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok) return i;
        }
        return -1;
    }

    #endregion
}

#endregion
