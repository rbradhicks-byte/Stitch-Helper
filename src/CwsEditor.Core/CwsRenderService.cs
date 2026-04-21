using ImageMagick;

namespace CwsEditor.Core;

public sealed class CwsRenderService : IDisposable
{
    private readonly CwsDocument _document;
    private readonly StripImageCache _cache;

    public CwsRenderService(CwsDocument document)
    {
        _document = document;
        _cache = new StripImageCache(document);
    }

    public Task<MagickImage> RenderViewportImageAsync(
        EditSession session,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken = default) =>
        RenderViewportImageAsync(
            session,
            session.BuildWarpMap(_document.SourceHeight),
            displayStart,
            displayHeight,
            zoom,
            cancellationToken);

    public Task<MagickImage> RenderViewportImageAsync(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => RenderViewportImage(session, warp, displayStart, displayHeight, zoom, cancellationToken),
            cancellationToken);
    }

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

    public void Dispose() => _cache.Dispose();

    private MagickImage RenderViewportImage(
        EditSession session,
        WarpMap warp,
        double displayStart,
        double displayHeight,
        double zoom,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double safeZoom = Math.Clamp(zoom, 0.1d, 6d);
        MagickImage baseImage = RenderWarpedBaseImage(session, warp, displayStart, displayHeight, useThumbnails: false, 1d, cancellationToken);
        if (Math.Abs(safeZoom - 1d) < 0.0001d)
        {
            return baseImage;
        }

        using (baseImage)
        {
            uint outputWidth = (uint)Math.Max(1, (int)Math.Round(_document.CompositeWidth * safeZoom, MidpointRounding.AwayFromZero));
            uint outputHeight = (uint)Math.Max(1, (int)Math.Round(baseImage.Height * safeZoom, MidpointRounding.AwayFromZero));
            MagickImage zoomed = (MagickImage)baseImage.Clone();
            zoomed.Resize(outputWidth, outputHeight);
            return zoomed;
        }
    }

    private MagickImage RenderOverview(EditSession session, WarpMap warp, double overviewZoom, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double thumbScale = (_document.ThumbnailWidth / (double)_document.CompositeWidth) * Math.Clamp(overviewZoom, 0.2d, 5d);
        return RenderWarpedBaseImage(session, warp, 0d, warp.TotalDisplayHeight, useThumbnails: true, thumbScale, cancellationToken);
    }

    private MagickImage RenderWarpedBaseImage(
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

            using MagickImage resized = (MagickImage)sourceSlice.Clone();
            MagickGeometry geometry = new((uint)sourceSlice.Width, (uint)destinationHeight)
            {
                IgnoreAspectRatio = true,
            };
            resized.Resize(geometry);
            canvas.Composite(resized, 0, destinationTop, CompositeOperator.Over);
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

        foreach (EditRegion region in session.GetOrderedRegions())
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
        foreach (CwsStrip strip in _document.Strips)
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
}
