using CwsEditor.Core;

namespace CwsEditor.Core.Tests;

public sealed class WarpMapTests
{
    [Fact]
    public void ForwardAndInverseRoundTripAcrossEditedIntervals()
    {
        EditSession session = new()
        {
            GlobalVerticalScale = 1.2,
        };

        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "A", 10d, 20d, RegionGeometryMode.Scale, 0.5d, ToneAdjustment.Identity));
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "B", 40d, 60d, RegionGeometryMode.Scale, 1.8d, ToneAdjustment.Identity));

        WarpMap map = session.BuildWarpMap(100d);
        double[] probes = [0d, 5d, 12.5d, 19.9d, 25d, 45d, 59.5d, 75d, 99.9d];

        foreach (double sourceY in probes)
        {
            double displayY = map.Forward(sourceY);
            double roundTrip = map.Inverse(displayY);
            Assert.InRange(Math.Abs(roundTrip - sourceY), 0d, 0.0001d);
        }

        Assert.True(map.TotalDisplayHeight > 100d);
    }

    [Fact]
    public void BuildWarpMapCachesGeometryUntilVerticalEditsChange()
    {
        EditSession session = new();

        WarpMap first = session.BuildWarpMap(120d);
        WarpMap second = session.BuildWarpMap(120d);
        Assert.Same(first, second);

        session.GlobalTone = new ToneAdjustment(5d, 0d, 0d, false);
        WarpMap third = session.BuildWarpMap(120d);
        Assert.Same(first, third);

        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Tone Only", 20d, 40d, RegionGeometryMode.None, null, new ToneAdjustment(2d, 0d, 0d, false)));
        WarpMap fourth = session.BuildWarpMap(120d);
        Assert.Same(first, fourth);

        session.GlobalVerticalScale = 1.15d;
        WarpMap fifth = session.BuildWarpMap(120d);
        Assert.NotSame(first, fifth);
    }

    [Fact]
    public void CropRegionRemovesDisplayHeightButPreservesRoundTripOutsideCrop()
    {
        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Crop", 30d, 50d, RegionGeometryMode.Crop, null, ToneAdjustment.Identity));

        WarpMap map = session.BuildWarpMap(100d);

        Assert.Equal(80d, map.TotalDisplayHeight, 6);
        Assert.Equal(25d, map.Inverse(map.Forward(25d)), 6);
        Assert.Equal(75d, map.Inverse(map.Forward(75d)), 6);
    }

    [Fact]
    public void DisabledRegionDoesNotAffectWarpMap()
    {
        EditSession session = new();
        session.UpsertRegion(new EditRegion(Guid.NewGuid(), "Disabled", 10d, 40d, RegionGeometryMode.Scale, 2d, ToneAdjustment.Identity) { IsEnabled = false });

        WarpMap map = session.BuildWarpMap(100d);

        Assert.Equal(100d, map.TotalDisplayHeight, 6);
        Assert.Equal(25d, map.Inverse(map.Forward(25d)), 6);
    }
}
