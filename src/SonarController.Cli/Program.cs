using SonarController.Core;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var logger = new ConsoleSonarLogger();
    using var controller = new SteelSeriesSonarController(logger);

    try
    {
        if (args.Length == 0 || args[0].Equals("get", StringComparison.OrdinalIgnoreCase))
        {
            Print(await controller.GetSnapshotAsync());
            return 0;
        }

        if (!TryParseChannel(args[0], out var channel))
        {
            return Usage($"Unknown channel '{args[0]}'.");
        }

        // Read first so a set command never initializes Sonar from defaults.
        _ = await controller.GetSnapshotAsync();

        if (args.Length >= 3 && args[1].Equals("mute", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseMute(args[2], out var muted))
            {
                return Usage("Mute state must be on/off or true/false.");
            }
            await controller.SetMuteAsync(channel, muted);
        }
        else if (args.Length >= 2 && int.TryParse(args[1], out var percent) &&
                 percent is >= 0 and <= 100)
        {
            await controller.SetVolumeAsync(channel, percent / 100d);
        }
        else
        {
            return Usage("Volume must be an integer from 0 to 100.");
        }

        var verified = await controller.GetSnapshotAsync();
        PrintChannel(channel, verified[channel]);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        if (ex.InnerException is not null)
        {
            Console.Error.WriteLine($"DETAIL: {ex.InnerException.Message}");
        }
        return 2;
    }
}

static bool TryParseChannel(string value, out SonarChannel channel)
{
    if (value.Equals("mic", StringComparison.OrdinalIgnoreCase))
    {
        channel = SonarChannel.Mic;
        return true;
    }
    return Enum.TryParse(value, true, out channel);
}

static bool TryParseMute(string value, out bool muted)
{
    if (value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase))
    {
        muted = true;
        return true;
    }
    if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("false", StringComparison.OrdinalIgnoreCase))
    {
        muted = false;
        return true;
    }
    muted = false;
    return false;
}

static void Print(SonarSnapshot snapshot)
{
    foreach (var channel in Enum.GetValues<SonarChannel>())
    {
        PrintChannel(channel, snapshot[channel]);
    }
}

static void PrintChannel(SonarChannel channel, SonarChannelState state) =>
    Console.WriteLine(
        $"{channel.ToString().ToUpperInvariant(),-6} {state.Volume * 100,6:0.0}%  " +
        $"{(state.IsMuted ? "MUTED" : "ACTIVE")}");

static int Usage(string error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project src/SonarController.Cli -- get");
    Console.Error.WriteLine("  dotnet run --project src/SonarController.Cli -- game 50");
    Console.Error.WriteLine("  dotnet run --project src/SonarController.Cli -- mic mute on");
    return 1;
}

file sealed class ConsoleSonarLogger : ISonarLogger
{
    public void Info(string category, string message) =>
        Console.Error.WriteLine($"[{category}] {message}");

    public void Error(string category, string message, Exception? exception = null) =>
        Console.Error.WriteLine($"[{category}] {message}: {exception?.Message}");
}
