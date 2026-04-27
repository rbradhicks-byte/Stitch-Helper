namespace CwsEditor.Core;

public sealed record CwsExportOptions
{
    public static CwsExportOptions Default { get; } = new();

    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    public int PngCompressionLevel { get; init; } = 1;

    public bool StorePngEntries { get; init; } = true;

    public bool CopyUnchangedStrips { get; init; } = true;

    public CwsExportOptions Normalize() =>
        this with
        {
            MaxDegreeOfParallelism = Math.Clamp(MaxDegreeOfParallelism, 1, 8),
            PngCompressionLevel = Math.Clamp(PngCompressionLevel, 0, 9),
        };
}
