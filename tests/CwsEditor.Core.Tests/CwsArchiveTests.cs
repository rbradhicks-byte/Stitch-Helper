using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CwsEditor.Core;
using ImageMagick;

namespace CwsEditor.Core.Tests;

public sealed class CwsArchiveTests
{
    [Fact]
    public async Task LoadAsyncReadsSyntheticArchive()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);

        using FileStream lockStream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);

        Assert.Equal(sourcePath, document.SourcePath);
        Assert.Equal(2, document.Strips.Count);
        Assert.Equal(6, document.CompositeWidth);
        Assert.Equal(4, document.StandardStripHeight);
        Assert.Equal(3, document.StripStride);
        Assert.Equal(3, document.ThumbnailWidth);
        Assert.Equal(3, document.DepthSamples.Count);
        Assert.Contains("Telemetry", document.TelemetryText);
    }

    [Theory]
    [InlineData("15/04/26 12:00:00.000 0.000 10.0")]
    [InlineData("15-04-26 12:00:00.000 0.000 10.0")]
    public void DepthSampleParseAcceptsObservedEvDateFormats(string line)
    {
        DepthSample sample = DepthSample.Parse(line);

        Assert.Equal(0d, sample.Depth);
        Assert.Equal(10d, sample.Orientation);
    }

    [Fact]
    public async Task GetStripsOverlappingReturnsOnlyIntersectingStrips()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);

        Assert.Equal(new[] { 0 }, document.GetStripsOverlapping(0d, 2.5d).Select(strip => strip.Index));
        Assert.Equal(new[] { 0, 1 }, document.GetStripsOverlapping(3d, 4d).Select(strip => strip.Index));
        Assert.Equal(new[] { 1 }, document.GetStripsOverlapping(4d, 7d).Select(strip => strip.Index));
    }

    [Fact]
    public async Task LoadAsyncNormalizesNegativeLayoutOffsetsForExport()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path, useNegativeOffsets: true);
        string outputPath = Path.Combine(temp.Path, "negative-offset-export.cws");
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);

        Assert.Equal(7d, document.SourceHeight);
        Assert.Equal(3, document.StripStride);
        Assert.Equal(new[] { 0, 3 }, document.Strips.Select(strip => strip.YOffset));

        EditSession session = new()
        {
            GlobalVerticalScale = 1.1d,
        };
        await CwsArchive.SaveEditedAsync(document, session, outputPath).WaitAsync(TimeSpan.FromSeconds(10));
        CwsDocument exported = await CwsArchive.LoadAsync(outputPath);

        Assert.True(exported.SourceHeight > document.SourceHeight);
    }

    [Fact]
    public async Task LoadAsyncNormalizesDescendingPartialLayoutWithoutArtificialGap()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateDescendingPartialAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);

        Assert.Equal(new[] { "00001.png", "00000.png" }, document.Strips.Select(strip => strip.ImageFileName));
        Assert.Equal(new[] { 0, 1 }, document.Strips.Select(strip => strip.YOffset));
        Assert.Equal(5d, document.SourceHeight);
        Assert.Equal(3, document.StripStride);

        using CwsRenderService renderer = new(document);
        using MagickImage image = renderer.RenderViewportImage(new EditSession(), new EditSession().BuildWarpMap(document.SourceHeight), 0d, document.SourceHeight, 1d);
        var pixels = image.GetPixels();

        Assert.Equal(5u, image.Height);
        for (int y = 0; y < image.Height; y++)
        {
            var color = pixels.GetPixel(0, y).ToColor();
            Assert.NotNull(color);
            Assert.True(color!.A > 0, $"Row {y} was transparent, indicating an artificial layout gap.");
        }
    }

    [Fact]
    public async Task CropOnDescendingPartialLayoutRemovesSelectedLowerInterval()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateDescendingPartialAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        using CwsRenderService renderer = new(document);

        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", 1d, 2d, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);

        using MagickImage image = renderer.RenderViewportImage(session, warp, 0d, warp.TotalDisplayHeight, 1d);
        var pixels = image.GetPixels();
        var top = pixels.GetPixel(0, 0).ToColor();
        var afterCrop = pixels.GetPixel(0, 1).ToColor();

        Assert.Equal(4u, image.Height);
        Assert.NotNull(top);
        Assert.NotNull(afterCrop);
        Assert.True(top!.B > 100);
        Assert.True(top.G < 30);
        Assert.True(top.R < 200);
        Assert.True(afterCrop!.R > 200);
        Assert.True(afterCrop.G < 30);
        Assert.True(afterCrop.B < 30);
    }

    [Fact]
    public async Task DepthMapperUsesNormalizedNegativeDisplacements()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateDescendingPartialAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        DepthMapper mapper = new(document);

        Assert.Equal(1, document.DisplacementStride);
        Assert.Contains(document.Displacements, sample => Math.Abs(sample.RegionY - 4d) < 0.0001d);

        DateTimeOffset top = mapper.GetJobTimeAtSourceY(0d);
        DateTimeOffset lower = mapper.GetJobTimeAtSourceY(4d);

        Assert.NotEqual(top, lower);
    }

    [Fact]
    public async Task RenderViewportUsesSourceStrips()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        using CwsRenderService renderer = new(document);

        using MagickImage image = await renderer.RenderViewportImageAsync(new EditSession(), 0d, 4d, 1d);
        var firstPixel = image.GetPixels().GetPixel(0, 0).ToColor();

        Assert.Equal(6u, image.Width);
        Assert.Equal(4u, image.Height);
        Assert.NotNull(firstPixel);
        Assert.True(firstPixel!.R > 200);
        Assert.True(firstPixel.G < 30);
        Assert.True(firstPixel.B < 30);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public async Task RenderViewportWithRegionScaleKeepsWidthAndAvoidsTransparentGaps(double regionScale)
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        using CwsRenderService renderer = new(document);

        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Zone", 1d, 5d, RegionGeometryMode.Scale, regionScale, ToneAdjustment.Identity));
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);

        using MagickImage image = await renderer.RenderViewportImageAsync(session, warp, 0d, warp.TotalDisplayHeight, 1d);
        var pixels = image.GetPixels();

        Assert.Equal((uint)document.CompositeWidth, image.Width);
        Assert.Equal((uint)Math.Ceiling(warp.TotalDisplayHeight), image.Height);
        for (int y = 0; y < image.Height; y++)
        {
            var color = pixels.GetPixel(0, y).ToColor();
            Assert.NotNull(color);
            Assert.True(color!.A > 0, $"Row {y} was transparent for scale {regionScale}.");
        }
    }

    [Fact]
    public async Task RenderViewportWithCropMovesFollowingPixelsUp()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        using CwsRenderService renderer = new(document);

        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", 1d, 3d, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);

        using MagickImage image = await renderer.RenderViewportImageAsync(session, warp, 0d, warp.TotalDisplayHeight, 1d);
        var pixels = image.GetPixels();
        var top = pixels.GetPixel(0, 0).ToColor();
        var afterCrop = pixels.GetPixel(0, 1).ToColor();

        Assert.NotNull(top);
        Assert.NotNull(afterCrop);
        Assert.True(top!.R > 200);
        Assert.True(top.G < 30);
        Assert.True(top.B < 30);
        Assert.True(afterCrop!.B > 200);
        Assert.True(afterCrop.R < 30);
        Assert.True(afterCrop.G < 30);
    }

    [Fact]
    public async Task FastIdentityRenderMatchesGenericRender()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        EditSession session = new();
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);

        using CwsRenderService fastRenderer = new(document, renderTileCacheByteLimit: 0, enableFastPath: true);
        using CwsRenderService genericRenderer = new(document, renderTileCacheByteLimit: 0, enableFastPath: false);
        using MagickImage fast = fastRenderer.RenderViewportImage(session, warp, 0d, document.SourceHeight, 1d);
        using MagickImage generic = genericRenderer.RenderViewportImage(session, warp, 0d, document.SourceHeight, 1d);

        AssertSamePixels(generic, fast);
    }

    [Fact]
    public async Task FastRendererMatchesGenericRenderWithToneCropAndScale()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        EditSession session = new()
        {
            GlobalTone = new ToneAdjustment(5d, 3d, 0d, false),
        };
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Scale", 0d, 1d, RegionGeometryMode.Scale, 1.2d, ToneAdjustment.Identity));
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", 1d, 2d, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Tone", 3d, 5d, RegionGeometryMode.None, null, new ToneAdjustment(-5d, 0d, 0d, false)));
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);

        using CwsRenderService fastRenderer = new(document, renderTileCacheByteLimit: 0, enableFastPath: true);
        using CwsRenderService genericRenderer = new(document, renderTileCacheByteLimit: 0, enableFastPath: false);
        using MagickImage fast = fastRenderer.RenderViewportImage(session, warp, 0d, warp.TotalDisplayHeight, 1d);
        using MagickImage generic = genericRenderer.RenderViewportImage(session, warp, 0d, warp.TotalDisplayHeight, 1d);

        AssertSamePixels(generic, fast);
    }

    [Fact]
    public async Task RenderTileCacheReusesOverlappingViewportTiles()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        CwsDocument document = await CwsArchive.LoadAsync(sourcePath);
        EditSession session = new();
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);
        using CwsRenderService renderer = new(document, renderTileCacheByteLimit: 1024 * 1024);

        using MagickImage first = renderer.RenderViewportImage(session, warp, 0d, 3d, 1d);
        using MagickImage second = renderer.RenderViewportImage(session, warp, 1d, 3d, 1d);

        Assert.True(renderer.CacheStats.TileMisses >= 1);
        Assert.True(renderer.CacheStats.TileHits >= 1);
    }

    [Fact]
    public async Task SaveEditedAsyncPreservesPassthroughAndExpandsLayout()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        string outputPath = Path.Combine(temp.Path, "sample_edited.cws");
        CwsDocument sourceDocument = await CwsArchive.LoadAsync(sourcePath);

        EditSession session = new()
        {
            GlobalVerticalScale = 1.5,
            GlobalTone = new ToneAdjustment(10d, 5d, 15d, false),
        };
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Zone", 1d, 5d, RegionGeometryMode.Scale, 0.8d, new ToneAdjustment(-5d, 10d, 0d, true)));

        await CwsArchive.SaveEditedAsync(sourceDocument, session, outputPath);
        CwsDocument outputDocument = await CwsArchive.LoadAsync(outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceDocument.PassthroughEntries["Depth.txt"].Data, outputDocument.PassthroughEntries["Depth.txt"].Data);
        Assert.Equal(sourceDocument.PassthroughEntries["Telemetry.txt"].Data, outputDocument.PassthroughEntries["Telemetry.txt"].Data);
        Assert.Equal(sourceDocument.PassthroughEntries["Source.stitchproj2"].Data, outputDocument.PassthroughEntries["Source.stitchproj2"].Data);
        Assert.True(outputDocument.Strips.Count > sourceDocument.Strips.Count);
        Assert.True(outputDocument.SourceHeight > sourceDocument.SourceHeight);
        Assert.NotEmpty(outputDocument.StitchMetadata.Displacements);

        JsonObject stitchJson = await ReadStitchJsonAsync(outputPath);
        JsonArray displacements = stitchJson["displacements"]?.AsArray() ?? throw new InvalidOperationException("Missing displacements array.");
        JsonObject debug = stitchJson["debug"]?.AsObject() ?? throw new InvalidOperationException("Missing debug object.");
        JsonArray movement = debug["movement"]?.AsArray() ?? throw new InvalidOperationException("Missing movement array.");
        JsonArray cumulative = debug["cumulative"]?.AsArray() ?? throw new InvalidOperationException("Missing cumulative array.");

        Assert.Equal(displacements.Count, movement.Count);
        Assert.Equal(displacements.Count, cumulative.Count);

        double runningX = 0d;
        double runningY = 0d;
        for (int index = 0; index < movement.Count; index++)
        {
            JsonObject step = movement[index]?.AsObject() ?? throw new InvalidOperationException($"Missing movement[{index}] object.");
            JsonObject total = cumulative[index]?.AsObject() ?? throw new InvalidOperationException($"Missing cumulative[{index}] object.");
            runningX += step["x"]?.GetValue<double>() ?? 0d;
            runningY += step["y"]?.GetValue<double>() ?? 0d;
            Assert.Equal(runningX, total["x"]?.GetValue<double>() ?? double.NaN);
            Assert.Equal(runningY, total["y"]?.GetValue<double>() ?? double.NaN);
        }
    }

    [Fact]
    public async Task SaveEditedAsyncWithCropShrinksOutputHeight()
    {
        using TempDirectory temp = new();
        string sourcePath = await SampleCwsFactory.CreateAsync(temp.Path);
        string outputPath = Path.Combine(temp.Path, "sample_cropped.cws");
        CwsDocument sourceDocument = await CwsArchive.LoadAsync(sourcePath);

        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", 1d, 3d, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));

        await CwsArchive.SaveEditedAsync(sourceDocument, session, outputPath);
        CwsDocument outputDocument = await CwsArchive.LoadAsync(outputPath);

        Assert.True(outputDocument.SourceHeight < sourceDocument.SourceHeight);
        Assert.Equal(sourceDocument.PassthroughEntries["Depth.txt"].Data, outputDocument.PassthroughEntries["Depth.txt"].Data);

        using MagickImage firstOutputImage = await ReadImageEntryAsync(outputPath, "images/00000.png");
        var pixels = firstOutputImage.GetPixels();
        var top = pixels.GetPixel(0, 0).ToColor();
        var afterCrop = pixels.GetPixel(0, 1).ToColor();

        Assert.NotNull(top);
        Assert.NotNull(afterCrop);
        Assert.True(top!.R > 200);
        Assert.True(top.G < 30);
        Assert.True(top.B < 30);
        Assert.True(afterCrop!.B > 200);
        Assert.True(afterCrop.R < 30);
        Assert.True(afterCrop.G < 30);
    }

    private static class SampleCwsFactory
    {
        public static async Task<string> CreateAsync(string directory, bool useNegativeOffsets = false)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "sample.cws");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);

            await WriteEntryAsync(archive, "VersionInfo.txt", "1.1");
            await WriteEntryAsync(archive, "Depth.txt",
                """
                15/04/26 12:00:00.000 0.000 10.0
                15/04/26 12:00:01.000 1.000 20.0
                15/04/26 12:00:02.000 2.000 30.0
                """.ReplaceLineEndings("\n"));
            await WriteEntryAsync(archive, "Telemetry.txt", "Version=1\nTelemetry Sources\n");
            await WriteEntryAsync(archive, "Source.stitchproj2", "stub");
            await WriteEntryAsync(archive, "images/00000.png", CreateSolidPng(MagickColors.Red, 6, 4));
            await WriteEntryAsync(archive, "images/00001.png", CreateSolidPng(MagickColors.Blue, 6, 4));
            await WriteEntryAsync(archive, "thumbs/00000.png", CreateSolidPng(MagickColors.Red, 3, 2));
            await WriteEntryAsync(archive, "thumbs/00001.png", CreateSolidPng(MagickColors.Blue, 3, 2));
            await WriteEntryAsync(archive, "Stitch.dat", BuildStitchJson(useNegativeOffsets));

            return path;
        }

        public static async Task<string> CreateDescendingPartialAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "descending-partial.cws");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);

            await WriteEntryAsync(archive, "VersionInfo.txt", "1.1");
            await WriteEntryAsync(archive, "Depth.txt",
                """
                15/04/26 12:00:00.000 0.000 10.0
                15/04/26 12:00:01.000 1.000 20.0
                15/04/26 12:00:02.000 2.000 30.0
                15/04/26 12:00:03.000 3.000 40.0
                15/04/26 12:00:04.000 4.000 50.0
                """.ReplaceLineEndings("\n"));
            await WriteEntryAsync(archive, "Telemetry.txt", "Version=1\nTelemetry Sources\n");
            await WriteEntryAsync(archive, "Source.stitchproj2", "stub");
            await WriteEntryAsync(archive, "images/00000.png", CreateSolidPng(MagickColors.Red, 6, 4));
            await WriteEntryAsync(archive, "images/00001.png", CreateRowsPng([MagickColors.Blue, MagickColors.Lime], 6));
            await WriteEntryAsync(archive, "thumbs/00000.png", CreateSolidPng(MagickColors.Red, 3, 2));
            await WriteEntryAsync(archive, "thumbs/00001.png", CreateRowsPng([MagickColors.Blue, MagickColors.Lime], 3));
            await WriteEntryAsync(archive, "Stitch.dat", BuildDescendingPartialStitchJson());

            return path;
        }

        private static byte[] CreateSolidPng(IMagickColor<byte> color, uint width, uint height)
        {
            using MagickImage image = new(color, width, height);
            return image.ToByteArray(MagickFormat.Png);
        }

        private static byte[] CreateRowsPng(IReadOnlyList<IMagickColor<byte>> rowColors, uint width)
        {
            using MagickImage image = new(MagickColors.Transparent, width, (uint)rowColors.Count);
            for (int y = 0; y < rowColors.Count; y++)
            {
                using MagickImage row = new(rowColors[y], width, 1);
                image.Composite(row, 0, y, CompositeOperator.Over);
            }

            return image.ToByteArray(MagickFormat.Png);
        }

        private static async Task WriteEntryAsync(ZipArchive archive, string name, string content)
        {
            await WriteEntryAsync(archive, name, Encoding.UTF8.GetBytes(content));
        }

        private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            await using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(content);
        }

        private static string BuildStitchJson(bool useNegativeOffsets)
        {
            string firstYOffset = useNegativeOffsets ? "0" : "0";
            string secondYOffset = useNegativeOffsets ? "-3" : "3";
            string json =
                """
                {"layout":[
                {"image":"00000.png","width":6,"height":4,"x offset":0,"y offset":__FIRST_Y_OFFSET__},
                {"image":"00001.png","width":6,"height":4,"x offset":0,"y offset":__SECOND_Y_OFFSET__}
                ],
                "attributes":[],
                "highlights":[],
                "displacements":[
                {"region x":0.0,"region y":0.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:00.000Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":1.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:00.500Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":2.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:01.000Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":3.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:01.500Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":4.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:02.000Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":5.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:02.500Z","displacement x":0.0,"displacement y":1.0},
                {"region x":0.0,"region y":8.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:03.000Z","displacement x":0.0,"displacement y":1.0}
                ],
                "debug":{"movement":[
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":1.0}
                ],"cumulative":[
                {"x":0.0,"y":1.0},
                {"x":0.0,"y":2.0},
                {"x":0.0,"y":3.0},
                {"x":0.0,"y":4.0},
                {"x":0.0,"y":5.0},
                {"x":0.0,"y":6.0},
                {"x":0.0,"y":7.0}
                ],"time taken":"00:00:00","total frames":0,"frames per second":0.0}}
                """.ReplaceLineEndings(string.Empty);
            return json
                .Replace("__FIRST_Y_OFFSET__", firstYOffset, StringComparison.Ordinal)
                .Replace("__SECOND_Y_OFFSET__", secondYOffset, StringComparison.Ordinal);
        }

        private static string BuildDescendingPartialStitchJson() =>
            """
            {"layout":[
            {"image":"00000.png","width":6,"height":4,"x offset":0,"y offset":0},
            {"image":"00001.png","width":6,"height":2,"x offset":0,"y offset":-3}
            ],
            "attributes":[],
            "highlights":[],
            "displacements":[
            {"region x":0.0,"region y":0.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:00.000Z","displacement x":0.0,"displacement y":0.0},
            {"region x":0.0,"region y":0.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:00.100Z","displacement x":0.0,"displacement y":0.0},
            {"region x":0.0,"region y":-1.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:01.000Z","displacement x":0.0,"displacement y":-1.0},
            {"region x":0.0,"region y":-2.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:02.000Z","displacement x":0.0,"displacement y":-1.0},
            {"region x":0.0,"region y":-3.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:03.000Z","displacement x":0.0,"displacement y":-1.0},
            {"region x":0.0,"region y":-4.0,"region width":6.0,"region height":2.0,"job time":"2026-04-15T12:00:04.000Z","displacement x":0.0,"displacement y":-1.0}
            ],
            "debug":{"movement":[
            {"x":0.0,"y":0.0},
            {"x":0.0,"y":0.0},
            {"x":0.0,"y":-1.0},
            {"x":0.0,"y":-1.0},
            {"x":0.0,"y":-1.0},
            {"x":0.0,"y":-1.0}
            ],"cumulative":[
            {"x":0.0,"y":0.0},
            {"x":0.0,"y":0.0},
            {"x":0.0,"y":-1.0},
            {"x":0.0,"y":-2.0},
            {"x":0.0,"y":-3.0},
            {"x":0.0,"y":-4.0}
            ],"time taken":"00:00:00","total frames":0,"frames per second":0.0}}
            """.ReplaceLineEndings(string.Empty);
    }

    private static async Task<JsonObject> ReadStitchJsonAsync(string cwsPath)
    {
        using FileStream stream = new(cwsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = archive.GetEntry("Stitch.dat") ?? throw new InvalidOperationException("Stitch.dat was not found.");
        using StreamReader reader = new(entry.Open());
        string json = await reader.ReadToEndAsync();
        return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Stitch.dat is not valid JSON.");
    }

    private static async Task<MagickImage> ReadImageEntryAsync(string cwsPath, string entryName)
    {
        using FileStream stream = new(cwsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"{entryName} was not found.");
        await using Stream entryStream = entry.Open();
        using MemoryStream buffer = new();
        await entryStream.CopyToAsync(buffer);
        buffer.Position = 0;
        return new MagickImage(buffer);
    }

    private static void AssertSamePixels(MagickImage expected, MagickImage actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.ToByteArray(MagickFormat.Rgba), actual.ToByteArray(MagickFormat.Rgba));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cws-editor-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
