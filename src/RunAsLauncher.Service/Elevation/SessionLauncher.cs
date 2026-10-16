using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RunAsLauncher.Native;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Elevation;

/// <summary>
/// Cria o processo do item na área de trabalho interativa do usuário, com o token já preparado pelo
/// <see cref="LaunchService"/>. A linha de comando vem só da política (nunca do cliente).
/// </summary>
internal sealed class SessionLauncher : IDisposable
{
    private const string Desktop = "winsta0\\default";

    // Jobs com KILL_ON_JOB_CLOSE ficam vivos até o processo principal terminar; ao fechar o handle,
    // o Windows encerra os filhos que restaram (ChildProcessPolicy.KillTreeOnExit).
    private readonly ConcurrentDictionary<int, TrackedJob> _jobs = new();

    /// <summary>Lança o item e devolve o PID. Lança <see cref="Win32Exception"/> em falha nativa.</summary>
    public int Start(SafeKernelHandle token, PolicyItem item)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(item);

        if (!Userenv.CreateEnvironmentBlock(out SafeEnvironmentBlock environment, token, inherit: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock falhou");
        }

        SafeKernelHandle? job = item.Children == ChildProcessPolicy.KillTreeOnExit ? CreateKillOnCloseJob() : null;
        bool denyChildren = item.Children == ChildProcessPolicy.Deny;
        int attributeCount = (job is null ? 0 : 1) + (denyChildren ? 1 : 0);

        string commandLine = "\"" + item.TargetPath + "\" " + (item.Arguments ?? string.Empty);
        string workingDirectory = item.WorkingDirectory ?? Path.GetDirectoryName(item.TargetPath)!;

        IntPtr desktop = Marshal.StringToHGlobalUni(Desktop);
        IntPtr commandBuffer = Marshal.StringToHGlobalUni(commandLine);
        IntPtr jobList = IntPtr.Zero;
        IntPtr childPolicy = IntPtr.Zero;
        SafeProcThreadAttributeList? attributes = null;
        try
        {
            if (attributeCount > 0)
            {
                attributes = SafeProcThreadAttributeList.Create(attributeCount);

                if (job is not null)
                {
                    jobList = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
                    UpdateAttribute(attributes, Kernel32.PROC_THREAD_ATTRIBUTE_JOB_LIST, jobList, (nuint)IntPtr.Size);
                }

                if (denyChildren)
                {
                    childPolicy = Marshal.AllocHGlobal(sizeof(uint));
                    Marshal.WriteInt32(childPolicy, (int)Kernel32.PROCESS_CREATION_CHILD_PROCESS_RESTRICTED);
                    UpdateAttribute(attributes, Kernel32.PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, childPolicy, sizeof(uint));
                }
            }

            var startup = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    lpDesktop = desktop,
                    dwFlags = Kernel32.STARTF_USESHOWWINDOW,
                    wShowWindow = ShowWindowFor(item.Window),
                },
                lpAttributeList = attributes?.DangerousGetHandle() ?? IntPtr.Zero,
            };

            uint flags = Kernel32.EXTENDED_STARTUPINFO_PRESENT | Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_NEW_CONSOLE;

            PROCESS_INFORMATION pi;
            bool created;
            unsafe
            {
                created = ServiceNative.CreateProcessAsUserW(
                    token, null, commandBuffer, IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                    flags, environment.DangerousGetHandle(), workingDirectory, &startup, &pi);
            }

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser falhou");
            }

            Kernel32.CloseHandle(pi.hThread);
            int pid = (int)pi.dwProcessId;

            if (job is null)
            {
                Kernel32.CloseHandle(pi.hProcess);
            }
            else
            {
                Track(pid, pi.hProcess, job);
                job = null; // agora pertence ao dicionário
            }

            return pid;
        }
        finally
        {
            attributes?.Dispose();
            job?.Dispose();
            environment.Dispose();
            Marshal.FreeHGlobal(desktop);
            Marshal.FreeHGlobal(commandBuffer);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (childPolicy != IntPtr.Zero) Marshal.FreeHGlobal(childPolicy);
        }
    }

    public void Dispose()
    {
        foreach (var pid in _jobs.Keys)
        {
            if (_jobs.TryRemove(pid, out var tracked))
            {
                tracked.Dispose();
            }
        }
    }

    private void Track(int pid, IntPtr process, SafeKernelHandle job)
    {
        var waitHandle = new ProcessWaitHandle(process);
        var tracked = new TrackedJob(job, waitHandle);
        _jobs[pid] = tracked;

        tracked.Registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, _) =>
            {
                if (_jobs.TryRemove(pid, out var done))
                {
                    done.Dispose(); // fecha o job → encerra os filhos restantes
                }
            },
            state: null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private static SafeKernelHandle CreateKillOnCloseJob()
    {
        SafeKernelHandle job = ServiceNative.CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject falhou");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = ServiceNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        if (!ServiceNative.SetInformationJobObject(
                job, ServiceNative.JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject falhou");
        }

        return job;
    }

    private static void UpdateAttribute(SafeProcThreadAttributeList list, nuint attribute, IntPtr value, nuint size)
    {
        if (!Kernel32.UpdateProcThreadAttribute(list.DangerousGetHandle(), 0, attribute, value, size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"UpdateProcThreadAttribute(0x{attribute:X}) falhou");
        }
    }

    private static ushort ShowWindowFor(WindowMode mode) => mode switch
    {
        WindowMode.Hidden => ServiceNative.SW_HIDE,
        WindowMode.Minimized => ServiceNative.SW_SHOWMINIMIZED,
        WindowMode.Maximized => ServiceNative.SW_SHOWMAXIMIZED,
        _ => ServiceNative.SW_SHOWNORMAL,
    };

    /// <summary><see cref="WaitHandle"/> sobre o handle do processo (sinalizado quando ele termina).</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        // O SafeWaitHandle passa a ser o dono do handle do processo (fechado no Dispose).
        public ProcessWaitHandle(IntPtr process) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, ownsHandle: true);
    }

    private sealed class TrackedJob(SafeKernelHandle job, ProcessWaitHandle process) : IDisposable
    {
        public RegisteredWaitHandle? Registration { get; set; }

        public void Dispose()
        {
            Registration?.Unregister(null);
            job.Dispose();
            process.Dispose();
        }
    }
}
