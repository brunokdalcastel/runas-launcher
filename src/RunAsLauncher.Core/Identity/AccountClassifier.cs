using System.Security.Principal;

namespace RunAsLauncher.Identity;

/// <summary>
/// Classifica a conta do usuário pelo SID, decidindo o pacote de autenticação do logon S4U.
/// Contas Entra ID (Azure AD, <c>S-1-12-1-*</c>) e qualquer coisa que não seja NT local/domínio
/// são <see cref="AccountKind.Unsupported"/>.
/// </summary>
public static class AccountClassifier
{
    private const string EntraPrefix = "S-1-12-1-";
    private const string NtDomainPrefix = "S-1-5-21-";

    /// <summary>
    /// <paramref name="machineSid"/> é o SID de domínio da máquina (<c>AccountDomainSid</c> de uma conta local).
    /// </summary>
    public static AccountKind Classify(SecurityIdentifier userSid, SecurityIdentifier machineSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        ArgumentNullException.ThrowIfNull(machineSid);

        string value = userSid.Value;

        if (value.StartsWith(EntraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AccountKind.Unsupported; // Entra ID
        }

        if (!value.StartsWith(NtDomainPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AccountKind.Unsupported; // contas de serviço, well-known, etc.
        }

        var userDomain = userSid.AccountDomainSid;
        if (userDomain is null)
        {
            return AccountKind.Unsupported;
        }

        return userDomain.Equals(machineSid) ? AccountKind.Local : AccountKind.Domain;
    }
}
