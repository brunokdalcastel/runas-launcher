using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

internal enum TOKEN_INFORMATION_CLASS
{
    TokenSessionId = 12,
    TokenElevationType = 18,
    TokenLinkedToken = 19,
    TokenElevation = 20,
    TokenIntegrityLevel = 25,
}

internal enum TOKEN_ELEVATION_TYPE
{
    Default = 1,
    Full = 2,
    Limited = 3,
}

internal enum SECURITY_IMPERSONATION_LEVEL
{
    Anonymous = 0,
    Identification = 1,
    Impersonation = 2,
    Delegation = 3,
}

internal enum TOKEN_TYPE
{
    Primary = 1,
    Impersonation = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public IntPtr Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_GROUPS_ONE
{
    public uint GroupCount;
    public SID_AND_ATTRIBUTES Group0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES_ONE
{
    public uint PrivilegeCount;
    public LUID_AND_ATTRIBUTES Privilege0;
}

internal static partial class Advapi32
{
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;

    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    // CreateRestrictedToken flags.
    public const uint DISABLE_MAX_PRIVILEGE = 0x1;
    public const uint LUA_TOKEN = 0x4;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeKernelHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(
        SafeKernelHandle tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTokenInformation(
        SafeKernelHandle tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(
        SafeKernelHandle existingToken, uint desiredAccess, IntPtr tokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out SafeKernelHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateRestrictedToken(
        SafeKernelHandle existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable,
        uint deletePrivilegeCount, IntPtr privilegesToDelete,
        uint restrictedSidCount, IntPtr sidsToRestrict,
        out SafeKernelHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessAsUserW(
        SafeKernelHandle token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        SafeKernelHandle tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES_ONE newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSidToSidW(string stringSid, out SafeLocalAllocHandle sid);
}

/// <summary>Buffer devolvido por APIs que alocam com <c>LocalAlloc</c> (ex.: <c>ConvertStringSidToSid</c>).</summary>
internal sealed partial class SafeLocalAllocHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeLocalAllocHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
