using ImageMagick;

namespace CwsEditor.Core;

public sealed class CwsRenderService : IDisposable
{
    private const int RenderTileDisplayHeight = 1024;

    private readonly CwsDocument _document;
    private readonly StripImageCache _cache;
    private readonly long _renderTileCacheByteLimit;
    private readonly bool _enableFastPath;
    private readonly object _renderTileCacheSync = new();
    private readonly Dictionary<RenderTileKey, LinkedListNode<RenderTileCacheItem>> _renderTileNodes = [];
    private readonly LinkedList<RenderTileCacheItem> _renderTileLru = [];
    private long _renderTileCacheBytes;
    private long _renderTileCacheHits;
    private long _renderTileCacheMisses;

    public CwsRenderService(
        CwsDocument document,
        int stripCacheCapacity = 24,
        long renderTileCacheByteLimit = 512L * 1024L * 1024L,
        bool enableFastPath = true)
    {
        _document = document;
        _cache = new StripImageCache(document, stripCacheCapacity);
        _renderTileCacheByteLimit = Math.Max(0L, renderTileCacheByteLimit);
        _enableFastPath = enableFastPath;
    }

    public RenderCacheStats CacheStats => new(
        Interlocked.Read(ref _renderTileCacheHits),
        Interlocked.Read(ref _renderTileCacheMisses),
        Interlocked.Read(ref _renderTileCacheBytes));

    public Task<MagickImage> RenderViewportImageAsync(
        EditSession session,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken = default,
        bool useRenderTileCache = true) =>
        RenderViewportImageAsync(
            session,
            session.BuildWarpMap(_document.SourceHeight),
            displayStart,
            displayHeight,
            zoom,
            cancellationToken,
            useRenderTileCache);

    public Task<MagickImage> RenderViewportImageAsync(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken = default,
        bool useRenderTileCache = true)
    {
        return Task.Run(
            () => RenderViewportImage(session, warp, displayStart, displayHeight, zoom, cancellationToken, useRenderTileCache),
            cancellationToken);
    }

    public MagickImage RenderViewportImage(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken = default,
        bool useRenderTileCache = true) =>
        RenderViewportImageCore(session, warp, displayStart, displayHeight, zoom, cancellationToken, useRenderTileCache);

    public Task<MagickImage> RenderOverviewImageAsync(
        EditSession session,
        double overviewZoom,
        CancellationToken cancellationToken = default) =>
        RenderOverviewImageAsync(
            session,
            session.BuildWarpMap(_document.SourceHeight),
            overviewZoom,
            cancellationToken);

    public Task<MagickImage> RenderOverviewImageAsync(
        EditSession session,
        WarpMap warp,
        double overviewZoom,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RenderOverview(session, warp, overviewZoom, cancellationToken), cancellationToken);
    }

    public MagickImage RenderOverviewImage(
        EditSession session,
        WarpMap warp,
        double overviewZoom,
        CancellationToken cancellationToken = default) =>
        RenderOverview(session, warp, overviewZoom, cancellationToken);

    public void Dispose()
    {
        ClearRenderTileCache();
        _cache.Dispose();
    }

    public Task PrefetchViewportAsync(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                double displayEnd = Math.Min(warp.TotalDisplayHeight, displayStart + Math.Max(1d, displayHeight));
                double sourceStart = Math.Max(0d, warp.Inverse(displayStart));
                double sourceEnd = Math.Min(_document.SourceHeight, warp.Inverse(displayEnd));
                double sourcePadding = Math.Max(1d, displayHeight * 0.75d);
                double prefetchStart = Math.Max(0d, sourceStart - sourcePadding);
                double prefetchEnd = Math.Min(_document.SourceHeight, sourceEnd + sourcePadding);

                foreach (CwsStrip strip in _document.GetStripsOverlapping(prefetchStart, prefetchEnd))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _cache.PrefetchFullStrip(strip);
                }
            },
            cancellationToken);
    }

    private MagickImage RenderViewportImageCore(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken,
        bool useRenderTileCache)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderWarpedBaseImage(session, warp, displayStart, displayHeight, useThumbnails: false, 1d, cancellationToken, useRenderTileCache);
    }

    private MagickImage RenderOverview(EditSession session, WarpMap warp, double overviewZoom, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double thumbScale = (_document.ThumbnailWidth / (double)_document.CompositeWidth) * Math.Clamp(overviewZoom, 0.2d, 5d);
        return RenderWarpedBaseImage(session, warp, 0d, warp.TotalDisplayHeight, useThumbnails: true, thumbScale, cancellationToken, useRenderTileCache: false);
    }

    private MagickImage RenderWarpedBaseImage(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        bool useThumbnails,
        double verticalScaleFactor,
        CancellationToken cancellationToken,
        bool useRenderTileCache)
    {
        if (useRenderTileCache && !useThumbnails && Math.Abs(verticalScaleFactor - 1d) < 0.0001d && _renderTileCacheByteLimit > 0)
        {
            return RenderViewportFromTiles(session, warp, displayStart, displayHeight, cancellationToken);
        }

        return RenderWarpedBaseImageUncached(session, warp, displayStart, displayHeight, useThumbnails, verticalScaleFactor, cancellationToken);
    }

    private MagickImage RenderWarpedBaseImageUncached(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        bool useThumbnails,
        double verticalScaleFactor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double displayEnd = Math.Min(warp.TotalDisplayHeight, displayStart + displayHeight);
        if (displayEnd <= displayStart)
        {
            displayEnd = Math.Min(warp.TotalDisplayHeight, displayStart + 1d);
        }

        if (_enableFastPath && CanRenderDirectIdentity(session, warp, useThumbnails, verticalScaleFactor))
        {
            return RenderDirectIdentity(displayStart, displayEnd, cancellationToken);
        }

        int outputWidth = useThumbnails ? _document.ThumbnailWidth : _document.CompositeWidth;
        int outputHeight = Math.Max(
            1,
            (int)Math.Ceiling((displayEnd - displayStart) * Math.Max(0.0001d, verticalScaleFactor)));
        MagickImage canvas = new(MagickColors.Transparent, (uint)outputWidth, (uint)outputHeight);

        foreach (RenderSlice slice in BuildRenderSlices(session, warp, displayStart, displayEnd))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using MagickImage sourceSlice = ComposeSourceSlice(slice.SourceStart, slice.SourceEnd, useThumbnails);
            ApplyTone(sourceSlice, slice.Tone);

            int destinationTop = Math.Max(
                0,
                Math.Min(outputHeight - 1, (int)Math.Floor((slice.DisplayStart - displayStart) * verticalScaleFactor)));
            int destinationBottom = Math.Max(
                destinationTop + 1,
                Math.Min(outputHeight, (int)Math.Ceiling((slice.DisplayEnd - displayStart) * verticalScaleFactor)));
            int destinationHeight = Math.Max(1, destinationBottom - destinationTop);

            if (sourceSlice.Height == destinationHeight)
            {
                canvas.Composite(sourceSlice, 0, destinationTop, CompositeOperator.Over);
            }
            else
            {
                using MagickImage resized = (MagickImage)sourceSlice.Clone();
                MagickGeometry geometry = new((uint)sourceSlice.Width, (uint)destinationHeight)
                {
                    IgnoreAspectRatio = true,
                };
                resized.Resize(geometry);
                canvas.Composite(resized, 0, destinationTop, CompositeOperator.Over);
            }
        }

        return canvas;
    }

    private MagickImage RenderViewportFromTiles(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double displayEnd = Math.Min(warp.TotalDisplayHeight, displayStart + displayHeight);
        if (displayEnd <= displayStart)
        {
            displayEnd = Math.Min(warp.TotalDisplayHeight, displayStart + 1d);
        }

        int outputHeight = Math.Max(1, (int)Math.Ceiling(displayEnd - displayStart));
        MagickImage canvas = new(MagickColors.Transparent, (uint)_document.CompositeWidth, (uint)outputHeight);
        string renderSignature = session.BuildRenderSignature(_document.SourceHeight);
        int firstTile = Math.Max(0, (int)Math.Floor(displayStart / RenderTileDisplayHeight));
        int lastTile = Math.Max(firstTile, (int)Math.Floor(Math.Max(displayStart, displayEnd - 0.0001d) / RenderTileDisplayHeight));

        for (int tileIndex = firstTile; tileIndex <= lastTile; tileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double tileStart = tileIndex * RenderTileDisplayHeight;
            double tileEnd = Math.Min(warp.TotalDisplayHeight, tileStart + RenderTileDisplayHeight);
            if (tileEnd <= tileStart)
            {
                continue;
            }

            double intersectionStart = Math.Max(displayStart, tileStart);
            double intersectionEnd = Math.Min(displayEnd, tileEnd);
            if (intersectionEnd <= intersectionStart)
            {
                continue;
            }

            using MagickImage tile = GetOrRenderTile(session, warp, renderSignature, tileIndex, tileStart, tileEnd - tileStart, cancellationToken);
            int cropY = Math.Max(0, (int)Math.Floor(intersectionStart - tileStart));
            int cropHeight = Math.Max(1, Math.Min((int)tile.Height - cropY, (int)Math.Ceiling(intersectionEnd - intersectionStart)));
            if (cropY >= tile.Height || cropHeight <= 0)
            {
                continue;
            }

            using MagickImage cropped = (MagickImage)tile.CloneArea(0, cropY, tile.Width, (uint)cropHeight);
            int destinationY = Math.Max(0, (int)Math.Round(intersectionStart - displayStart, MidpointRounding.AwayFromZero));
            canvas.Composite(cropped, 0, destinationY, CompositeOperator.Over);
        }

        return canvas;
    }

    private MagickImage GetOrRenderTile(
        EditSession session,
        WarpMap warp,
        string renderSignature,
        int tileIndex,
        double tileStart,
        double tileHeight,
        CancellationToken cancellationToken)
    {
        RenderTileKey key = new(renderSignature, tileIndex);
        lock (_renderTileCacheSync)
        {
            if (_renderTileNodes.TryGetValue(key, out LinkedListNode<RenderTileCacheItem>? existing))
            {
                _renderTileLru.Remove(existing);
                _renderTileLru.AddFirst(existing);
                Interlocked.Increment(ref _renderTileCacheHits);
                return (MagickImage)existing.Value.Image.Clone();
            }
        }

        Interlocked.Increment(ref _renderTileCacheMisses);
        MagickImage rendered = RenderWarpedBaseImageUncached(session, warp, tileStart, tileHeight, useThumbnails: false, 1d, cancellationToken);
        long byteCost = EstimateImageByteCost(rendered);
        if (byteCost <= _renderTileCacheByteLimit)
        {
            lock (_renderTileCacheSync)
            {
                if (_renderTileNodes.TryGetValue(key, out LinkedListNode<RenderTileCacheItem>? existing))
                {
                    _renderTileLru.Remove(existing);
                    _renderTileLru.AddFirst(existing);
                    rendered.Dispose();
                    return (MagickImage)existing.Value.Image.Clone();
                }

                MagickImage cached = (MagickImage)rendered.Clone();
                LinkedListNode<RenderTileCacheItem> node = new(new RenderTileCacheItem(key, cached, byteCost));
                _renderTileLru.AddFirst(node);
                _renderTileNodes[key] = node;
                _renderTileCacheBytes += byteCost;
                TrimRenderTileCache();
            }
        }

        return rendered;
    }

    private static long EstimateImageByteCost(MagickImage image) =>
        checked((long)image.Width * image.Height * 4L);

    private void TrimRenderTileCache()
    {
        while (_renderTileCacheBytes > _renderTileCacheByteLimit && _renderTileLru.Last is LinkedListNode<RenderTileCacheItem> tail)
        {
            _renderTileLru.RemoveLast();
            _renderTileNodes.Remove(tail.Value.Key);
            _renderTileCacheBytes -= tail.Value.ByteCost;
            tail.Value.Image.Dispose();
        }
    }

    private void ClearRenderTileCache()
    {
        lock (_renderTileCacheSync)
        {
            foreach (RenderTileCacheItem item in _renderTileLru)
            {
                item.Image.Dispose();
            }

            _renderTileLru.Clear();
            _renderTileNodes.Clear();
            _renderTileCacheBytes = 0;
        }
    }

    private bool CanRenderDirectIdentity(EditSession session, WarpMap warp, bool useThumbnails, double verticalScaleFactor)
    {
        if (useThumbnails || Math.Abs(verticalScaleFactor - 1d) > 0.0001d || session.HasAnyEdits || warp.Segments.Count != 1)
        {
            return false;
        }

        WarpSegment segment = warp.Segments[0];
        return Math.Abs(segment.SourceStart) < 0.0001d &&
               Math.Abs(segment.DisplayStart) < 0.0001d &&
               Math.Abs(segment.Scale - 1d) < 0.0001d &&
               Math.Abs(segment.SourceEnd - _document.SourceHeight) < 0.0001d;
    }

    private MagickImage RenderDirectIdentity(double displayStart, double displayEnd, CancellationToken cancellationToken)
    {
        int outputHeight = Math.Max(1, (int)Math.Ceiling(displayEnd - displayStart));
        MagickImage canvas = new(MagickColors.Transparent, (uint)_document.CompositeWidth, (uint)outputHeight);

        foreach (CwsStrip strip in _document.GetStripsOverlapping(displayStart, displayEnd))
        {
            cancellationToken.ThrowIfCancellationRequested();
            double stripStart = strip.YOffset;
            double stripEnd = strip.YOffset + strip.Height;
            double intersectionStart = Math.Max(displayStart, stripStart);
            double intersectionEnd = Math.Min(displayEnd, stripEnd);
            int cropY = Math.Max(0, (int)Math.Floor(intersectionStart - stripStart));
            int cropHeight = Math.Max(1, (int)Math.Ceiling(intersectionEnd - intersectionStart));
            using MagickImage source = _cache.GetFullStrip(strip);
            if (cropY >= source.Height)
            {
                continue;
            }

            cropHeight = Math.Min(cropHeight, (int)source.Height - cropY);
            if (cropHeight <= 0)
            {
                continue;
            }

            using MagickImage cropped = (MagickImage)source.CloneArea(0, cropY, source.Width, (uint)cropHeight);
            int destinationY = Math.Max(0, (int)Math.Round(intersectionStart - displayStart, MidpointRounding.AwayFromZero));
            canvas.Composite(cropped, 0, destinationY, CompositeOperator.Over);
        }

        return canvas;
    }

    private IReadOnlyList<RenderSlice> BuildRenderSlices(EditSession session, WarpMap warp, double displayStart, double displayEnd)
    {
        SortedSet<double> boundaries = [displayStart, displayEnd];
        foreach (WarpSegment segment in warp.GetDisplaySegments(displayStart, displayEnd))
        {
            boundaries.Add(Math.Max(displayStart, segment.DisplayStart));
            boundaries.Add(Math.Min(displayEnd, segment.DisplayEnd));
        }

        foreach (EditRegion region in session.GetOrderedRegions().Where(region => !region.IsCrop))
        {
            double regionDisplayStart = warp.Forward(region.NormalizedStart);
            double regionDisplayEnd = warp.Forward(region.NormalizedEnd);
            if (regionDisplayEnd <= displayStart || regionDisplayStart >= displayEnd)
            {
                continue;
            }

            boundaries.Add(Math.Max(displayStart, regionDisplayStart));
            boundaries.Add(Math.Min(displayEnd, regionDisplayEnd));
        }

        double[] ordered = boundaries.OrderBy(value => value).ToArray();
        List<RenderSlice> slices = [];
        for (int index = 0; index < ordered.Length - 1; index++)
        {
            double start = ordered[index];
            double end = ordered[index + 1];
            if (end - start < 0.01d)
            {
                continue;
            }

            double midpoint = start + ((end - start) / 2d);
            double sourceStart = warp.Inverse(start);
            double sourceEnd = warp.Inverse(end);
            ToneAdjustment tone = session.ResolveTone(warp.Inverse(midpoint));
            slices.Add(new RenderSlice(sourceStart, sourceEnd, start, end, tone));
        }

        return slices;
    }

    private MagickImage ComposeSourceSlice(double sourceStart, double sourceEnd, bool useThumbnails)
    {
        double safeStart = Math.Max(0d, sourceStart);
        double safeEnd = Math.Max(safeStart + 0.01d, sourceEnd);
        double scaleFactor = useThumbnails ? _document.ThumbnailWidth / (double)_document.CompositeWidth : 1d;
        int width = useThumbnails ? _document.ThumbnailWidth : _document.CompositeWidth;
        int height = Math.Max(1, (int)Math.Ceiling((safeEnd - safeStart) * scaleFactor));

        MagickImage canvas = new(MagickColors.Transparent, (uint)width, (uint)height);
        foreach (CwsStrip strip in _document.GetStripsOverlapping(safeStart, safeEnd))
        {
            double stripStart = strip.YOffset;
            double stripEnd = strip.YOffset + strip.Height;
            if (stripEnd <= safeStart || stripStart >= safeEnd)
            {
                continue;
            }

            double intersectionStart = Math.Max(safeStart, stripStart);
            double intersectionEnd = Math.Min(safeEnd, stripEnd);
            int cropY = Math.Max(0, (int)Math.Floor((intersectionStart - stripStart) * scaleFactor));
            int cropHeight = Math.Max(1, (int)Math.Ceiling((intersectionEnd - intersectionStart) * scaleFactor));
            using MagickImage source = useThumbnails ? _cache.GetThumbStrip(strip) : _cache.GetFullStrip(strip);
            if (cropY >= source.Height)
            {
                continue;
            }

            cropHeight = Math.Min(cropHeight, (int)source.Height - cropY);
            if (cropHeight <= 0)
            {
                continue;
            }

            using MagickImage cropped = (MagickImage)source.CloneArea(0, cropY, (uint)width, (uint)cropHeight);
            int destinationY = Math.Max(0, (int)Math.Round((intersectionStart - safeStart) * scaleFactor, MidpointRounding.AwayFromZero));
            canvas.Composite(cropped, 0, destinationY, CompositeOperator.Over);
        }

        return canvas;
    }

    private static void ApplyTone(MagickImage image, ToneAdjustment tone)
    {
        if (tone.IsIdentity)
        {
            return;
        }

        if (tone.NormalizeEnabled)
        {
            image.Normalize();
        }

        if (Math.Abs(tone.Brightness) > 0.001d || Math.Abs(tone.Contrast) > 0.001d)
        {
            image.BrightnessContrast(new Percentage(tone.Brightness), new Percentage(tone.Contrast));
        }

        if (tone.Sharpness > 0.001d)
        {
            double sigma = Math.Clamp(tone.Sharpness / 50d, 0.1d, 8d);
            image.Sharpen(0d, sigma);
        }
    }

    private sealed record RenderSlice(
        double SourceStart,
        double SourceEnd,
        double DisplayStart,
        double DisplayEnd,
        ToneAdjustment Tone);

    private sealed record RenderTileKey(string RenderSignature, int TileIndex);

    private sealed record RenderTileCacheItem(RenderTileKey Key, MagickImage Image, long ByteCost);
}

public sealed record RenderCacheStats(long TileHits, long TileMisses, long TileBytes);
