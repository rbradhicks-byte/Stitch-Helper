using System.Globalization;
using System.Collections.Concurrent;
using ICSharpCode.SharpZipLib.Checksum;
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

        byte[] stitchBytes = await ReadBinaryEntryAsync(zipFile, stitchEntryName, cancellationToken);
        string stitchJson = System.Text.Encoding.UTF8.GetString(stitchBytes);
        StitchMetadata stitchMetadata = StitchMetadata.Parse(stitchJson);
        byte[] depthBytes = await ReadBinaryEntryAsync(zipFile, depthEntryName, cancellationToken);
        string depthText = System.Text.Encoding.UTF8.GetString(depthBytes);
        byte[] telemetryBytes = await ReadBinaryEntryAsync(zipFile, telemetryEntryName, cancellationToken);
        string telemetryText = System.Text.Encoding.UTF8.GetString(telemetryBytes);

        List<DepthSample> depthSamples = depthText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DepthSample.Parse)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        List<StripLayoutEntry> normalizedLayoutEntries = NormalizeLayoutEntries(stitchMetadata.LayoutEntries);

        List<CwsStrip> strips = [];
        foreach (StripLayoutEntry layoutEntry in normalizedLayoutEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = strips.Count;
            string imageEntryName = FindOptionalEntry(entriesByName.Keys, $"images/{layoutEntry.ImageFileName}")
                ?? throw new CwsEditorException($"Image entry was not found for layout item: {layoutEntry.ImageFileName}");
            string thumbEntryName = FindOptionalEntry(entriesByName.Keys, $"thumbs/{layoutEntry.ImageFileName}")
                ?? FindOptionalEntry(entriesByName.Keys, $"thumbs/{Path.GetFileNameWithoutExtension(layoutEntry.ImageFileName)}.png")
                ?? string.Empty;
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
                    null));
        }

        int standardStripHeight = normalizedLayoutEntries.Count > 1
            ? normalizedLayoutEntries.Take(normalizedLayoutEntries.Count - 1).Max(entry => entry.Height)
            : normalizedLayoutEntries.FirstOrDefault()?.Height ?? 2048;
        int stripStride = DetermineStripStride(normalizedLayoutEntries, standardStripHeight);
        int thumbnailWidth = DetermineThumbnailWidth(zipFile, strips);

        Dictionary<string, CwsPassthroughEntry> passthroughEntries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entryName in entryOrder.Where(name => !name.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("thumbs/", StringComparison.OrdinalIgnoreCase) && !name.Equals(stitchEntryName, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entryName, depthEntryName, StringComparison.OrdinalIgnoreCase))
            {
                passthroughEntries[entryName] = new CwsPassthroughEntry(entryName, depthBytes);
            }
            else if (string.Equals(entryName, telemetryEntryName, StringComparison.OrdinalIgnoreCase))
            {
                passthroughEntries[entryName] = new CwsPassthroughEntry(entryName, telemetryBytes);
            }
            else
            {
                string capturedEntryName = entryName;
                string capturedPath = path;
                passthroughEntries[entryName] = new CwsPassthroughEntry(
                    entryName,
                    () => ReadBinaryEntryFromFile(capturedPath, capturedEntryName));
            }
        }

        int compositeWidth = normalizedLayoutEntries.FirstOrDefault()?.Width ?? strips.FirstOrDefault()?.Width ?? 1944;
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

    private static List<StripLayoutEntry> NormalizeLayoutEntries(IReadOnlyList<StripLayoutEntry> layoutEntries)
    {
        if (layoutEntries.Count == 0)
        {
            return [];
        }

        int minYOffset = layoutEntries.Min(entry => entry.YOffset);
        return layoutEntries
            .Select(entry => entry with { YOffset = entry.YOffset - minYOffset })
            .OrderBy(entry => entry.YOffset)
            .ThenBy(entry => entry.ImageFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int DetermineStripStride(IReadOnlyList<StripLayoutEntry> orderedLayoutEntries, int standardStripHeight)
    {
        if (orderedLayoutEntries.Count > 1)
        {
            for (int index = 1; index < orderedLayoutEntries.Count; index++)
            {
                int stride = orderedLayoutEntries[index].YOffset - orderedLayoutEntries[index - 1].YOffset;
                if (stride > 0)
                {
                    return stride;
                }
            }
        }

        return Math.Max(1, standardStripHeight);
    }

    public static async Task SaveEditedAsync(
        CwsDocument document,
        EditSession session,
        string outputPath,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await SaveEditedAsync(document, session, outputPath, CwsExportOptions.Default, progress, cancellationToken);

    public static async Task SaveEditedAsync(
        CwsDocument document,
        EditSession session,
        string outputPath,
        CwsExportOptions? exportOptions,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);
        CwsExportOptions options = (exportOptions ?? CwsExportOptions.Default).Normalize();

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
            using FileStream sourceStream = new(document.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using ZipFile sourceZipFile = new(sourceStream) { IsStreamOwner = false };
            zipStream.SetLevel(6);

            int totalSteps = document.PassthroughEntries.Count + (generatedStrips.Count * 2) + 1;
            int completed = 0;
            using ExportRendererPool rendererPool = new(document, options.MaxDegreeOfParallelism);

            foreach (CwsPassthroughEntry entry in document.PassthroughEntries.Values.OrderBy(item => item.EntryName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyEntryAsync(sourceZipFile, zipStream, entry.EntryName, entry.EntryName, store: false, cancellationToken);
                completed++;
                progress?.Report(new SaveProgress(completed, totalSteps, $"Copied {entry.EntryName}"));
            }

            int nextToSchedule = 0;
            int nextToWrite = 0;
            int maxQueuedPayloads = Math.Max(1, options.MaxDegreeOfParallelism * 2);
            Dictionary<int, Task<ExportStripPayload>> inFlight = [];

            void ScheduleMore()
            {
                while (nextToSchedule < generatedStrips.Count && inFlight.Count < maxQueuedPayloads)
                {
                    GeneratedStrip scheduledStrip = generatedStrips[nextToSchedule++];
                    inFlight[scheduledStrip.Index] = Task.Run(
                        () => BuildExportStripPayload(document, session, warp, scheduledStrip, options, rendererPool, cancellationToken),
                        cancellationToken);
                }
            }

            ScheduleMore();
            while (nextToWrite < generatedStrips.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<ExportStripPayload> payloadTask = inFlight[nextToWrite];
                ExportStripPayload payload = await payloadTask;
                inFlight.Remove(nextToWrite);
                ScheduleMore();

                string imageEntryName = $"images/{payload.Strip.ImageFileName}";
                if (payload.SourceImageEntryName is not null)
                {
                    await CopyEntryAsync(sourceZipFile, zipStream, payload.SourceImageEntryName, imageEntryName, options.StorePngEntries, cancellationToken);
                    completed++;
                    progress?.Report(new SaveProgress(completed, totalSteps, $"Copied {payload.Strip.ImageFileName}"));
                }
                else
                {
                    await WriteEntryAsync(zipStream, imageEntryName, payload.ImagePngBytes ?? [], options.StorePngEntries, cancellationToken);
                    completed++;
                    progress?.Report(new SaveProgress(completed, totalSteps, $"Rendered {payload.Strip.ImageFileName}"));
                }

                string thumbEntryName = $"thumbs/{payload.Strip.ImageFileName}";
                if (payload.SourceThumbEntryName is not null)
                {
                    await CopyEntryAsync(sourceZipFile, zipStream, payload.SourceThumbEntryName, thumbEntryName, options.StorePngEntries, cancellationToken);
                    completed++;
                    progress?.Report(new SaveProgress(completed, totalSteps, $"Copied thumbs/{payload.Strip.ImageFileName}"));
                }
                else
                {
                    await WriteEntryAsync(zipStream, thumbEntryName, payload.ThumbPngBytes ?? [], options.StorePngEntries, cancellationToken);
                    completed++;
                    progress?.Report(new SaveProgress(completed, totalSteps, $"Rendered thumbs/{payload.Strip.ImageFileName}"));
                }

                nextToWrite++;
            }

            await WriteEntryAsync(zipStream, "Stitch.dat", System.Text.Encoding.UTF8.GetBytes(stitchJson), store: false, cancellationToken);
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

    private static ExportStripPayload BuildExportStripPayload(
        CwsDocument document,
        EditSession session,
        WarpMap warp,
        GeneratedStrip strip,
        CwsExportOptions options,
        ExportRendererPool rendererPool,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.CopyUnchangedStrips &&
            TryGetUnchangedSourceStrip(document, session, warp, strip, out CwsStrip? sourceStrip) &&
            !string.IsNullOrWhiteSpace(sourceStrip.ThumbEntryName))
        {
            return ExportStripPayload.FromSource(strip, sourceStrip.ImageEntryName, sourceStrip.ThumbEntryName);
        }

        CwsRenderService renderer = rendererPool.Rent(cancellationToken);
        try
        {
            using MagickImage image = renderer.RenderViewportImage(session, warp, strip.YOffset, strip.Height, 1d, cancellationToken);
            byte[] imageBytes = EncodePng(image, options);

            int thumbHeight = Math.Max(1, (int)Math.Round(strip.Height * (document.ThumbnailWidth / (double)document.CompositeWidth), MidpointRounding.AwayFromZero));
            using MagickImage thumbnail = (MagickImage)image.Clone();
            thumbnail.Resize((uint)document.ThumbnailWidth, (uint)thumbHeight);
            byte[] thumbBytes = EncodePng(thumbnail, options);

            return ExportStripPayload.FromBytes(strip, imageBytes, thumbBytes);
        }
        finally
        {
            rendererPool.Return(renderer);
        }
    }

    private static bool TryGetUnchangedSourceStrip(
        CwsDocument document,
        EditSession session,
        WarpMap warp,
        GeneratedStrip generatedStrip,
        out CwsStrip sourceStrip)
    {
        sourceStrip = null!;
        double displayStart = generatedStrip.YOffset;
        double displayEnd = generatedStrip.YOffset + generatedStrip.Height;
        double sourceStart = warp.Inverse(displayStart);
        double sourceEnd = warp.Inverse(displayEnd);
        if (Math.Abs(sourceStart - displayStart) > 0.0001d ||
            Math.Abs(sourceEnd - displayEnd) > 0.0001d ||
            session.HasEditsAffectingSourceInterval(sourceStart, sourceEnd))
        {
            return false;
        }

        sourceStrip = document.Strips.FirstOrDefault(strip =>
            strip.YOffset == generatedStrip.YOffset &&
            strip.Height == generatedStrip.Height) ?? null!;
        return sourceStrip is not null;
    }

    private static byte[] EncodePng(MagickImage image, CwsExportOptions options)
    {
        image.Strip();
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", options.PngCompressionLevel.ToString(CultureInfo.InvariantCulture));
        return image.ToByteArray(MagickFormat.Png);
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

    private static async Task WriteEntryAsync(ZipOutputStream zipStream, string entryName, byte[] data, bool store, CancellationToken cancellationToken)
    {
        ZipEntry entry = new(entryName)
        {
            DateTime = DateTime.Now,
            Size = data.Length,
        };

        if (store)
        {
            Crc32 crc = new();
            crc.Update(data);
            entry.Crc = crc.Value;
            entry.CompressionMethod = ICSharpCode.SharpZipLib.Zip.CompressionMethod.Stored;
        }

        zipStream.PutNextEntry(entry);
        await zipStream.WriteAsync(data, cancellationToken);
        zipStream.CloseEntry();
    }

    private static async Task CopyEntryAsync(
        ZipFile sourceZipFile,
        ZipOutputStream outputZipStream,
        string sourceEntryName,
        string outputEntryName,
        bool store,
        CancellationToken cancellationToken)
    {
        ZipEntry sourceEntry = sourceZipFile.GetEntry(sourceEntryName) ?? throw new CwsEditorException($"Archive entry was not found: {sourceEntryName}");
        using Stream sourceStream = sourceZipFile.GetInputStream(sourceEntry);
        using MemoryStream buffer = new();
        await sourceStream.CopyToAsync(buffer, cancellationToken);
        await WriteEntryAsync(outputZipStream, outputEntryName, buffer.ToArray(), store, cancellationToken);
    }

    private static string FindRequiredEntry(IEnumerable<string> names, string expectedName) =>
        FindOptionalEntry(names, expectedName) ?? throw new CwsEditorException($"Archive entry was not found: {expectedName}");

    private static string? FindOptionalEntry(IEnumerable<string> names, string expectedName) =>
        names.FirstOrDefault(name => string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBinaryEntryAsync(ZipFile zipFile, string entryName, CancellationToken cancellationToken)
    {
        ZipEntry entry = zipFile.GetEntry(entryName) ?? throw new CwsEditorException($"Archive entry was not found: {entryName}");
        using Stream entryStream = zipFile.GetInputStream(entry);
        using MemoryStream buffer = new();
        await entryStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    internal static byte[] ReadBinaryEntryFromFile(string archivePath, string entryName)
    {
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipFile zipFile = new(stream) { IsStreamOwner = false };
        ZipEntry entry = zipFile.GetEntry(entryName) ?? throw new CwsEditorException($"Archive entry was not found: {entryName}");
        using Stream entryStream = zipFile.GetInputStream(entry);
        using MemoryStream buffer = new();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static int DetermineThumbnailWidth(ZipFile zipFile, IReadOnlyList<CwsStrip> strips)
    {
        foreach (CwsStrip strip in strips)
        {
            if (string.IsNullOrWhiteSpace(strip.ThumbEntryName))
            {
                continue;
            }

            ZipEntry? entry = zipFile.GetEntry(strip.ThumbEntryName);
            if (entry is null)
            {
                continue;
            }

            using Stream entryStream = zipFile.GetInputStream(entry);
            using MemoryStream buffer = new();
            entryStream.CopyTo(buffer);
            buffer.Position = 0;
            using MagickImage thumbnail = new(buffer);
            return (int)thumbnail.Width;
        }

        return 60;
    }

    private sealed record GeneratedStrip(int Index, string ImageFileName, int YOffset, int Height);

    private sealed record ExportStripPayload(
        GeneratedStrip Strip,
        byte[]? ImagePngBytes,
        byte[]? ThumbPngBytes,
        string? SourceImageEntryName,
        string? SourceThumbEntryName)
    {
        public static ExportStripPayload FromBytes(GeneratedStrip strip, byte[] imagePngBytes, byte[] thumbPngBytes) =>
            new(strip, imagePngBytes, thumbPngBytes, null, null);

        public static ExportStripPayload FromSource(GeneratedStrip strip, string imageEntryName, string thumbEntryName) =>
            new(strip, null, null, imageEntryName, thumbEntryName);
    }

    private sealed class ExportRendererPool : IDisposable
    {
        private readonly ConcurrentBag<CwsRenderService> _renderers = [];
        private readonly SemaphoreSlim _semaphore;

        public ExportRendererPool(CwsDocument document, int count)
        {
            int safeCount = Math.Max(1, count);
            _semaphore = new SemaphoreSlim(safeCount, safeCount);
            for (int index = 0; index < safeCount; index++)
            {
                _renderers.Add(new CwsRenderService(document, stripCacheCapacity: 10, renderTileCacheByteLimit: 0));
            }
        }

        public CwsRenderService Rent(CancellationToken cancellationToken)
        {
            _semaphore.Wait(cancellationToken);
            if (_renderers.TryTake(out CwsRenderService? renderer))
            {
                return renderer;
            }

            _semaphore.Release();
            throw new CwsEditorException("No export renderer was available.");
        }

        public void Return(CwsRenderService renderer)
        {
            _renderers.Add(renderer);
            _semaphore.Release();
        }

        public void Dispose()
        {
            while (_renderers.TryTake(out CwsRenderService? renderer))
            {
                renderer.Dispose();
            }

            _semaphore.Dispose();
        }
    }
}
