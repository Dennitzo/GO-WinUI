using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace GoWinUI.BricsCad.Protocol;

internal static class BridgeRendezvousSecurity
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint SecurityDescriptorRevision = 1;

    public static void ApplyCurrentUserOnly(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyCurrentUserOnlyWindows(path, isDirectory);
        VerifyCurrentUserOnlyWindows(path);
    }

    public static void VerifyCurrentUserOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyCurrentUserOnlyWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyCurrentUserOnlyWindows(string path, bool isDirectory)
    {
        string sid = GetCurrentUserSid();
        string inheritance = isDirectory ? "OICI" : string.Empty;
        string descriptorText = $"D:P(A;{inheritance};FA;;;{sid})";

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                descriptorText,
                SecurityDescriptorRevision,
                out nint descriptor,
                out _))
        {
            throw CreateSecurityException(path, "create security descriptor");
        }

        try
        {
            uint information = DaclSecurityInformation | ProtectedDaclSecurityInformation;
            if (!SetFileSecurity(path, information, descriptor))
            {
                throw CreateSecurityException(path, "set current-user ACL");
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCurrentUserOnlyWindows(string path)
    {
        const uint information = DaclSecurityInformation;
        _ = GetFileSecurity(path, information, null, 0, out uint requiredBytes);
        int initialError = Marshal.GetLastWin32Error();
        if (requiredBytes == 0)
        {
            throw new UnauthorizedAccessException(
                $"Unable to inspect bridge ACL for '{path}': {new Win32Exception(initialError).Message}");
        }

        byte[] descriptor = new byte[requiredBytes];
        if (!GetFileSecurity(path, information, descriptor, requiredBytes, out _))
        {
            throw CreateSecurityException(path, "read ACL");
        }

        if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                descriptor,
                SecurityDescriptorRevision,
                information,
                out nint descriptorTextPointer,
                out _))
        {
            throw CreateSecurityException(path, "format ACL");
        }

        try
        {
            string descriptorText = Marshal.PtrToStringUni(descriptorTextPointer) ?? string.Empty;
            string sid = GetCurrentUserSid();
            int aceCount = descriptorText.Count(character => character == '(');
            if (!descriptorText.StartsWith("D:P", StringComparison.Ordinal)
                || aceCount != 1
                || !descriptorText.Contains($";;;{sid})", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Bridge rendezvous ACL for '{path}' is not restricted to the current Windows user.");
            }
        }
        finally
        {
            _ = LocalFree(descriptorTextPointer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new UnauthorizedAccessException("The current Windows user has no SID.");
    }

    private static UnauthorizedAccessException CreateSecurityException(string path, string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new UnauthorizedAccessException(
            $"Unable to {operation} for bridge path '{path}': {new Win32Exception(error).Message}");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetFileSecurityW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(
        string fileName,
        uint securityInformation,
        nint securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "GetFileSecurityW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(
        string fileName,
        uint requestedInformation,
        [Out] byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        byte[] securityDescriptor,
        uint requestedStringSecurityDescriptorRevision,
        uint securityInformation,
        out nint stringSecurityDescriptor,
        out uint stringSecurityDescriptorLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);
}
