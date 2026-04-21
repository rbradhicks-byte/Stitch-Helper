using ICSharpCode.SharpZipLib.Zip;
using ImageMagick;

namespace CwsEditor.Core;

public static class CwsArchive
{
    public static async Task<CwsDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CwsEditorException("A .cws path is required.");
        }

        if (!File.Exists(path))
        {
            throw new CwsEditorException($"Input .cws file was not found: {path}");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipFile zipFile = new(stream) { IsStreamOwner = false };

        List<string> entryOrder = [];
        Dictionary<string, ZipEntry> entriesByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipEntry entry in zipFile)
        {
            if (!entry.IsFile)
            {
                continue;
            }

            entryOrder.Add(entry.Name);
            entriesByName[entry.Name] = entry;
        }

        string stitchEntryName = FindRequiredEntry(entriesByName.Keys, "Stitch.dat");
        string depthEntryName = FindRequiredEntry(entriesByName.Keys, "Depth.txt");
        string telemetryEntryName = FindRequiredEntry(entriesByName.Keys, "Telemetry.txt");

        string stitchJson = await ReadTextEntryAsync(zipFile, stitchEntryName, cancellationToken);
        StitchMetadata stitchMetadata = StitchMetadata.Parse(stitchJson);
        string depthText = await ReadTextEntryAsync(zipFile, depthEntryName, cancellationToken);
        string telemetryText = await ReadTextEntryAsync(zipFile, telemetryEntryName, cancellationToken);

        List<DepthSample> depthSamples = depthText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DepthSample.Parse)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        List<CwsStrip> strips = [];
        foreach (StripLayoutEntry layoutEntry in stitchMetadata.LayoutEntries.OrderBy(entry => entry.YOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = strips.Count;
            string imageEntryName = FindOptionalEntry(entriesByName.Keys, $"images/{layoutEntry.ImageFileName}")
                ?? throw new CwsEditorException($"Image entry was not found for layout item: {layoutEntry.ImageFileName}");
            string thumbEntryName = FindOptionalEntry(entriesByName.Keys, $"thumbs/{layoutEntry.ImageFileName}")
                ?? FindOptionalEntry(entriesByName.Keys, $"thumbs/{Path.GetFileNameWithoutExtension(layoutEntry.ImageFileName)}.png")
                ?? string.Empty;
            byte[]? thumbBytes = string.IsNullOrEmpty(thumbEntryName)
                ? null
                : await ReadBinaryEntryAsync(zipFile, thumbEntryName, cancellationToken);
            strips.Add(
                new CwsStrip(
                    index,
                    imageEntryName,
                    thumbEntryName,
                    layoutEntry.ImageFileName,
                    layoutEntry.Width,
                    layoutEntry.Height,
                    layoutEntry.XOffset,
                    layoutEntry.YOffset,
                    thumbBytes));
        }

        int standardStripHeight = stitchMetadata.LayoutEntries.Count > 1
            ? stitchMetadata.LayoutEntries.Take(stitchMetadata.LayoutEntries.Count - 1).Max(entry => entry.Height)
            : stitchMetadata.LayoutEntries.FirstOrDefault()?.Height ?? 2048;
        int stripStride = stitchMetadata.LayoutEntries.Count > 1
            ? stitchMetadata.LayoutEntries[1].YOffset - stitchMetadata.LayoutEntries[0].YOffset
            : standardStripHeight;
        int thumbnailWidth = DetermineThumbnailWidth(strips);

        Dictionary<string, CwsPassthroughEntry> passthroughEntries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entryName in entryOrder.Where(name => !name.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("thumbs/", StringComparison.OrdinalIgnoreCase) && !name.Equals(stitchEntryName, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = await ReadBinaryEntryAsync(zipFile, entryName, cancellationToken);
            passthroughEntries[entryName] = new CwsPassthroughEntry(entryName, data);
        }

        int compositeWidth = stitchMetadata.LayoutEntries.FirstOrDefault()?.Width ?? strips.FirstOrDefault()?.Width ?? 1944;
        return new CwsDocument(
            path,
            strips,
            depthSamples,
            telemetryText,
            passthroughEntries,
            entryOrder,
            stitchMetadata,
            compositeWidth,
            standardStripHeight,
            stripStride,
            thumbnailWidth);
    }

    public static async Task SaveEditedAsync(
        CwsDocument document,
        EditSession session,
        string outputPath,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new CwsEditorException("An output .cws path is required.");
        }

        string sourceFullPath = Path.GetFullPath(document.SourcePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CwsEditorException("The output path must be different from the input path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath) ?? Environment.CurrentDirectory);
        string tempPath = Path.Combine(Path.GetDirectoryName(outputFullPath) ?? Environment.CurrentDirectory, $"{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");
        WarpMap warp = session.BuildWarpMap(document.SourceHeight);
        int totalDisplayHeight = Math.Max(1, (int)Math.Ceiling(warp.TotalDisplayHeight));
        List<GeneratedStrip> generatedStrips = BuildGeneratedStripPlan(document, totalDisplayHeight);
        DepthMapper depthMapper = new(document);
        List<DisplacementSample> displacements = BuildDisplacements(document, warp, depthMapper, totalDisplayHeight);
        List<MovementVector> movementVectors = displacements.Select(sample => new MovementVector(sample.DisplacementX, sample.DisplacementY)).ToList();
        List<StripLayoutEntry> layoutEntries = generatedStrips
            .Select(strip => new StripLayoutEntry(strip.ImageFileName, document.CompositeWidth, strip.Height, 0, strip.YOffset))
            .ToList();
        string stitchJson = document.StitchMetadata.BuildJson(layoutEntries, displacements, movementVectors);

        try
        {
            using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using ZipOutputStream zipStream = new(outputStream)
            {
                IsStreamOwner = false,
            };
            zipStream.SetLevel(6);

            int totalSteps = document.PassthroughEntries.Count + (generatedStrips.Count * 2) + 1;
            int completed = 0;

            foreach (CwsPassthroughEntry entry in document.PassthroughEntries.Values.OrderBy(item => item.EntryName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteEntryAsync(zipStream, entry.EntryName, entry.Data, cancellationToken);
                completed++;
                progress?.Report(new SaveProgress(completed, totalSteps, $"Copied {entry.EntryName}"));
            }

            using CwsRenderService renderer = new(document);
            foreach (GeneratedStrip strip in generatedStrips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using MagickImage image = await renderer.RenderViewportImageAsync(session, warp, strip.YOffset, strip.Height, 1d, cancellationToken);
                byte[] pngBytes = image.ToByteArray(MagickFormat.Png);
                await WriteEntryAsync(zipStream, $"images/{strip.ImageFileName}", pngBytes, cancellationToken);
                completed++;
                progress?.Report(new SaveProgress(completed, totalSteps, $"Rendered {strip.ImageFileName}"));

                int thumbHeight = Math.Max(1, (int)Math.Round(strip.Height * (document.ThumbnailWidth / (double)document.CompositeWidth), MidpointRounding.AwayFromZero));
                using MagickImage thumbnail = (MagickImage)image.Clone();
                thumbnail.Resize((uint)document.ThumbnailWidth, (uint)thumbHeight);
                byte[] thumbBytes = thumbnail.ToByteArray(MagickFormat.Png);
                await WriteEntryAsync(zipStream, $"thumbs/{strip.ImageFileName}", thumbBytes, cancellationToken);
                completed++;
                progress?.Report(new SaveProgress(completed, totalSteps, $"Rendered thumbs/{strip.ImageFileName}"));
            }

            await WriteEntryAsync(zipStream, "Stitch.dat", System.Text.Encoding.UTF8.GetBytes(stitchJson), cancellationToken);
            completed++;
            progress?.Report(new SaveProgress(completed, totalSteps, "Updated Stitch.dat"));

            zipStream.Finish();
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        if (File.Exists(outputFullPath))
        {
            File.Delete(outputFullPath);
        }

        File.Move(tempPath, outputFullPath);
    }

    private static List<GeneratedStrip> BuildGeneratedStripPlan(CwsDocument document, int totalDisplayHeight)
    {
        List<GeneratedStrip> strips = [];
        int currentYOffset = 0;
        int index = 0;
        while (currentYOffset < totalDisplayHeight)
        {
            int remaining = totalDisplayHeight - currentYOffset;
            int stripHeight = Math.Min(document.StandardStripHeight, remaining);
            if (stripHeight <= 0)
            {
                stripHeight = Math.Min(document.StandardStripHeight, totalDisplayHeight);
            }

            string fileName = $"{index:D5}.png";
            strips.Add(new GeneratedStrip(index, fileName, currentYOffset, stripHeight));
            if (currentYOffset + stripHeight >= totalDisplayHeight)
            {
                break;
            }

            currentYOffset += document.StripStride;
            index++;
        }

        return strips;
    }

    private static List<DisplacementSample> BuildDisplacements(CwsDocument document, WarpMap warp, DepthMapper depthMapper, int totalDisplayHeight)
    {
        int stride = Math.Max(1, document.DisplacementStride);
        double paddedHeight = Math.Max(0d, totalDisplayHeight + document.DisplacementOverscan);
        int sampleCount = Math.Max(1, ((int)Math.Floor(paddedHeight / stride)) + 1);
        List<DisplacementSample> results = [];
        for (int index = 0; index < sampleCount; index++)
        {
            double regionY = index * stride;
            double clampedDisplayY = Math.Clamp(regionY, 0d, warp.TotalDisplayHeight);
            double sourceY = warp.Inverse(clampedDisplayY);
            DateTimeOffset jobTime = depthMapper.GetJobTimeAtSourceY(sourceY);
            results.Add(
                new DisplacementSample(
                    0d,
                    regionY,
                    document.CompositeWidth,
                    document.DisplacementRegionHeight,
                    jobTime,
                    0d,
                    stride));
        }

        return results;
    }

    private static async Task WriteEntryAsync(ZipOutputStream zipStream, string entryName, byte[] data, CancellationToken cancellationToken)
    {
        ZipEntry entry = new(entryName)
        {
            DateTime = DateTime.Now,
            Size = data.Length,
        };
        zipStream.PutNextEntry(entry);
        await zipStream.WriteAsync(data, cancellationToken);
        zipStream.CloseEntry();
    }

    private static string FindRequiredEntry(IEnumerable<string> names, string expectedName) =>
        FindOptionalEntry(names, expectedName) ?? throw new CwsEditorException($"Archive entry was not found: {expectedName}");

    private static string? FindOptionalEntry(IEnumerable<string> names, string expectedName) =>
        names.FirstOrDefault(name => string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ReadTextEntryAsync(ZipFile zipFile, string entryName, CancellationToken cancellationToken)
    {
        byte[] data = await ReadBinaryEntryAsync(zipFile, entryName, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static async Task<byte[]> ReadBinaryEntryAsync(ZipFile zipFile, string entryName, CancellationToken cancellationToken)
    {
        ZipEntry entry = zipFile.GetEntry(entryName) ?? throw new CwsEditorException($"Archive entry was not found: {entryName}");
        using Stream entryStream = zipFile.GetInputStream(entry);
        using MemoryStream buffer = new();
        await entryStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static int DetermineThumbnailWidth(IReadOnlyList<CwsStrip> strips)
    {
        foreach (CwsStrip strip in strips)
        {
            if (strip.ThumbBytes is null)
            {
                continue;
            }

            using MagickImage thumbnail = new(strip.ThumbBytes);
            return (int)thumbnail.Width;
        }

        return 60;
    }

    private sealed record GeneratedStrip(int Index, string ImageFileName, int YOffset, int Height);
}
