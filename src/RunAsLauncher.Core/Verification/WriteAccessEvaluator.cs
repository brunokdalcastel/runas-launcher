namespace RunAsLauncher.Verification;

/// <summary>Uma ACE já extraída de uma DACL, reduzida ao que o avaliador precisa.</summary>
/// <param name="Sid">SID do trustee, em formato string (ex.: S-1-5-11).</param>
/// <param name="Allow">true = Allow; false = Deny.</param>
/// <param name="Rights">Máscara de direitos (bits de acesso do Windows).</param>
/// <param name="InheritOnly">true quando a ACE só se aplica a filhos (ignorada no objeto atual).</param>
public sealed record AceInfo(string Sid, bool Allow, int Rights, bool InheritOnly);

/// <summary>
/// Lógica pura (sem I/O) que decide se algum SID "arriscado" consegue gravar sobre um objeto,
/// a partir de suas ACEs. Usada tanto pelo <see cref="ProtectedLocationChecker"/> (alvo e ancestrais)
/// quanto pela verificação da ACL de <c>%ProgramData%\RunAsLauncher</c> no serviço.
/// </summary>
public static class WriteAccessEvaluator
{
    // Direitos de escrita (bits de acesso a arquivo/pasta do Win32).
    public const int FILE_WRITE_DATA = 0x0002;        // = FILE_ADD_FILE (pastas)
    public const int FILE_APPEND_DATA = 0x0004;       // = FILE_ADD_SUBDIRECTORY (pastas)
    public const int FILE_DELETE_CHILD = 0x0040;
    public const int DELETE = 0x10000;
    public const int WRITE_DAC = 0x40000;
    public const int WRITE_OWNER = 0x80000;
    public const int GENERIC_WRITE = unchecked((int)0x40000000);
    public const int GENERIC_ALL = unchecked((int)0x10000000);

    /// <summary>Máscara padrão com todos os direitos de escrita considerados perigosos.</summary>
    public const int DefaultWriteMask =
        FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_DELETE_CHILD |
        DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;

    /// <summary>
    /// Máscara para o próprio arquivo-alvo: qualquer direito que permita alterar/substituir o conteúdo
    /// ou a segurança do arquivo (inclui APPEND, que em arquivos é escrita real).
    /// </summary>
    public const int FileWriteMask =
        FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;

    /// <summary>
    /// Máscara para diretórios ancestrais: só os direitos que permitem apagar/redirecionar a cadeia já
    /// existente. Os bits de criar-novo (<see cref="FILE_WRITE_DATA"/>=ADD_FILE e
    /// <see cref="FILE_APPEND_DATA"/>=ADD_SUBDIRECTORY) são excluídos — criar um irmão novo em C:\ não permite
    /// adulterar C:\Windows, e C:\ concede ADD_SUBDIRECTORY a "Usuários autenticados" por padrão.
    /// </summary>
    public const int DirectoryWriteMask =
        FILE_DELETE_CHILD | DELETE | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;

    /// <summary>Avalia com a máscara de escrita padrão.</summary>
    public static bool IsWritableBy(IEnumerable<AceInfo> aces, ISet<string> riskySids) =>
        IsWritableBy(aces, riskySids, DefaultWriteMask);

    /// <summary>
    /// true se, considerando apenas os bits de <paramref name="writeMask"/>, algum SID de
    /// <paramref name="riskySids"/> tiver acesso de escrita efetivo (Allow &amp; ~Deny para o mesmo SID).
    /// ACEs InheritOnly são ignoradas; uma Deny para o mesmo SID anula só os bits que cobre.
    /// </summary>
    public static bool IsWritableBy(IEnumerable<AceInfo> aces, ISet<string> riskySids, int writeMask)
    {
        ArgumentNullException.ThrowIfNull(aces);
        ArgumentNullException.ThrowIfNull(riskySids);

        var allowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var denied = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ace in aces)
        {
            if (ace is null || ace.InheritOnly || !riskySids.Contains(ace.Sid))
            {
                continue;
            }

            int writeBits = ace.Rights & writeMask;
            if (writeBits == 0)
            {
                continue;
            }

            var map = ace.Allow ? allowed : denied;
            map[ace.Sid] = (map.TryGetValue(ace.Sid, out var existing) ? existing : 0) | writeBits;
        }

        foreach (var (sid, allowBits) in allowed)
        {
            int denyBits = denied.TryGetValue(sid, out var d) ? d : 0;
            if ((allowBits & ~denyBits) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
