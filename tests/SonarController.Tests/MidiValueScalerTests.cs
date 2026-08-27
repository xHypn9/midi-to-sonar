using SonarController.Midi;

namespace SonarController.Tests;

public sealed class MidiValueScalerTests
{
    [Theory]
    [InlineData(0, 0d)]
    [InlineData(64, 64d / 127d)]
    [InlineData(127, 1d)]
    public void ConvertsMidiCcToNormalizedVolume(int input, double expected)
    {
        Assert.Equal(expected, MidiValueScaler.ToNormalized(input), precision: 10);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(127, 127)]
    [InlineData(150, 127)]
    public void ClampsMidiCcRange(int input, int expected)
    {
        Assert.Equal(expected, MidiValueScaler.Clamp(input));
    }
}
