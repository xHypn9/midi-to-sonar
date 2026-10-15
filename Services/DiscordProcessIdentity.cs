using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SonarMidiController.App.Services;

public static class DiscordProcessIdentity
{
    public static bool IsDiscord(int pid)
    {
        // Never infer identity from the session display name or retain a PID cache.
        using var process = Process.GetProcessById(pid);
        var path = process.MainModule?.FileName;
        if (path is null || !IsDiscordExecutableName(Path.GetFileName(path))) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.CompanyName, "Discord Inc.", StringComparison.OrdinalIgnoreCase)) return false;
        using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
        using var certificate = new X509Certificate2(signedCertificate);
        if (!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false),
                "Discord Inc.", StringComparison.OrdinalIgnoreCase)) return false;
        return HasValidSignature(path);
    }

    public static bool IsDiscordExecutableName(string name) =>
        name.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordPTB.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DiscordCanary.exe", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidSignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2,
                UnionChoice = 1, File = pointer,
                // Local verification only; avoid network stalls in the audio worker.
                ProviderFlags = 0x1000
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new nint(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint Handle;
        public nint KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallback;
        public nint SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint Url;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
