using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RunAsLauncher.Native;

namespace RunAsLauncher.Verification;

/// <summary>
/// Abre o alvo somente para leitura, compartilhando apenas leitura (sem Write/Delete), e mantém o
/// handle aberto do começo da verificação (tamanho/hash/assinatura) até depois do
/// <c>CreateProcessAsUser</c> — fechando a janela TOCTOU em que o arquivo seria trocado (R4).
/// Recusa o alvo se o caminho real (resolvendo junctions/symlinks) divergir do caminho da política.
/// </summary>
public sealed class LockedTarget : IDisposable
{
    private LockedTarget(FileStream stream, string finalPath)
    {
        Stream = stream;
        FinalPath = finalPath;
    }

    /// <summary>Stream aberto do alvo (posicionável, somente leitura).</summary>
    public FileStream Stream { get; }

    /// <summary>Tamanho do arquivo em bytes.</summary>
    public long Length => Stream.Length;

    /// <summary>Caminho final normalizado, sem o prefixo <c>\\?\</c>.</summary>
    public string FinalPath { get; }

    /// <summary>
    /// Abre e bloqueia <paramref name="targetPath"/>. Lança <see cref="TargetMismatchException"/>
    /// se o caminho final divergir, ou <see cref="Win32Exception"/>/<see cref="IOException"/> em falha de abertura.
    /// </summary>
    public static LockedTarget Open(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string expected = Path.GetFullPath(targetPath);

        var stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string final = GetFinalPath(stream.SafeFileHandle);
            if (!PathsEqual(final, expected))
            {
                throw new TargetMismatchException(expected, final);
            }

            return new LockedTarget(stream, Strip(final));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => Stream.Dispose();

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string path)
    {
        const string prefix = @"\\?\";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            path = path[prefix.Length..];
        }

        return path.TrimEnd('\\');
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        uint needed = Kernel32.GetFinalPathNameByHandle(handle, IntPtr.Zero, 0, 0);
        if (needed == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed * sizeof(char)));
        try
        {
            uint written = Kernel32.GetFinalPathNameByHandle(handle, buffer, needed, 0);
            if (written == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUni(buffer, (int)written) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>O caminho final do alvo (após resolver junctions/symlinks) não bate com o da política.</summary>
public sealed class TargetMismatchException : Exception
{
    public TargetMismatchException(string expected, string actual)
        : base($"O caminho final do alvo ('{actual}') difere do caminho da política ('{expected}').")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
}
