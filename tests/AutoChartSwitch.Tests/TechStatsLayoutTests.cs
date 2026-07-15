using AutoChartSwitch.App;

namespace AutoChartSwitch.Tests;

public sealed class TechStatsLayoutTests
{
    [Fact]
    public void GeometryMatchesAuthoritativeReferenceCapture()
    {
        Assert.Equal(508, TechStatsLayout.Width);
        Assert.Equal(200, TechStatsLayout.Height);
        Assert.Equal(189, TechStatsLayout.BarLeft);
        Assert.Equal(25, TechStatsLayout.BarHeight);
        Assert.Equal(240, TechStatsLayout.GetBarWidth(200));
        Assert.Equal(438, TechStatsLayout.GetNumberLeft(3));
    }

    [Fact]
    public void RowSpacingMatchesFiveAndSixRowLayouts()
    {
        Assert.Equal([0d, 35d, 70d, 105d, 140d, 175d],
            Enumerable.Range(0, 6).Select(row => TechStatsLayout.GetRowTop(row, true)));
        Assert.Equal([0d, 40d, 80d, 120d, 160d],
            Enumerable.Range(0, 5).Select(row => TechStatsLayout.GetRowTop(row, false)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(41, 50)]
    [InlineData(139, 170)]
    [InlineData(158, 190)]
    [InlineData(161, 195)]
    [InlineData(200, 240)]
    [InlineData(400, 240)]
    public void BarWidthsUseFivePixelGameMakerRasterSteps(double value, double expected)
    {
        Assert.Equal(expected, TechStatsLayout.GetBarWidth(value));
    }
}
