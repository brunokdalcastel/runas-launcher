using System.Security.Cryptography;

namespace RunAsLauncher.Verification;

/// <summary>Cálculo do SHA-256 do alvo, a partir de um stream já aberto (ver <see cref="LockedTarget"/>).</summary>
public static class FileHasher
{
    /// <summary>
    /// Devolve o SHA-256 do conteúdo de <paramref name="stream"/> em hexadecimal maiúsculo.
    /// Não reabre o arquivo: lê o stream passado (o do <see cref="LockedTarget"/>, mantido bloqueado).
    /// </summary>
    public static string Sha256(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash); // já vem maiúsculo
    }
}
