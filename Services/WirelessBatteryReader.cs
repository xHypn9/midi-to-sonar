using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SonarMidiController.App.Services;

internal sealed record WirelessBattery(string Name, string Icon, int? Percentage = null, bool Estimated = false)
{
    public string DisplayLevel => Percentage is int value ? $"{(Estimated ? "~" : "")}{value}%" : "—";
    public string Description => Percentage is null ? $"{Name}: battery unavailable" : $"{Name}: {DisplayLevel}";
}

internal static class WirelessBatteryReader
{
    private static readonly Guid HidInterfaceId = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private const uint ReadWriteAccess = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint OverlappedIo = 0x40000000;
    private const byte LongReport = 0x11;
    private const byte ClientId = 0x0D;

    // Sparse voltage/charge reference points for the two supported battery profiles.
    private static readonly (int Millivolts, int Percent)[] MouseCurve =
        [(3537, 1), (3677, 11), (3720, 21), (3754, 31), (3781, 41), (3815, 51),
         (3865, 61), (3929, 71), (3997, 81), (4075, 91), (4186, 100)];

    private static readonly (int Millivolts, int Percent)[] HeadsetCurve =
        [(3399, 1), (3569, 11), (3599, 21), (3630, 31), (3661, 41), (3696, 51),
         (3732, 61), (3773, 71), (3820, 81), (3919, 91), (4030, 100)];

    public static async Task<WirelessBattery[]> ReadAsync(CancellationToken cancellationToken)
    {
        var paths = GetHidPaths();
        var mouse = ReadDeviceAsync(paths, "vid_046d&pid_c53a&", 1,
            new("POWERPLAY mouse", "\uE962"), false, cancellationToken);
        var headset = ReadDeviceAsync(paths, "vid_046d&pid_0a87&", 0xFF,
            new("G935", "\uE7F6"), true, cancellationToken);
        var batteries = await Task.WhenAll(mouse, headset).ConfigureAwait(false);
        return batteries.OfType<WirelessBattery>().ToArray();
    }

    private static async Task<WirelessBattery?> ReadDeviceAsync(string[] paths, string deviceId,
        byte slot, WirelessBattery device, bool isHeadset, CancellationToken cancellationToken)
    {
        var matchingPaths = paths.Where(path => path.Contains(deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var path in matchingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = CreateFile(path, ReadWriteAccess, ShareReadWrite, IntPtr.Zero, OpenExisting,
                OverlappedIo, IntPtr.Zero);
            if (handle.IsInvalid || !TryGetCaps(handle, out var caps) ||
                caps.UsagePage < 0xFF00 || caps.InputLength < 20 || caps.OutputLength < 20)
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
                var battery = await ReadBatteryAsync(stream, caps, slot, device, isHeadset, cancellationToken)
                    .ConfigureAwait(false);
                if (battery is not null)
                {
                    return battery;
                }
            }
            catch (IOException)
            {
                // The USB receiver can disappear between enumeration, open and I/O.
            }
        }

        return matchingPaths.Length > 0 ? device : null;
    }

    private static async Task<WirelessBattery?> ReadBatteryAsync(FileStream stream, HidCaps caps,
        byte slot, WirelessBattery device, bool isHeadset, CancellationToken cancellationToken)
    {
        ushort[] features = isHeadset ? [0x1F20] : [0x1004, 0x1000, 0x1001];
        for (var i = 0; i < features.Length; i++)
        {
            var feature = features[i];
            // Feature lookups share the same command. Give each a distinct reply address.
            var root = await QueryAsync(stream, caps, slot, 0, 0, (byte)(feature >> 8), (byte)feature,
                cancellationToken, (byte)(ClientId - i)).ConfigureAwait(false);
            if (root is null || root[4] == 0)
            {
                continue;
            }

            var index = root[4];
            if (feature == 0x1004)
            {
                var capabilities = await QueryAsync(stream, caps, slot, index, 0, 0, 0, cancellationToken)
                    .ConfigureAwait(false);
                if (capabilities is null || (capabilities[5] & 2) == 0)
                {
                    continue;
                }
            }

            var reply = await QueryAsync(stream, caps, slot, index, feature == 0x1004 ? (byte)1 : (byte)0,
                0, 0, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                continue;
            }

            if (feature is 0x1000 or 0x1004)
            {
                if (reply[4] <= 100)
                {
                    return device with { Percentage = reply[4] };
                }
                continue;
            }

            var millivolts = (reply[4] << 8) | reply[5];
            var flags = reply[6];
            if (millivolts is < 2500 or > 5000 || (isHeadset && flags is not (1 or 3 or 7)))
            {
                continue;
            }

            var full = isHeadset ? flags == 7 : (flags & 0x87) == 0x81;
            return device with
            {
                Percentage = full ? 100 : EstimatePercentage(millivolts, isHeadset ? HeadsetCurve : MouseCurve),
                Estimated = !full
            };
        }

        return null;
    }

    private static int EstimatePercentage(int millivolts, (int Millivolts, int Percent)[] curve)
    {
        if (millivolts < curve[0].Millivolts)
        {
            return 0;
        }

        for (var i = 1; i < curve.Length; i++)
        {
            var upper = curve[i];
            if (millivolts <= upper.Millivolts)
            {
                var lower = curve[i - 1];
                var fraction = (double)(millivolts - lower.Millivolts) / (upper.Millivolts - lower.Millivolts);
                return (int)Math.Round(lower.Percent + fraction * (upper.Percent - lower.Percent));
            }
        }

        return 100;
    }

    private static async Task<byte[]?> QueryAsync(FileStream stream, HidCaps caps, byte slot,
        byte feature, byte function, byte firstParameter, byte secondParameter, CancellationToken cancellationToken,
        byte clientId = ClientId)
    {
        var address = (byte)((function << 4) | clientId);
        var output = new byte[caps.OutputLength];
        output[0] = LongReport;
        output[1] = slot;
        output[2] = feature;
        output[3] = address;
        output[4] = firstParameter;
        output[5] = secondParameter;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(650));
        try
        {
            await stream.WriteAsync(output, timeout.Token).ConfigureAwait(false);
            var reply = new byte[caps.InputLength];
            while (await stream.ReadAsync(reply, timeout.Token).ConfigureAwait(false) >= 7)
            {
                if (reply[0] is not (0x10 or LongReport) || reply[1] != slot)
                {
                    continue;
                }
                if (reply[2] == feature && reply[3] == address)
                {
                    return reply;
                }
                if (reply[2] is 0xFF or 0x8F && reply[3] == feature && reply[4] == address)
                {
                    return null;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A sleeping accessory may not answer until it is used again.
        }

        return null;
    }

    private static string[] GetHidPaths()
    {
        const uint presentOnly = 0;
        const uint bufferSmall = 0x1A;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in HidInterfaceId, IntPtr.Zero, presentOnly) != 0)
            {
                break;
            }

            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(in HidInterfaceId, IntPtr.Zero, buffer, length, presentOnly);
            if (result == 0)
            {
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            if (result != bufferSmall)
            {
                break;
            }
        }
        return [];
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return false;
        }
        try
        {
            return HidP_GetCaps(preparsed, out caps) == 0x00110000;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputLength;
        public ushort OutputLength;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length,
        in Guid interfaceId, IntPtr deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(
        in Guid interfaceId, IntPtr deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HidCaps caps);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
}
