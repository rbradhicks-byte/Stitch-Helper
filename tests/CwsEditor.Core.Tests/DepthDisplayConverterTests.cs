using CwsEditor.Core;

namespace CwsEditor.Core.Tests;

public sealed class DepthDisplayConverterTests
{
    [Fact]
    public void ConvertDepthKeepsMetersForMetricDisplay()
    {
        double result = DepthDisplayConverter.ConvertDepth(12.5d, SourceDepthUnit.Meters, DisplayDepthUnit.Metric);

        Assert.Equal(12.5d, result, 6);
        Assert.Equal("m", DepthDisplayConverter.GetUnitLabel(DisplayDepthUnit.Metric));
    }

    [Fact]
    public void ConvertDepthConvertsMetersToFeetForImperialDisplay()
    {
        double result = DepthDisplayConverter.ConvertDepth(10d, SourceDepthUnit.Meters, DisplayDepthUnit.Imperial);

        Assert.Equal(32.80839895d, result, 6);
        Assert.Equal("ft", DepthDisplayConverter.GetUnitLabel(DisplayDepthUnit.Imperial));
    }

    [Fact]
    public void ConvertDepthConvertsFeetToMetersForMetricDisplay()
    {
        double result = DepthDisplayConverter.ConvertDepth(32.80839895d, SourceDepthUnit.Feet, DisplayDepthUnit.Metric);

        Assert.Equal(10d, result, 6);
    }
}
