using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CwsEditor.Core;

public sealed class StitchMetadata
{
    private readonly JsonObject _root;

    private StitchMetadata(
        JsonObject root,
        IReadOnlyList<StripLayoutEntry> layoutEntries,
        IReadOnlyList<DisplacementSample> displacements,
        IReadOnlyList<MovementVector> movementVectors)
    {
        _root = root;
        LayoutEntries = layoutEntries;
        Displacements = displacements;
        MovementVectors = movementVectors;
        DisplacementStride = displacements.Count > 1
            ? (int)Math.Round(displacements[1].RegionY - displacements[0].RegionY, MidpointRounding.AwayFromZero)
            : 5;
        DisplacementRegionHeight = displacements.Count > 0
            ? (int)Math.Round(displacements[0].RegionHeight, MidpointRounding.AwayFromZero)
            : 230;
        double sourceHeight = layoutEntries.Count == 0 ? 0d : layoutEntries.Max(entry => entry.YOffset + entry.Height);
        double lastRegionY = displacements.Count == 0 ? 0d : displacements[^1].RegionY;
        DisplacementOverscan = Math.Max(0d, lastRegionY - sourceHeight);
    }

    public IReadOnlyList<StripLayoutEntry> LayoutEntries { get; }

    public IReadOnlyList<DisplacementSample> Displacements { get; }

    public IReadOnlyList<MovementVector> MovementVectors { get; }

    public int DisplacementStride { get; }

    public int DisplacementRegionHeight { get; }

    public double DisplacementOverscan { get; }

    public static StitchMetadata Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject() ?? throw new CwsEditorException("Stitch.dat is not valid JSON.");
        JsonArray layoutNode = root["layout"]?.AsArray() ?? throw new CwsEditorException("Stitch.dat does not contain a layout array.");
        JsonArray displacementNode = root["displacements"]?.AsArray() ?? throw new CwsEditorException("Stitch.dat does not contain a displacements array.");
        JsonArray? movementNode = root["debug"]?["movement"]?.AsArray();

        List<StripLayoutEntry> layoutEntries = [];
        foreach (JsonNode? node in layoutNode)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            layoutEntries.Add(
                new StripLayoutEntry(
                    item["image"]?.GetValue<string>() ?? throw new CwsEditorException("layout.image is required."),
                    item["width"]?.GetValue<int>() ?? 0,
                    item["height"]?.GetValue<int>() ?? 0,
                    item["x offset"]?.GetValue<int>() ?? 0,
                    item["y offset"]?.GetValue<int>() ?? 0));
        }

        List<DisplacementSample> displacements = [];
        foreach (JsonNode? node in displacementNode)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            string timeText = item["job time"]?.GetValue<string>() ?? throw new CwsEditorException("displacements[].job time is required.");
            if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset jobTime))
            {
                throw new CwsEditorException($"Invalid displacement timestamp: {timeText}");
            }

            displacements.Add(
                new DisplacementSample(
                    item["region x"]?.GetValue<double>() ?? 0d,
                    item["region y"]?.GetValue<double>() ?? 0d,
                    item["region width"]?.GetValue<double>() ?? 0d,
                    item["region height"]?.GetValue<double>() ?? 0d,
                    jobTime,
                    item["displacement x"]?.GetValue<double>() ?? 0d,
                    item["displacement y"]?.GetValue<double>() ?? 0d));
        }

        List<MovementVector> movement = [];
        if (movementNode is not null)
        {
            foreach (JsonNode? node in movementNode)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                movement.Add(new MovementVector(item["x"]?.GetValue<double>() ?? 0d, item["y"]?.GetValue<double>() ?? 0d));
            }
        }

        return new StitchMetadata(root, layoutEntries, displacements, movement);
    }

    public string BuildJson(
        IReadOnlyList<StripLayoutEntry> layoutEntries,
        IReadOnlyList<DisplacementSample> displacements,
        IReadOnlyList<MovementVector> movementVectors)
    {
        JsonObject root = _root.DeepClone().AsObject();
        root["layout"] = BuildLayoutArray(layoutEntries);
        root["displacements"] = BuildDisplacementArray(displacements);

        JsonObject debug = root["debug"]?.AsObject() ?? [];
        debug["movement"] = BuildMovementArray(movementVectors);
        root["debug"] = debug;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        });
    }

    private static JsonArray BuildLayoutArray(IReadOnlyList<StripLayoutEntry> layoutEntries)
    {
        JsonArray array = [];
        foreach (StripLayoutEntry entry in layoutEntries)
        {
            array.Add(
                new JsonObject
                {
                    ["image"] = entry.ImageFileName,
                    ["width"] = entry.Width,
                    ["height"] = entry.Height,
                    ["x offset"] = entry.XOffset,
                    ["y offset"] = entry.YOffset,
                });
        }

        return array;
    }

    private static JsonArray BuildDisplacementArray(IReadOnlyList<DisplacementSample> displacements)
    {
        JsonArray array = [];
        foreach (DisplacementSample sample in displacements)
        {
            array.Add(
                new JsonObject
                {
                    ["region x"] = sample.RegionX,
                    ["region y"] = sample.RegionY,
                    ["region width"] = sample.RegionWidth,
                    ["region height"] = sample.RegionHeight,
                    ["job time"] = sample.JobTimeUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["displacement x"] = sample.DisplacementX,
                    ["displacement y"] = sample.DisplacementY,
                });
        }

        return array;
    }

    private static JsonArray BuildMovementArray(IReadOnlyList<MovementVector> movementVectors)
    {
        JsonArray array = [];
        foreach (MovementVector movement in movementVectors)
        {
            array.Add(
                new JsonObject
                {
                    ["x"] = movement.X,
                    ["y"] = movement.Y,
                });
        }

        return array;
    }
}
