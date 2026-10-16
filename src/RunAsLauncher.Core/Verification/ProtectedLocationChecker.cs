using System.Security.AccessControl;
using System.Security.Principal;

namespace RunAsLauncher.Verification;

/// <summary>
/// Verifica se o alvo está numa "localização protegida": nem o arquivo nem nenhum de seus diretórios
/// ancestrais (até a raiz do volume) podem ser adulterados por um SID não-administrador (F4, mitigação
/// de substituição do binário). Usa <see cref="WriteAccessEvaluator"/> com máscaras distintas para
/// arquivo e diretório (ver <see cref="WriteAccessEvaluator.DirectoryWriteMask"/>).
/// </summary>
public static class ProtectedLocationChecker
{
    // SIDs sempre considerados "não confiáveis" para fins de escrita.
    private static readonly string[] BaseRiskySids =
    {
        "S-1-1-0",      // Everyone
        "S-1-5-11",     // Authenticated Users
        "S-1-5-32-545", // BUILTIN\Users
        "S-1-5-4",      // Interactive
    };

    private const string AdministratorsSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";

    /// <summary>
    /// <paramref name="callerSid"/> e <paramref name="callerGroupSids"/> somam-se aos SIDs arriscados
    /// (exceto Administrators/SYSTEM entre os grupos). Retorna o primeiro caminho ofensivo encontrado.
    /// </summary>
    public static (bool Ok, string? OffendingPath) Check(
        string targetPath, string callerSid, IEnumerable<string> callerGroupSids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var risky = BuildRiskySids(callerSid, callerGroupSids);

        bool first = true;
        foreach (var path in AncestorsOf(targetPath))
        {
            // O primeiro item é o próprio arquivo-alvo; o resto são diretórios.
            int mask = first ? WriteAccessEvaluator.FileWriteMask : WriteAccessEvaluator.DirectoryWriteMask;
            first = false;

            if (IsRiskyWritable(path, risky, mask))
            {
                return (false, path);
            }
        }

        return (true, null);
    }

    private static HashSet<string> BuildRiskySids(string callerSid, IEnumerable<string> callerGroupSids)
    {
        var set = new HashSet<string>(BaseRiskySids, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(callerSid))
        {
            set.Add(callerSid);
        }

        foreach (var sid in callerGroupSids ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrEmpty(sid) ||
                sid.Equals(AdministratorsSid, StringComparison.OrdinalIgnoreCase) ||
                sid.Equals(SystemSid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            set.Add(sid);
        }

        return set;
    }

    /// <summary>Arquivo-alvo seguido de cada diretório ancestral, até a raiz do volume.</summary>
    private static IEnumerable<string> AncestorsOf(string targetPath)
    {
        string full = Path.GetFullPath(targetPath);
        yield return full;

        string? dir = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir))
        {
            yield return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || parent.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            dir = parent;
        }
    }

    internal static bool IsRiskyWritable(string path, ISet<string> risky, int mask)
    {
        FileSystemSecurity security;
        try
        {
            security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Não foi possível ler a DACL: fail-closed (trata como não protegido).
            return true;
        }

        // Dono arriscado = controle total implícito (pode reescrever a DACL).
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
            risky.Contains(owner.Value))
        {
            return true;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var aces = new List<AceInfo>(rules.Count);
        foreach (AuthorizationRule rule in rules)
        {
            if (rule is not FileSystemAccessRule fsRule)
            {
                continue;
            }

            string sid = ((SecurityIdentifier)fsRule.IdentityReference).Value;
            bool allow = fsRule.AccessControlType == AccessControlType.Allow;
            int rights = (int)fsRule.FileSystemRights;
            bool inheritOnly = (fsRule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
            aces.Add(new AceInfo(sid, allow, rights, inheritOnly));
        }

        return WriteAccessEvaluator.IsWritableBy(aces, risky, mask);
    }
}
