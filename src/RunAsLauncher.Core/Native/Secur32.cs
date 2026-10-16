using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

internal enum SECURITY_LOGON_TYPE
{
    Interactive = 2,
    Network = 3,
    Batch = 4,
    Service = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>String ANSI do LSA (<c>LSA_STRING</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LSA_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

/// <summary><c>UNICODE_STRING</c>: Length/MaximumLength em bytes, Buffer UTF-16.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TOKEN_SOURCE
{
    public fixed byte SourceName[8];
    public LUID SourceIdentifier;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QUOTA_LIMITS
{
    public nuint PagedPoolLimit;
    public nuint NonPagedPoolLimit;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public nuint PagefileLimit;
    public long TimeLimit;
}

/// <summary>
/// Submit type das mensagens de logon do MSV1_0 e do Kerberos. S4U vale 12 em ambos
/// (<c>MsV1_0S4ULogon</c> / <c>KerbS4ULogon</c>).
/// </summary>
internal enum LogonSubmitType
{
    MsV1_0S4ULogon = 12,
    KerbS4ULogon = 12,
}

internal static partial class Secur32
{
    public const uint SE_GROUP_MANDATORY = 0x1;
    public const uint SE_GROUP_ENABLED_BY_DEFAULT = 0x2;
    public const uint SE_GROUP_ENABLED = 0x4;

    [LibraryImport("secur32.dll")]
    public static partial int LsaRegisterLogonProcess(
        ref LSA_STRING logonProcessName, out SafeLsaHandle lsaHandle, out LUID securityMode);

    [LibraryImport("secur32.dll")]
    public static partial int LsaDeregisterLogonProcess(IntPtr lsaHandle);

    [LibraryImport("secur32.dll")]
    public static partial int LsaLookupAuthenticationPackage(
        SafeLsaHandle lsaHandle, ref LSA_STRING packageName, out uint authenticationPackage);

    [LibraryImport("secur32.dll")]
    public static partial int LsaLogonUser(
        SafeLsaHandle lsaHandle,
        ref LSA_STRING originName,
        SECURITY_LOGON_TYPE logonType,
        uint authenticationPackage,
        IntPtr authenticationInformation,
        uint authenticationInformationLength,
        IntPtr localGroups,
        ref TOKEN_SOURCE sourceContext,
        out SafeLsaBuffer profileBuffer,
        out uint profileBufferLength,
        out LUID logonId,
        out SafeKernelHandle token,
        out QUOTA_LIMITS quotas,
        out int subStatus);

    [LibraryImport("secur32.dll")]
    public static partial int LsaFreeReturnBuffer(IntPtr buffer);

    [LibraryImport("advapi32.dll")]
    public static partial int LsaNtStatusToWinError(int status);
}
