using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RunAsLauncher.Native;

/// <summary>Handle do kernel (token, processo, thread, job) fechado com <c>CloseHandle</c>.</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(ownsHandle: true) { }

    public SafeKernelHandle(IntPtr existing) : base(ownsHandle: true) => SetHandle(existing);

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Conexão com o LSA obtida por <c>LsaRegisterLogonProcess</c>.</summary>
internal sealed class SafeLsaHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeLsaHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => Secur32.LsaDeregisterLogonProcess(handle) == 0;
}

/// <summary>Buffer alocado pelo LSA (ex.: profile buffer do <c>LsaLogonUser</c>).</summary>
internal sealed class SafeLsaBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    // Construtor sem parâmetros exigido pela marshalling gerada para parâmetros `out`.
    public SafeLsaBuffer() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => Secur32.LsaFreeReturnBuffer(handle) == 0;
}

/// <summary>Bloco de ambiente criado por <c>CreateEnvironmentBlock</c>.</summary>
internal sealed class SafeEnvironmentBlock : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeEnvironmentBlock() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => Userenv.DestroyEnvironmentBlock(handle);
}

/// <summary>Lista de atributos de criação de processo (memória própria + <c>DeleteProcThreadAttributeList</c>).</summary>
internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcThreadAttributeList(IntPtr buffer) : base(ownsHandle: true) => SetHandle(buffer);

    public static SafeProcThreadAttributeList Create(int attributeCount)
    {
        nuint size = 0;
        Kernel32.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size); // falha por design
        IntPtr buffer = Marshal.AllocHGlobal((nint)size);
        if (!Kernel32.InitializeProcThreadAttributeList(buffer, attributeCount, 0, ref size))
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            throw new System.ComponentModel.Win32Exception(error);
        }

        return new SafeProcThreadAttributeList(buffer);
    }

    protected override bool ReleaseHandle()
    {
        Kernel32.DeleteProcThreadAttributeList(handle);
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
