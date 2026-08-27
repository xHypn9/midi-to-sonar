namespace SonarController.Midi;

public static class MidiValueScaler
{
    public static int Clamp(int value) => Math.Clamp(value, 0, 127);

    public static double ToNormalized(int value) => Clamp(value) / 127d;
}
