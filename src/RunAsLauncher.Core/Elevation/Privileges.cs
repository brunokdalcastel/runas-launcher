using System.ComponentModel;
using System.Runtime.InteropServices;
using RunAsLauncher.Native;

namespace RunAsLauncher.Elevation;

/// <summary>Habilita privilégios no token do processo atual (o serviço roda como LocalSystem, que os possui).</summary>
internal static class Privileges
{
    public const string SeTcbPrivilege = "SeTcbPrivilege";

    public static void Enable(string privilegeName)
    {
        // Pseudo-handle do processo atual (não precisa ser fechado).
        IntPtr currentProcess = -1;
        if (!Advapi32.OpenProcessToken(
                currentProcess,
                Advapi32.TOKEN_ADJUST_PRIVILEGES | Advapi32.TOKEN_QUERY,
                out SafeKernelHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcessToken falhou para {privilegeName}");
        }

        using (token)
        {
            if (!Advapi32.LookupPrivilegeValueW(null, privilegeName, out LUID luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue falhou para {privilegeName}");
            }

            var privileges = new TOKEN_PRIVILEGES_ONE
            {
                PrivilegeCount = 1,
                Privilege0 = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = Advapi32.SE_PRIVILEGE_ENABLED },
            };

            if (!Advapi32.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"AdjustTokenPrivileges falhou para {privilegeName}");
            }

            // AdjustTokenPrivileges pode devolver sucesso com ERROR_NOT_ALL_ASSIGNED.
            int lastError = Marshal.GetLastWin32Error();
            if (lastError == 1300) // ERROR_NOT_ALL_ASSIGNED
            {
                throw new InvalidOperationException(
                    $"O privilégio {privilegeName} não está disponível no token atual. " +
                    "Este componente precisa rodar como LocalSystem.");
            }
        }
    }
}
