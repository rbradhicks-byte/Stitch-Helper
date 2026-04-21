using System.IO.Compression;
using System.Text;
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
    }

    private static class SampleCwsFactory
    {
        public static async Task<string> CreateAsync(string directory)
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
            await WriteEntryAsync(archive, "Stitch.dat", BuildStitchJson());

            return path;
        }

        private static byte[] CreateSolidPng(IMagickColor<byte> color, uint width, uint height)
        {
            using MagickImage image = new(color, width, height);
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

        private static string BuildStitchJson()
        {
            return
                """
                {"layout":[
                {"image":"00000.png","width":6,"height":4,"x offset":0,"y offset":0},
                {"image":"00001.png","width":6,"height":4,"x offset":0,"y offset":3}
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
                ],"time taken":"00:00:00","total frames":0,"frames per second":0.0}}
                """.ReplaceLineEndings(string.Empty);
        }
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
