using System.Globalization;

namespace CwsEditor.Core;

public sealed record StripLayoutEntry(
    string ImageFileName,
    int Width,
    int Height,
    int XOffset,
    int YOffset);

public sealed record CwsStrip(
    int Index,
    string ImageEntryName,
    string ThumbEntryName,
    string ImageFileName,
    int Width,
    int Height,
    int XOffset,
    int YOffset,
    byte[]? ThumbBytes);

public sealed record CwsPassthroughEntry(string EntryName, byte[] Data);

public sealed record DepthSample(DateTimeOffset TimestampUtc, double Depth, double Orientation)
{
    public static DepthSample Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
        {
            throw new CwsEditorException($"Depth.txt line is invalid: {line}");
        }

        string stamp = $"{parts[0]} {parts[1]}";
        if (!DateTime.TryParseExact(
                stamp,
                "dd/MM/yy HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            throw new CwsEditorException($"Depth.txt timestamp is invalid: {stamp}");
        }

        if (!double.TryParse(parts[2], CultureInfo.InvariantCulture, out double depth))
        {
            throw new CwsEditorException($"Depth.txt depth is invalid: {line}");
        }

        if (!double.TryParse(parts[3], CultureInfo.InvariantCulture, out double orientation))
        {
            throw new CwsEditorException($"Depth.txt orientation is invalid: {line}");
        }

        return new DepthSample(
            new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)),
            depth,
            orientation);
    }
}

public sealed record DisplacementSample(
    double RegionX,
    double RegionY,
    double RegionWidth,
    double RegionHeight,
    DateTimeOffset JobTimeUtc,
    double DisplacementX,
    double DisplacementY);

public sealed record MovementVector(double X, double Y);

public sealed record DepthInfo(DateTimeOffset TimestampUtc, double Depth, double Orientation)
{
    public double FileOrientation => Orientation;
}

public enum SourceDepthUnit
{
    Meters = 0,
    Feet = 1,
}

public enum DisplayDepthUnit
{
    Metric = 0,
    Imperial = 1,
}

public static class DepthDisplayConverter
{
    private const double FeetPerMeter = 3.28083989501312d;

    public static double ConvertDepth(double rawDepth, SourceDepthUnit sourceUnit, DisplayDepthUnit displayUnit)
    {
        double metricDepth = sourceUnit switch
        {
            SourceDepthUnit.Feet => rawDepth / FeetPerMeter,
            _ => rawDepth,
        };

        return displayUnit switch
        {
            DisplayDepthUnit.Imperial => metricDepth * FeetPerMeter,
            _ => metricDepth,
        };
    }

    public static string GetUnitLabel(DisplayDepthUnit displayUnit) =>
        displayUnit == DisplayDepthUnit.Imperial ? "ft" : "m";
}

public sealed record ToneAdjustment(double Brightness, double Contrast, double Sharpness, bool NormalizeEnabled)
{
    public static ToneAdjustment Identity { get; } = new(0d, 0d, 0d, false);

    public bool IsIdentity =>
        Math.Abs(Brightness) < 0.0001d &&
        Math.Abs(Contrast) < 0.0001d &&
        Math.Abs(Sharpness) < 0.0001d &&
        !NormalizeEnabled;

    public ToneAdjustment Combine(ToneAdjustment other) =>
        new(
            Brightness + other.Brightness,
            Contrast + other.Contrast,
            Sharpness + other.Sharpness,
            NormalizeEnabled || other.NormalizeEnabled);
}

public enum RegionGeometryMode
{
    None = 0,
    Scale = 1,
    Crop = 2,
}

public sealed record EditRegion(
    Guid Id,
    string Name,
    double StartSourceY,
    double EndSourceY,
    RegionGeometryMode GeometryMode,
    double? VerticalScale,
    ToneAdjustment Tone)
{
    public bool IsEnabled { get; init; } = true;

    public double NormalizedStart => Math.Min(StartSourceY, EndSourceY);

    public double NormalizedEnd => Math.Max(StartSourceY, EndSourceY);

    public bool HasScaleEdit => GeometryMode == RegionGeometryMode.Scale && VerticalScale.HasValue;

    public bool IsCrop => GeometryMode == RegionGeometryMode.Crop;

    public bool Intersects(EditRegion other) =>
        NormalizedStart < other.NormalizedEnd &&
        other.NormalizedStart < NormalizedEnd;

    public bool Contains(double sourceY) => sourceY >= NormalizedStart && sourceY < NormalizedEnd;

    public EditRegion WithScale(double verticalScale) =>
        this with
        {
            GeometryMode = RegionGeometryMode.Scale,
            VerticalScale = verticalScale,
        };

    public EditRegion WithCrop() =>
        this with
        {
            GeometryMode = RegionGeometryMode.Crop,
            VerticalScale = null,
        };

    public EditRegion ClearGeometry() =>
        this with
        {
            GeometryMode = RegionGeometryMode.None,
            VerticalScale = null,
        };

    public EditRegion WithEnabled(bool isEnabled) => this with { IsEnabled = isEnabled };

    public EditRegion WithTone(ToneAdjustment tone) => this with { Tone = tone };
}

public sealed class EditSession
{
    private readonly List<EditRegion> _regions = [];
    private double _globalVerticalScale = 1d;
    private double? _cachedWarpSourceHeight;
    private string? _cachedGeometrySignature;
    private WarpMap? _cachedWarpMap;

    public double GlobalVerticalScale
    {
        get => _globalVerticalScale;
        set
        {
            _globalVerticalScale = value;
            InvalidateWarpCache();
        }
    }

    public ToneAdjustment GlobalTone { get; set; } = ToneAdjustment.Identity;

    public IReadOnlyList<EditRegion> Regions => _regions;

    public WarpMap BuildWarpMap(double sourceHeight)
    {
        string signature = BuildGeometrySignature();
        if (_cachedWarpMap is not null &&
            _cachedWarpSourceHeight.HasValue &&
            Math.Abs(_cachedWarpSourceHeight.Value - sourceHeight) < 0.0001d &&
            string.Equals(_cachedGeometrySignature, signature, StringComparison.Ordinal))
        {
            return _cachedWarpMap;
        }

        WarpMap map = WarpMap.Build(sourceHeight, GlobalVerticalScale, GetActiveRegions());
        _cachedWarpMap = map;
        _cachedWarpSourceHeight = sourceHeight;
        _cachedGeometrySignature = signature;
        return map;
    }

    public IReadOnlyList<EditRegion> GetOrderedRegions(bool includeDisabled = false) =>
        (includeDisabled ? _regions : GetActiveRegions())
            .OrderBy(region => region.NormalizedStart)
            .ToArray();

    public void UpsertRegion(EditRegion region)
    {
        string before = BuildGeometrySignature();
        region = region with
        {
            StartSourceY = region.NormalizedStart,
            EndSourceY = region.NormalizedEnd,
        };

        _regions.RemoveAll(existing => existing.Intersects(region) || existing.Id == region.Id);
        _regions.Add(region);
        if (!string.Equals(before, BuildGeometrySignature(), StringComparison.Ordinal))
        {
            InvalidateWarpCache();
        }
    }

    public void RemoveRegion(Guid regionId)
    {
        string before = BuildGeometrySignature();
        _regions.RemoveAll(region => region.Id == regionId);
        if (!string.Equals(before, BuildGeometrySignature(), StringComparison.Ordinal))
        {
            InvalidateWarpCache();
        }
    }

    public double ResolveVerticalScale(double sourceY)
    {
        EditRegion? region = GetActiveRegions().LastOrDefault(candidate => candidate.HasScaleEdit && candidate.Contains(sourceY));
        return region?.VerticalScale ?? GlobalVerticalScale;
    }

    public ToneAdjustment ResolveTone(double sourceY)
    {
        ToneAdjustment adjustment = GlobalTone;
        EditRegion? region = GetActiveRegions().LastOrDefault(candidate => candidate.Contains(sourceY));
        if (region is not null)
        {
            adjustment = adjustment.Combine(region.Tone);
        }

        return adjustment;
    }

    private void InvalidateWarpCache()
    {
        _cachedWarpSourceHeight = null;
        _cachedGeometrySignature = null;
        _cachedWarpMap = null;
    }

    private string BuildGeometrySignature()
    {
        string scaleText = GlobalVerticalScale.ToString("G17", CultureInfo.InvariantCulture);
        IEnumerable<string> regionParts = GetActiveRegions()
            .Where(region => region.HasScaleEdit || region.IsCrop)
            .OrderBy(region => region.NormalizedStart)
            .ThenBy(region => region.NormalizedEnd)
            .Select(region =>
                $"{region.NormalizedStart.ToString("G17", CultureInfo.InvariantCulture)}:" +
                $"{region.NormalizedEnd.ToString("G17", CultureInfo.InvariantCulture)}:" +
                $"{region.GeometryMode}:" +
                $"{(region.VerticalScale?.ToString("G17", CultureInfo.InvariantCulture) ?? "null")}");
        return string.Join("|", [scaleText, .. regionParts]);
    }

    private IEnumerable<EditRegion> GetActiveRegions() => _regions.Where(region => region.IsEnabled);
}

public sealed record WarpSegment(
    double SourceStart,
    double SourceEnd,
    double Scale,
    double DisplayStart,
    double DisplayEnd)
{
    public bool IntersectsDisplay(double displayStart, double displayEnd) =>
        DisplayStart < displayEnd && displayStart < DisplayEnd;
}

public sealed class WarpMap
{
    private readonly IReadOnlyList<WarpSegment> _segments;

    private WarpMap(IReadOnlyList<WarpSegment> segments)
    {
        _segments = segments;
    }

    public IReadOnlyList<WarpSegment> Segments => _segments;

    public double TotalDisplayHeight => _segments.Count == 0 ? 0d : _segments[^1].DisplayEnd;

    public static WarpMap Build(double sourceHeight, double globalScale, IEnumerable<EditRegion> regions)
    {
        if (sourceHeight <= 0)
        {
            throw new CwsEditorException("Source height must be positive.");
        }

        double safeGlobalScale = SanitizeScale(globalScale);
        SortedSet<double> boundaries = [0d, sourceHeight];

        foreach (EditRegion region in regions)
        {
            if (!region.HasScaleEdit && !region.IsCrop)
            {
                continue;
            }

            boundaries.Add(Math.Clamp(region.NormalizedStart, 0d, sourceHeight));
            boundaries.Add(Math.Clamp(region.NormalizedEnd, 0d, sourceHeight));
        }

        double displayCursor = 0d;
        List<WarpSegment> segments = [];
        double[] orderedBoundaries = boundaries.OrderBy(value => value).ToArray();
        for (int index = 0; index < orderedBoundaries.Length - 1; index++)
        {
            double start = orderedBoundaries[index];
            double end = orderedBoundaries[index + 1];
            if (end <= start)
            {
                continue;
            }

            double midpoint = start + ((end - start) / 2d);
            EditRegion? region = regions.LastOrDefault(candidate => (candidate.HasScaleEdit || candidate.IsCrop) && candidate.Contains(midpoint));
            if (region?.IsCrop == true)
            {
                continue;
            }

            double scale = SanitizeScale(region?.VerticalScale ?? safeGlobalScale);
            double displayEnd = displayCursor + ((end - start) * scale);
            segments.Add(new WarpSegment(start, end, scale, displayCursor, displayEnd));
            displayCursor = displayEnd;
        }

        if (segments.Count == 0)
        {
            segments.Add(new WarpSegment(0d, sourceHeight, safeGlobalScale, 0d, sourceHeight * safeGlobalScale));
        }

        return new WarpMap(segments);
    }

    public double Forward(double sourceY)
    {
        WarpSegment segment = FindSegmentBySource(sourceY);
        return segment.DisplayStart + ((Math.Clamp(sourceY, segment.SourceStart, segment.SourceEnd) - segment.SourceStart) * segment.Scale);
    }

    public double Inverse(double displayY)
    {
        WarpSegment segment = FindSegmentByDisplay(displayY);
        if (Math.Abs(segment.Scale) < 0.000001d)
        {
            return segment.SourceStart;
        }

        return segment.SourceStart + ((Math.Clamp(displayY, segment.DisplayStart, segment.DisplayEnd) - segment.DisplayStart) / segment.Scale);
    }

    public IReadOnlyList<WarpSegment> GetDisplaySegments(double displayStart, double displayEnd) =>
        _segments.Where(segment => segment.IntersectsDisplay(displayStart, displayEnd)).ToArray();

    private WarpSegment FindSegmentBySource(double sourceY)
    {
        if (sourceY <= 0d)
        {
            return _segments[0];
        }

        if (sourceY >= _segments[^1].SourceEnd)
        {
            return _segments[^1];
        }

        int low = 0;
        int high = _segments.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            WarpSegment segment = _segments[mid];
            if (sourceY < segment.SourceStart)
            {
                high = mid - 1;
            }
            else if (sourceY >= segment.SourceEnd)
            {
                low = mid + 1;
            }
            else
            {
                return segment;
            }
        }

        return _segments[Math.Clamp(low, 0, _segments.Count - 1)];
    }

    private WarpSegment FindSegmentByDisplay(double displayY)
    {
        if (displayY <= 0d)
        {
            return _segments[0];
        }

        if (displayY >= _segments[^1].DisplayEnd)
        {
            return _segments[^1];
        }

        int low = 0;
        int high = _segments.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            WarpSegment segment = _segments[mid];
            if (displayY < segment.DisplayStart)
            {
                high = mid - 1;
            }
            else if (displayY >= segment.DisplayEnd)
            {
                low = mid + 1;
            }
            else
            {
                return segment;
            }
        }

        return _segments[Math.Clamp(low, 0, _segments.Count - 1)];
    }

    private static double SanitizeScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return 1d;
        }

        return Math.Clamp(scale, 0.1d, 10d);
    }
}

public sealed class CwsDocument
{
    public CwsDocument(
        string sourcePath,
        IReadOnlyList<CwsStrip> strips,
        IReadOnlyList<DepthSample> depthSamples,
        string telemetryText,
        IReadOnlyDictionary<string, CwsPassthroughEntry> passthroughEntries,
        IReadOnlyList<string> entryOrder,
        StitchMetadata stitchMetadata,
        int compositeWidth,
        int standardStripHeight,
        int stripStride,
        int thumbnailWidth)
    {
        SourcePath = sourcePath;
        Strips = strips;
        DepthSamples = depthSamples;
        TelemetryText = telemetryText;
        PassthroughEntries = passthroughEntries;
        EntryOrder = entryOrder;
        StitchMetadata = stitchMetadata;
        CompositeWidth = compositeWidth;
        StandardStripHeight = standardStripHeight;
        StripStride = stripStride;
        StripOverlap = Math.Max(0, standardStripHeight - stripStride);
        ThumbnailWidth = thumbnailWidth;

        SourceHeight = Strips.Count == 0 ? 0d : Strips.Max(strip => strip.YOffset + strip.Height);
        DisplacementStride = StitchMetadata.DisplacementStride;
        DisplacementRegionHeight = StitchMetadata.DisplacementRegionHeight;
        DisplacementOverscan = StitchMetadata.DisplacementOverscan;
    }

    public string SourcePath { get; }

    public IReadOnlyList<CwsStrip> Strips { get; }

    public IReadOnlyList<DepthSample> DepthSamples { get; }

    public string TelemetryText { get; }

    public IReadOnlyDictionary<string, CwsPassthroughEntry> PassthroughEntries { get; }

    public IReadOnlyList<string> EntryOrder { get; }

    public StitchMetadata StitchMetadata { get; }

    public int CompositeWidth { get; }

    public double SourceHeight { get; }

    public int StandardStripHeight { get; }

    public int StripStride { get; }

    public int StripOverlap { get; }

    public int ThumbnailWidth { get; }

    public int DisplacementStride { get; }

    public int DisplacementRegionHeight { get; }

    public double DisplacementOverscan { get; }
}

public sealed record SaveProgress(int Completed, int Total, string Stage);
