using System.Diagnostics;
using CwsEditor.Core;
using ImageMagick;

if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: CwsEditor.ResponsivenessBench <archive.cws> [all|all-with-export|scroll|page|zoom|drag|overview|load|export]");
    return 2;
}

string inputPath = args[0];
string modeFilter = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "all";

if (modeFilter is "load" or "all" or "all-with-export")
{
    await RunLoadBenchmarksAsync(inputPath);
}

CwsDocument document = await CwsArchive.LoadAsync(inputPath);
EditSession session = new();
WarpMap warp = session.BuildWarpMap(document.SourceHeight);
double totalHeight = warp.TotalDisplayHeight;
double viewportHeight = Math.Min(1800d, Math.Max(600d, totalHeight / 8d));

List<Scenario> scenarios =
[
    BuildSmallScroll(totalHeight, viewportHeight),
    BuildPageJumps(totalHeight, viewportHeight),
    BuildZoomSequence(totalHeight, viewportHeight),
    BuildScrollbarDrag(totalHeight, viewportHeight),
    BuildOverviewJumps(totalHeight, viewportHeight),
];

if (modeFilter is not "load" and not "export")
{
    Console.WriteLine("Scenario,Policy,Events,Renders,TotalMs,RenderMs,BitmapMs,TileHits,TileMisses,ImprovementPercent");
    foreach (Scenario scenario in scenarios.Where(s => modeFilter is "all" or "all-with-export" || s.Name.Equals(modeFilter, StringComparison.OrdinalIgnoreCase)))
    {
        BenchResult baseline = await RunBaselineAsync(document, session, warp, scenario);
        BenchResult current = await RunCurrentAsync(document, session, warp, scenario);
        double improvement = baseline.WorkMilliseconds <= 0d
            ? 0d
            : ((baseline.WorkMilliseconds - current.WorkMilliseconds) / baseline.WorkMilliseconds) * 100d;

        WriteResult(scenario.Name, "baseline", scenario.Events.Count, baseline, null);
        WriteResult(scenario.Name, "current", scenario.Events.Count, current, improvement);
    }
}

if (modeFilter is "export" or "all-with-export")
{
    await RunExportBenchmarksAsync(document);
}

return 0;

static void WriteResult(string scenario, string policy, int events, BenchResult result, double? improvement)
{
    string percent = improvement.HasValue ? improvement.Value.ToString("0.0") : "";
    Console.WriteLine($"{scenario},{policy},{events},{result.RenderCount},{result.TotalMilliseconds:0.0},{result.RenderMilliseconds:0.0},{result.BitmapMilliseconds:0.0},{result.TileHits},{result.TileMisses},{percent}");
}

static async Task RunLoadBenchmarksAsync(string inputPath)
{
    Stopwatch sw = Stopwatch.StartNew();
    CwsDocument loaded = await CwsArchive.LoadAsync(inputPath);
    sw.Stop();
    Console.WriteLine("LoadMetric,Milliseconds,Details");
    Console.WriteLine($"LoadAsync,{sw.Elapsed.TotalMilliseconds:0.0},strips={loaded.Strips.Count};depth={loaded.DepthSamples.Count};passthrough={loaded.PassthroughEntries.Count}");

    using CwsRenderService renderer = new(loaded, stripCacheCapacity: 48);
    EditSession session = new();
    WarpMap warp = session.BuildWarpMap(loaded.SourceHeight);
    double viewportHeight = Math.Min(1800d, Math.Max(600d, warp.TotalDisplayHeight / 8d));

    sw.Restart();
    using MagickImage firstViewport = renderer.RenderViewportImage(session, warp, 0d, viewportHeight, 1d, useRenderTileCache: false);
    double firstRenderMs = sw.Elapsed.TotalMilliseconds;
    Stopwatch bitmap = Stopwatch.StartNew();
    _ = firstViewport.ToByteArray(MagickFormat.Bgra);
    bitmap.Stop();
    Console.WriteLine($"FirstViewport,{firstRenderMs + bitmap.Elapsed.TotalMilliseconds:0.0},render={firstRenderMs:0.0};bitmap={bitmap.Elapsed.TotalMilliseconds:0.0};pixels={firstViewport.Width}x{firstViewport.Height}");

    sw.Restart();
    using MagickImage overview = renderer.RenderOverviewImage(session, warp, 1d);
    double overviewRenderMs = sw.Elapsed.TotalMilliseconds;
    bitmap.Restart();
    _ = overview.ToByteArray(MagickFormat.Bgra);
    bitmap.Stop();
    Console.WriteLine($"Overview,{overviewRenderMs + bitmap.Elapsed.TotalMilliseconds:0.0},render={overviewRenderMs:0.0};bitmap={bitmap.Elapsed.TotalMilliseconds:0.0};pixels={overview.Width}x{overview.Height}");
}

static async Task RunExportBenchmarksAsync(CwsDocument document)
{
    Console.WriteLine("ExportScenario,Milliseconds,OutputMB");
    string directory = Path.Combine(Path.GetTempPath(), $"stitch-helper-export-bench-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        await TimeExportAsync(document, new EditSession(), Path.Combine(directory, "no-edit.cws"), "no-edit");

        EditSession localTone = new();
        double toneStart = document.SourceHeight * 0.25d;
        localTone.UpsertRegion(new EditRegion(Guid.NewGuid(), "Tone", toneStart, toneStart + Math.Min(1800d, document.SourceHeight * 0.05d), RegionGeometryMode.None, null, new ToneAdjustment(10d, 5d, 0d, false)));
        await TimeExportAsync(document, localTone, Path.Combine(directory, "local-tone.cws"), "local-tone");

        EditSession crop = new();
        double cropStart = document.SourceHeight * 0.35d;
        crop.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", cropStart, cropStart + Math.Min(1200d, document.SourceHeight * 0.03d), RegionGeometryMode.Crop, null, ToneAdjustment.Identity));
        await TimeExportAsync(document, crop, Path.Combine(directory, "crop.cws"), "crop");

        EditSession globalScale = new()
        {
            GlobalVerticalScale = 1.02d,
        };
        await TimeExportAsync(document, globalScale, Path.Combine(directory, "global-scale.cws"), "global-scale");
    }
    finally
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}

static async Task TimeExportAsync(CwsDocument document, EditSession session, string outputPath, string name)
{
    Stopwatch sw = Stopwatch.StartNew();
    await CwsArchive.SaveEditedAsync(document, session, outputPath);
    sw.Stop();
    double outputMb = File.Exists(outputPath) ? new FileInfo(outputPath).Length / 1024d / 1024d : 0d;
    Console.WriteLine($"{name},{sw.Elapsed.TotalMilliseconds:0.0},{outputMb:0.0}");
}

static async Task<BenchResult> RunBaselineAsync(CwsDocument document, EditSession session, WarpMap warp, Scenario scenario)
{
    using CwsRenderService renderer = new(document, stripCacheCapacity: 48, renderTileCacheByteLimit: 0, enableFastPath: false);
    Stopwatch total = Stopwatch.StartNew();
    double renderMs = 0d;
    double bitmapMs = 0d;
    int renders = 0;

    foreach (ViewportEvent item in scenario.Events)
    {
        Stopwatch render = Stopwatch.StartNew();
        (double start, double height) = FixedRange(item.DisplayStart, item.ViewportHeight, item.Zoom, warp.TotalDisplayHeight);
        using MagickImage image = await renderer.RenderViewportImageAsync(session, warp, start, height, 1d);
        ResizeLikeOriginalViewport(image, item.Zoom);
        render.Stop();

        Stopwatch bitmap = Stopwatch.StartNew();
        _ = image.ToByteArray(MagickFormat.Bgra);
        bitmap.Stop();

        renderMs += render.Elapsed.TotalMilliseconds;
        bitmapMs += bitmap.Elapsed.TotalMilliseconds;
        renders++;
    }

    total.Stop();
    RenderCacheStats stats = renderer.CacheStats;
    return new BenchResult(total.Elapsed.TotalMilliseconds, renderMs, bitmapMs, renders, stats.TileHits, stats.TileMisses);
}

static async Task<BenchResult> RunCurrentAsync(CwsDocument document, EditSession session, WarpMap warp, Scenario scenario)
{
    using CwsRenderService renderer = new(document, stripCacheCapacity: 48);
    Stopwatch total = Stopwatch.StartNew();
    double renderMs = 0d;
    double bitmapMs = 0d;
    int renders = 0;
    ViewportBuffer? buffer = null;
    double lastStart = scenario.Events.Count > 0 ? scenario.Events[0].DisplayStart : 0d;

    foreach (ViewportEvent item in scenario.Events)
    {
        if (item.Mode == InteractionMode.Zoom || item.Mode == InteractionMode.ScrollbarDrag)
        {
            lastStart = item.DisplayStart;
            continue;
        }

        double visibleEnd = Math.Min(warp.TotalDisplayHeight, item.DisplayStart + item.ViewportHeight);
        if (buffer is not null &&
            item.DisplayStart >= buffer.Start &&
            visibleEnd <= buffer.End &&
            Math.Abs(buffer.Zoom - item.Zoom) < 0.0001d)
        {
            lastStart = item.DisplayStart;
            continue;
        }

        (double start, double end) = AdaptiveRange(item.Mode, item.DisplayStart, visibleEnd, item.ViewportHeight, item.Zoom, lastStart, warp.TotalDisplayHeight);
        RenderMeasurement measurement = await RenderMeasuredAsync(renderer, session, warp, start, Math.Max(1d, end - start), item.Mode == InteractionMode.SmallScroll);
        renderMs += measurement.RenderMilliseconds;
        bitmapMs += measurement.BitmapMilliseconds;
        renders++;
        buffer = new ViewportBuffer(start, start + measurement.PixelHeight, item.Zoom);
        lastStart = item.DisplayStart;
    }

    ViewportEvent? finalDeferred = scenario.Events.LastOrDefault(item => item.Mode is InteractionMode.Zoom or InteractionMode.ScrollbarDrag);
    if (finalDeferred is not null)
    {
        ViewportEvent item = finalDeferred;
        double visibleEnd = Math.Min(warp.TotalDisplayHeight, item.DisplayStart + item.ViewportHeight);
        (double start, double end) = AdaptiveRange(item.Mode, item.DisplayStart, visibleEnd, item.ViewportHeight, item.Zoom, lastStart, warp.TotalDisplayHeight);
        RenderMeasurement measurement = await RenderMeasuredAsync(renderer, session, warp, start, Math.Max(1d, end - start), useRenderTileCache: false);
        renderMs += measurement.RenderMilliseconds;
        bitmapMs += measurement.BitmapMilliseconds;
        renders++;
        buffer = new ViewportBuffer(start, start + measurement.PixelHeight, item.Zoom);
    }

    total.Stop();
    RenderCacheStats stats = renderer.CacheStats;
    return new BenchResult(total.Elapsed.TotalMilliseconds, renderMs, bitmapMs, renders, stats.TileHits, stats.TileMisses);
}

static async Task<RenderMeasurement> RenderMeasuredAsync(CwsRenderService renderer, EditSession session, WarpMap warp, double start, double height, bool useRenderTileCache)
{
    Stopwatch render = Stopwatch.StartNew();
    using MagickImage image = await renderer.RenderViewportImageAsync(session, warp, start, height, 1d, useRenderTileCache: useRenderTileCache);
    render.Stop();
    Stopwatch bitmap = Stopwatch.StartNew();
    _ = image.ToByteArray(MagickFormat.Bgra);
    bitmap.Stop();
    return new RenderMeasurement(render.Elapsed.TotalMilliseconds, bitmap.Elapsed.TotalMilliseconds, image.Height);
}

static (double Start, double Height) FixedRange(double displayStart, double viewportHeight, double zoom, double totalHeight)
{
    const double bufferPixels = 220d;
    double before = bufferPixels / Math.Max(0.1d, zoom);
    double start = Math.Max(0d, displayStart - before);
    double end = Math.Min(totalHeight, displayStart + viewportHeight + before);
    return (start, Math.Max(1d, end - start));
}

static (double Start, double End) AdaptiveRange(
    InteractionMode mode,
    double visibleStart,
    double visibleEnd,
    double visibleHeight,
    double zoom,
    double lastStart,
    double totalHeight)
{
    const double minimumBufferPixels = 220d;
    double minimumBuffer = minimumBufferPixels / Math.Max(0.1d, zoom);
    double before;
    double after;
    switch (mode)
    {
        case InteractionMode.PageJump:
        case InteractionMode.OverviewJump:
            before = Math.Max(minimumBuffer * 0.35d, visibleHeight * 0.08d);
            after = before;
            break;
        case InteractionMode.Zoom:
            before = Math.Max(minimumBuffer * 0.5d, visibleHeight * 0.18d);
            after = before;
            break;
        case InteractionMode.ScrollbarDrag:
            before = Math.Max(minimumBuffer * 0.5d, visibleHeight * 0.14d);
            after = before;
            break;
        default:
            double directional = Math.Max(minimumBuffer, visibleHeight * 0.9d);
            double trailing = Math.Max(minimumBuffer * 0.35d, visibleHeight * 0.25d);
            bool movingDown = visibleStart >= lastStart;
            before = movingDown ? trailing : directional;
            after = movingDown ? directional : trailing;
            break;
    }

    return (Math.Max(0d, visibleStart - before), Math.Min(totalHeight, visibleEnd + after));
}

static void ResizeLikeOriginalViewport(MagickImage image, double zoom)
{
    if (Math.Abs(zoom - 1d) < 0.0001d)
    {
        return;
    }

    MagickGeometry geometry = new(
        Math.Max(1, (uint)Math.Round(image.Width * zoom)),
        Math.Max(1, (uint)Math.Round(image.Height * zoom)))
    {
        IgnoreAspectRatio = true,
    };
    image.Resize(geometry);
}

static Scenario BuildSmallScroll(double totalHeight, double viewportHeight)
{
    double start = totalHeight * 0.18d;
    double step = viewportHeight * 0.12d;
    List<ViewportEvent> events = Enumerable.Range(0, 42)
        .Select(index => new ViewportEvent(ClampStart(start + (index * step), viewportHeight, totalHeight), viewportHeight, 0.55d, InteractionMode.SmallScroll))
        .ToList();
    return new Scenario("scroll", events);
}

static Scenario BuildPageJumps(double totalHeight, double viewportHeight)
{
    double start = totalHeight * 0.12d;
    double step = viewportHeight * 0.9d;
    List<ViewportEvent> events = Enumerable.Range(0, 14)
        .Select(index => new ViewportEvent(ClampStart(start + (index * step), viewportHeight, totalHeight), viewportHeight, 0.55d, InteractionMode.PageJump))
        .ToList();
    return new Scenario("page", events);
}

static Scenario BuildZoomSequence(double totalHeight, double viewportHeight)
{
    double start = totalHeight * 0.32d;
    double[] zooms = [0.35d, 0.42d, 0.5d, 0.62d, 0.74d, 0.88d, 1.05d, 1.2d, 1.05d, 0.82d, 0.65d];
    List<ViewportEvent> events = zooms
        .Select(zoom => new ViewportEvent(ClampStart(start, viewportHeight / zoom, totalHeight), viewportHeight / zoom, zoom, InteractionMode.Zoom))
        .ToList();
    return new Scenario("zoom", events);
}

static Scenario BuildScrollbarDrag(double totalHeight, double viewportHeight)
{
    List<ViewportEvent> events = Enumerable.Range(0, 28)
        .Select(index =>
        {
            double t = index / 27d;
            double eased = t * t * (3d - (2d * t));
            return new ViewportEvent(ClampStart(totalHeight * eased * 0.85d, viewportHeight, totalHeight), viewportHeight, 0.55d, InteractionMode.ScrollbarDrag);
        })
        .ToList();
    return new Scenario("drag", events);
}

static Scenario BuildOverviewJumps(double totalHeight, double viewportHeight)
{
    double[] positions = [0.08d, 0.62d, 0.18d, 0.78d, 0.34d, 0.91d, 0.49d, 0.12d, 0.72d, 0.25d];
    List<ViewportEvent> events = positions
        .Select(position => new ViewportEvent(ClampStart(totalHeight * position, viewportHeight, totalHeight), viewportHeight, 0.55d, InteractionMode.OverviewJump))
        .ToList();
    return new Scenario("overview", events);
}

static double ClampStart(double start, double viewportHeight, double totalHeight) =>
    Math.Clamp(start, 0d, Math.Max(0d, totalHeight - viewportHeight));

internal sealed record Scenario(string Name, IReadOnlyList<ViewportEvent> Events);

internal sealed record ViewportEvent(double DisplayStart, double ViewportHeight, double Zoom, InteractionMode Mode);

internal sealed record BenchResult(
    double TotalMilliseconds,
    double RenderMilliseconds,
    double BitmapMilliseconds,
    int RenderCount,
    long TileHits,
    long TileMisses)
{
    public double WorkMilliseconds => RenderMilliseconds + BitmapMilliseconds;
}

internal sealed record RenderMeasurement(double RenderMilliseconds, double BitmapMilliseconds, uint PixelHeight);

internal sealed record ViewportBuffer(double Start, double End, double Zoom);

internal enum InteractionMode
{
    SmallScroll,
    PageJump,
    Zoom,
    ScrollbarDrag,
    OverviewJump,
}
