using System.Runtime.InteropServices;

namespace RunAsLauncher.Native;

internal static partial class Userenv
{
    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateEnvironmentBlock(
        out SafeEnvironmentBlock environment, SafeKernelHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyEnvironmentBlock(IntPtr environment);
}
