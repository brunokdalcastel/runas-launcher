using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using RunAsLauncher.Policies;
using RunAsLauncher.Verification;

namespace RunAsLauncher.Admin;

/// <summary>Destino lido de um atalho <c>.lnk</c>.</summary>
internal sealed record ShortcutTarget(string TargetPath, string? Arguments, string? WorkingDirectory);

/// <summary>
/// Operações de arquivo e conta compartilhadas pela lista e pelo editor: hash, situação do arquivo atual,
/// leitura de <c>.lnk</c>, item pré-preenchido para drag &amp; drop e tradução nome ↔ SID.
/// </summary>
internal static class TargetFiles
{
    /// <summary>SHA-256 (hex maiúsculo) e tamanho do arquivo, lidos de um único handle aberto.</summary>
    public static (string Sha256, long Size) Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (FileHasher.Sha256(stream), stream.Length);
    }

    /// <summary>Confere o arquivo atual contra o item como o serviço faria (tamanho, hash, assinatura).</summary>
    public static (bool Ok, string Text) Status(PolicyItem item)
    {
        if (!File.Exists(item.TargetPath))
        {
            return (false, Strings.StatusMissing);
        }

        try
        {
            var (sha256, size) = Hash(item.TargetPath);
            if (item.ExpectedSize is long expected && expected != size)
            {
                return (false, Strings.StatusSizeMismatch);
            }

            if (item.Sha256 is not null && !string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (false, Strings.StatusHashMismatch);
            }

            if (item.Publisher is not null && !AuthenticodeVerifier.Matches(item.Publisher, AuthenticodeVerifier.Verify(item.TargetPath)))
            {
                return (false, Strings.StatusSignatureMismatch);
            }

            return (true, Strings.StatusOk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, string.Format(Strings.StatusUnreadable, ex.Message));
        }
    }

    /// <summary>Lê destino, argumentos e pasta de trabalho de um <c>.lnk</c> via COM <c>WScript.Shell</c>.</summary>
    public static ShortcutTarget ResolveShortcut(string lnkPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new InvalidOperationException("WScript.Shell indisponível.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            try
            {
                string target = (string)shortcut.TargetPath;
                string args = (string)shortcut.Arguments;
                string workDir = (string)shortcut.WorkingDirectory;
                return new ShortcutTarget(
                    target,
                    string.IsNullOrWhiteSpace(args) ? null : args,
                    string.IsNullOrWhiteSpace(workDir) ? null : workDir);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Item novo pré-preenchido para um <c>.exe</c> (drag &amp; drop): título da descrição do arquivo, id único
    /// derivado do título, hash e tamanho calculados. Modo Administrador, filhos negados, pasta protegida exigida,
    /// ninguém autorizado até o administrador escolher.
    /// </summary>
    public static PolicyItem NewItemFor(string exePath, string? arguments, string? workingDirectory, IEnumerable<string> usedIds)
    {
        string title = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                title = description.Trim();
            }
        }
        catch (FileNotFoundException)
        {
            // Sem informação de versão: fica o nome do arquivo.
        }

        string? sha256 = null;
        try
        {
            sha256 = Hash(exePath).Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O editor mostra o campo vazio; o administrador pode tentar "Calcular hash" de novo.
        }

        return new PolicyItem(
            UniqueId(Slugify(title), usedIds), title, exePath, arguments,
            workingDirectory is not null && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : null,
            RunMode.Elevated, WindowMode.Normal, ChildProcessPolicy.Deny,
            sha256, null, null, true, [], null);
    }

    /// <summary>Slug <c>[a-z0-9-]{1,40}</c> a partir de um texto livre (acentos removidos).</summary>
    public static string Slugify(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        return slug.Length == 0 ? "item" : slug;
    }

    /// <summary><paramref name="baseId"/> ou, se já usado, <c>baseId-2</c>, <c>baseId-3</c>… (sempre ≤ 40 chars).</summary>
    public static string UniqueId(string baseId, IEnumerable<string> usedIds)
    {
        var used = new HashSet<string>(usedIds, StringComparer.Ordinal);
        if (!used.Contains(baseId))
        {
            return baseId;
        }

        for (int n = 2; ; n++)
        {
            string suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string candidate = (baseId.Length + suffix.Length > 40 ? baseId[..(40 - suffix.Length)] : baseId) + suffix;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>SID de um nome (<c>DOMÍNIO\usuário</c>, grupo) ou de um SID digitado. Lança se não existir.</summary>
    public static string ResolveSid(string nameOrSid)
    {
        string text = nameOrSid.Trim();
        if (text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(text).Value;
        }

        return ((SecurityIdentifier)new NTAccount(text).Translate(typeof(SecurityIdentifier))).Value;
    }

    /// <summary>Nome legível de um SID; o próprio SID se não puder ser traduzido (conta apagada, DC fora).</summary>
    public static string DisplayName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException, ArgumentException (SID malformado) ou falha de confiança com o domínio.
            return sid;
        }
    }
}
