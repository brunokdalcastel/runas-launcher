using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RunAsLauncher.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOW
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
    public int dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFOEXW
{
    public STARTUPINFOW StartupInfo;
    public IntPtr lpAttributeList;
}

internal static partial class Kernel32
{
    public const int STARTF_USESHOWWINDOW = 0x1;

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    public const uint CREATE_NEW_CONSOLE = 0x10;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000;

    // Atributos de criação de processo (ProcThreadAttribute).
    public static readonly nuint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x2000D;
    public static readonly nuint PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY = 0x2000E;
    public const uint PROCESS_CREATION_CHILD_PROCESS_RESTRICTED = 0x1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, nuint attribute, IntPtr lpValue, nuint cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    // Caminho final (resolvendo junctions/symlinks) do arquivo já aberto; usado por LockedTarget
    // para recusar o alvo se o caminho real divergir do caminho da política.
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, IntPtr lpszFilePath, uint cchFilePath, uint dwFlags);
}
