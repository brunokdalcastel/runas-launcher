namespace RunAsLauncher.Policies;

/// <summary>
/// Alvos de alto risco: programas que, rodando elevados, equivalem a dar admin completo ao usuário
/// (shells, editores de registro/MMC, gerenciadores). O console avisa o administrador; não é bloqueio.
/// </summary>
public static class HighRiskTargets
{
    /// <summary>Nomes de arquivo (comparação case-insensitive, sem pasta).</summary>
    public static readonly IReadOnlySet<string> FileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "regedit.exe",
        "mmc.exe",
        "explorer.exe",
        "taskmgr.exe",
    };

    /// <summary>true se o nome do arquivo de <paramref name="targetPath"/> está na lista.</summary>
    public static bool IsHighRiskFile(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        return FileNames.Contains(Path.GetFileName(targetPath.Trim()));
    }

    /// <summary>
    /// Alto risco = alvo na lista, ou modo <see cref="RunMode.Elevated"/> com filhos permitidos
    /// (o processo elevado pode abrir qualquer outro programa elevado).
    /// </summary>
    public static bool IsHighRisk(PolicyItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsHighRiskFile(item.TargetPath) ||
               (item.Mode == RunMode.Elevated && item.Children == ChildProcessPolicy.Allow);
    }
}
