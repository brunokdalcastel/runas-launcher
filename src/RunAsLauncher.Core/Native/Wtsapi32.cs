using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

internal static partial class Wtsapi32
{
    /// <summary>Token primário do usuário interativo da sessão informada. Exige SeTcbPrivilege.</summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint sessionId, out SafeKernelHandle token);
}
