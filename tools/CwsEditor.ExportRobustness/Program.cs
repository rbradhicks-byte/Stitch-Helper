using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using CwsEditor.Core;
using ImageMagick;

if (args.Length == 0 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: CwsEditor.ExportRobustness <stitching-root> [--export-count N] [--output-dir PATH] [--keep-output] [--cases a,b] [--min-export-mb N] [--max-export-mb N] [--path-contains TEXT]");
    return 2;
}

string root = args[0];
int exportCount = ReadIntOption(args, "--export-count", 14);
bool keepOutput = args.Any(arg => string.Equals(arg, "--keep-output", StringComparison.OrdinalIgnoreCase));
double minExportMb = ReadDoubleOption(args, "--min-export-mb", 0d);
double maxExportMb = ReadDoubleOption(args, "--max-export-mb", double.PositiveInfinity);
HashSet<string>? caseFilter = ReadCaseFilter(args);
string? pathContains = ReadStringOption(args, "--path-contains");
string outputRoot = ReadStringOption(args, "--output-dir")
    ?? Path.Combine(Path.GetTempPath(), $"stitch-helper-robustness-{DateTime.Now:yyyyMMdd-HHmmss}");
Directory.CreateDirectory(outputRoot);
string reportPath = Path.Combine(outputRoot, "robustness-report.csv");

List<string> paths = Directory
    .EnumerateFiles(root, "*.cws", SearchOption.AllDirectories)
    .Where(path => string.IsNullOrWhiteSpace(pathContains) || path.Contains(pathContains, StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"Discovered {paths.Count} .cws files under {root}");
Console.WriteLine($"Output root: {outputRoot}");

List<LoadedArchive> loaded = [];
List<ResultRow> results = [];

foreach (string path in paths)
{
    Stopwatch sw = Stopwatch.StartNew();
    try
    {
        CwsDocument document = await CwsArchive.LoadAsync(path);
        ValidateDocumentShape(document);
        SmokeRender(document);
        ValidateArchiveEntries(path, document);
        sw.Stop();
        loaded.Add(new LoadedArchive(path, document));
        results.Add(ResultRow.Pass("load+smoke", path, sw.Elapsed));
        await WriteReportAsync(reportPath, results);
        Console.WriteLine($"PASS load+smoke {FormatMb(path),8} MB  {path}");
    }
    catch (Exception ex)
    {
        sw.Stop();
        results.Add(ResultRow.Fail("load+smoke", path, sw.Elapsed, ex));
        await WriteReportAsync(reportPath, results);
        Console.WriteLine($"FAIL load+smoke {path}");
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    }
}

List<LoadedArchive> exportCandidates = loaded
    .Where(item =>
    {
        double mb = item.LengthBytes / 1024d / 1024d;
        return mb >= minExportMb && mb <= maxExportMb;
    })
    .ToList();
List<LoadedArchive> exportTargets = SelectExportTargets(exportCandidates, exportCount);
Console.WriteLine($"Selected {exportTargets.Count} files for edited export validation.");

foreach (LoadedArchive target in exportTargets)
{
    foreach (ExportCase exportCase in BuildExportCases(target.Document).Where(item => caseFilter is null || caseFilter.Contains(item.Name)))
    {
        string safeBaseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(target.Path));
        string caseOutput = Path.Combine(outputRoot, $"{safeBaseName}-{exportCase.Name}.cws");
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await CwsArchive.SaveEditedAsync(target.Document, exportCase.Session, caseOutput);
            CwsDocument exported = await CwsArchive.LoadAsync(caseOutput);
            ValidateDocumentShape(exported);
            ValidateArchiveEntries(caseOutput, exported);
            SmokeRender(exported);
            ValidateExportMetadata(caseOutput);
            sw.Stop();
            results.Add(ResultRow.Pass($"export:{exportCase.Name}", target.Path, sw.Elapsed));
            await WriteReportAsync(reportPath, results);
            Console.WriteLine($"PASS export:{exportCase.Name,-12} {sw.Elapsed.TotalSeconds,7:0.0}s  {target.Path}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(ResultRow.Fail($"export:{exportCase.Name}", target.Path, sw.Elapsed, ex));
            await WriteReportAsync(reportPath, results);
            Console.WriteLine($"FAIL export:{exportCase.Name} {target.Path}");
            Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!keepOutput)
            {
                TryDelete(caseOutput);
            }
        }
    }
}

int failures = results.Count(result => !result.Success);
Console.WriteLine($"Robustness complete: {results.Count - failures}/{results.Count} passed.");
Console.WriteLine($"Report: {reportPath}");

return failures == 0 ? 0 : 1;

static int ReadIntOption(string[] args, string name, int fallback)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], out int value))
        {
            return Math.Max(0, value);
        }
    }

    return fallback;
}

static string? ReadStringOption(string[] args, string name)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static double ReadDoubleOption(string[] args, string name, double fallback)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(args[index + 1], out double value))
        {
            return Math.Max(0d, value);
        }
    }

    return fallback;
}

static HashSet<string>? ReadCaseFilter(string[] args)
{
    string? value = ReadStringOption(args, "--cases");
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static void ValidateDocumentShape(CwsDocument document)
{
    if (document.Strips.Count == 0)
    {
        throw new InvalidOperationException("No image strips were loaded.");
    }

    if (document.CompositeWidth <= 0 || document.SourceHeight <= 0d)
    {
        throw new InvalidOperationException("Composite dimensions are invalid.");
    }

    if (document.DepthSamples.Count == 0)
    {
        throw new InvalidOperationException("No depth samples were loaded.");
    }

    if (document.StitchMetadata.LayoutEntries.Count != document.Strips.Count)
    {
        throw new InvalidOperationException("Layout entry count does not match loaded strip count.");
    }
}

static void ValidateArchiveEntries(string path, CwsDocument document)
{
    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
    foreach (CwsStrip strip in document.Strips)
    {
        _ = archive.GetEntry(strip.ImageEntryName) ?? throw new InvalidOperationException($"Missing image entry {strip.ImageEntryName}.");
        if (!string.IsNullOrWhiteSpace(strip.ThumbEntryName))
        {
            _ = archive.GetEntry(strip.ThumbEntryName) ?? throw new InvalidOperationException($"Missing thumb entry {strip.ThumbEntryName}.");
        }
    }

    _ = archive.GetEntry("Stitch.dat") ?? throw new InvalidOperationException("Missing Stitch.dat.");
}

static void SmokeRender(CwsDocument document)
{
    using CwsRenderService renderer = new(document, stripCacheCapacity: 8, renderTileCacheByteLimit: 0);
    EditSession session = new();
    WarpMap warp = session.BuildWarpMap(document.SourceHeight);
    double height = Math.Min(600d, Math.Max(1d, warp.TotalDisplayHeight));
    RenderAt(renderer, session, warp, 0d, height);
    RenderAt(renderer, session, warp, Math.Max(0d, (warp.TotalDisplayHeight - height) / 2d), height);
    RenderAt(renderer, session, warp, Math.Max(0d, warp.TotalDisplayHeight - height), height);
}

static void RenderAt(CwsRenderService renderer, EditSession session, WarpMap warp, double start, double height)
{
    using MagickImage image = renderer.RenderViewportImage(session, warp, start, height, 1d, useRenderTileCache: false);
    if (image.Width == 0 || image.Height == 0)
    {
        throw new InvalidOperationException("Rendered image has invalid dimensions.");
    }
}

static void ValidateExportMetadata(string path)
{
    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
    ZipArchiveEntry stitchEntry = archive.GetEntry("Stitch.dat") ?? throw new InvalidOperationException("Missing Stitch.dat.");
    using StreamReader reader = new(stitchEntry.Open());
    JsonObject root = JsonNode.Parse(reader.ReadToEnd())?.AsObject() ?? throw new InvalidOperationException("Stitch.dat is invalid JSON.");
    JsonArray displacements = root["displacements"]?.AsArray() ?? throw new InvalidOperationException("Missing displacements array.");
    JsonObject debug = root["debug"]?.AsObject() ?? throw new InvalidOperationException("Missing debug object.");
    JsonArray movement = debug["movement"]?.AsArray() ?? throw new InvalidOperationException("Missing debug.movement array.");
    JsonArray cumulative = debug["cumulative"]?.AsArray() ?? throw new InvalidOperationException("Missing debug.cumulative array.");
    if (movement.Count != displacements.Count || cumulative.Count != displacements.Count)
    {
        throw new InvalidOperationException($"Debug metadata count mismatch: displacements={displacements.Count}, movement={movement.Count}, cumulative={cumulative.Count}.");
    }
}

static List<LoadedArchive> SelectExportTargets(IReadOnlyList<LoadedArchive> loaded, int count)
{
    if (count <= 0 || loaded.Count <= count)
    {
        return count <= 0 ? [] : loaded.ToList();
    }

    LoadedArchive[] ordered = loaded
        .OrderBy(item => new FileInfo(item.Path).Length)
        .ToArray();
    SortedSet<int> indices = [];
    int[] fixedPositions = [0, ordered.Length - 1, ordered.Length / 2, ordered.Length / 4, (ordered.Length * 3) / 4];
    foreach (int position in fixedPositions)
    {
        if (indices.Count >= count)
        {
            break;
        }

        indices.Add(Math.Clamp(position, 0, ordered.Length - 1));
    }

    int step = Math.Max(1, ordered.Length / count);
    for (int index = 0; index < ordered.Length && indices.Count < count; index += step)
    {
        indices.Add(index);
    }

    for (int index = 0; index < ordered.Length && indices.Count < count; index++)
    {
        indices.Add(index);
    }

    return indices.Select(index => ordered[index]).ToList();
}

static IEnumerable<ExportCase> BuildExportCases(CwsDocument document)
{
    yield return new ExportCase("no-edit", new EditSession());

    EditSession tone = new();
    (double start, double end) = PickInterval(document, 0.25d, 0.04d, maxHeight: 1800d);
    tone.UpsertRegion(new EditRegion(Guid.NewGuid(), "Tone", start, end, RegionGeometryMode.None, null, new ToneAdjustment(8d, 4d, 0d, false)));
    yield return new ExportCase("local-tone", tone);

    EditSession scale = new();
    (start, end) = PickInterval(document, 0.48d, 0.03d, maxHeight: 1200d);
    scale.UpsertRegion(new EditRegion(Guid.NewGuid(), "Scale", start, end, RegionGeometryMode.Scale, 0.95d, ToneAdjustment.Identity));
    yield return new ExportCase("local-scale", scale);

    EditSession crop = new();
    (start, end) = PickInterval(document, 0.62d, 0.02d, maxHeight: 900d);
    crop.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", start, end, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));
    yield return new ExportCase("crop", crop);

    EditSession combined = new()
    {
        GlobalTone = new ToneAdjustment(2d, 1d, 0d, false),
    };
    (start, end) = PickInterval(document, 0.35d, 0.025d, maxHeight: 1000d);
    combined.UpsertRegion(new EditRegion(Guid.NewGuid(), "Combined Scale", start, end, RegionGeometryMode.Scale, 1.03d, new ToneAdjustment(-4d, 2d, 0d, false)));
    yield return new ExportCase("combined", combined);
}

static (double Start, double End) PickInterval(CwsDocument document, double position, double fraction, double maxHeight)
{
    double height = Math.Clamp(document.SourceHeight * fraction, 1d, maxHeight);
    double start = Math.Clamp((document.SourceHeight * position) - (height / 2d), 0d, Math.Max(0d, document.SourceHeight - height));
    return (start, start + height);
}

static Task WriteReportAsync(string reportPath, IEnumerable<ResultRow> rows) =>
    File.WriteAllLinesAsync(reportPath, BuildReportLines(rows));

static IEnumerable<string> BuildReportLines(IEnumerable<ResultRow> rows)
{
    yield return "success,operation,seconds,path,error";
    foreach (ResultRow row in rows)
    {
        yield return string.Join(
            ",",
            row.Success ? "true" : "false",
            Csv(row.Operation),
            row.Elapsed.TotalSeconds.ToString("0.000"),
            Csv(row.Path),
            Csv(row.Error ?? string.Empty));
    }
}

static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

static string FormatMb(string path) => (new FileInfo(path).Length / 1024d / 1024d).ToString("0.0");

static string MakeSafeFileName(string name)
{
    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
        name = name.Replace(invalid, '_');
    }

    return name.Length <= 96 ? name : name[..96];
}

static void TryDelete(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
    }
}

internal sealed record LoadedArchive(string Path, CwsDocument Document)
{
    public long LengthBytes { get; } = new FileInfo(Path).Length;
}

internal sealed record ExportCase(string Name, EditSession Session);

internal sealed record ResultRow(bool Success, string Operation, string Path, TimeSpan Elapsed, string? Error)
{
    public static ResultRow Pass(string operation, string path, TimeSpan elapsed) => new(true, operation, path, elapsed, null);

    public static ResultRow Fail(string operation, string path, TimeSpan elapsed, Exception ex) =>
        new(false, operation, path, elapsed, $"{ex.GetType().Name}: {ex.Message}");
}
