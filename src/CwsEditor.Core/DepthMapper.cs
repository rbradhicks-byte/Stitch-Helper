namespace CwsEditor.Core;

public sealed class DepthMapper
{
    private readonly IReadOnlyList<DepthSample> _depthSamples;
    private readonly IReadOnlyList<DisplacementSample> _displacements;

    public DepthMapper(CwsDocument document)
    {
        _depthSamples = document.DepthSamples.OrderBy(sample => sample.TimestampUtc).ToArray();
        _displacements = document.StitchMetadata.Displacements.OrderBy(sample => sample.RegionY).ToArray();
    }

    public DateTimeOffset GetJobTimeAtSourceY(double sourceY)
    {
        if (_displacements.Count == 0)
        {
            return _depthSamples.Count > 0 ? _depthSamples[0].TimestampUtc : DateTimeOffset.UtcNow;
        }

        if (_displacements.Count == 1)
        {
            return _displacements[0].JobTimeUtc;
        }

        int upperIndex = FindUpperIndex(sourceY);
        int lowerIndex = Math.Max(0, upperIndex - 1);
        DisplacementSample lower = _displacements[lowerIndex];
        DisplacementSample upper = _displacements[Math.Min(upperIndex, _displacements.Count - 1)];
        if (Math.Abs(upper.RegionY - lower.RegionY) < 0.0001d)
        {
            return lower.JobTimeUtc;
        }

        double fraction = Math.Clamp((sourceY - lower.RegionY) / (upper.RegionY - lower.RegionY), 0d, 1d);
        long ticks = lower.JobTimeUtc.UtcTicks + (long)((upper.JobTimeUtc.UtcTicks - lower.JobTimeUtc.UtcTicks) * fraction);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public DepthInfo GetDepthInfoAtSourceY(double sourceY)
    {
        DateTimeOffset timestamp = GetJobTimeAtSourceY(sourceY);
        return GetDepthInfoAtTime(timestamp);
    }

    public DepthInfo GetDepthInfoAtTime(DateTimeOffset timestamp)
    {
        if (_depthSamples.Count == 0)
        {
            return new DepthInfo(timestamp, 0d, 0d);
        }

        if (_depthSamples.Count == 1)
        {
            DepthSample only = _depthSamples[0];
            return new DepthInfo(timestamp, only.Depth, only.Orientation);
        }

        int upperIndex = FindDepthUpperIndex(timestamp);
        int lowerIndex = Math.Max(0, upperIndex - 1);
        DepthSample lower = _depthSamples[lowerIndex];
        DepthSample upper = _depthSamples[Math.Min(upperIndex, _depthSamples.Count - 1)];
        if (upper.TimestampUtc == lower.TimestampUtc)
        {
            return new DepthInfo(timestamp, lower.Depth, lower.Orientation);
        }

        double fraction = Math.Clamp(
            (timestamp - lower.TimestampUtc).TotalMilliseconds /
            (upper.TimestampUtc - lower.TimestampUtc).TotalMilliseconds,
            0d,
            1d);
        return new DepthInfo(
            timestamp,
            lower.Depth + ((upper.Depth - lower.Depth) * fraction),
            lower.Orientation + ((upper.Orientation - lower.Orientation) * fraction));
    }

    private int FindUpperIndex(double sourceY)
    {
        int low = 0;
        int high = _displacements.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_displacements[mid].RegionY < sourceY)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Math.Clamp(low, 0, _displacements.Count - 1);
    }

    private int FindDepthUpperIndex(DateTimeOffset timestamp)
    {
        int low = 0;
        int high = _depthSamples.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_depthSamples[mid].TimestampUtc < timestamp)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Math.Clamp(low, 0, _depthSamples.Count - 1);
    }
}
