using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RunAsLauncher.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
    public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>APIs nativas usadas só pelo serviço (pipe, job objects, criação de processo).</summary>
internal static partial class ServiceNative
{
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public const ushort SW_HIDE = 0;
    public const ushort SW_SHOWNORMAL = 1;
    public const ushort SW_SHOWMINIMIZED = 2;
    public const ushort SW_SHOWMAXIMIZED = 3;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    public static partial SafeKernelHandle CreateJobObject(IntPtr jobAttributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(
        SafeKernelHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint infoLength);

    /// <summary>
    /// Variante com <paramref name="commandLine"/> como buffer não gerenciado: <c>CreateProcessW</c> pode
    /// escrever na linha de comando, então ela não pode apontar para uma <see cref="string"/> .NET.
    /// As structs vêm do Core (outro assembly), por isso vão como ponteiros.
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcessAsUserW(
        SafeKernelHandle token, string? applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        STARTUPINFOEXW* startupInfo, PROCESS_INFORMATION* processInformation);

    /// <summary>
    /// Com o nome do computador devolve o SID do domínio de contas local (<c>SidTypeDomain</c>);
    /// <c>NTAccount.Translate</c> não aceita esse tipo de SID.
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountNameW(
        string? systemName, string accountName, [Out] byte[]? sid, ref uint cbSid,
        [Out] char[]? referencedDomainName, ref uint cchReferencedDomainName, out int use);
}
