namespace RunAsLauncher.Identity;

/// <summary>
/// Tipo da conta do usuário, que decide o pacote de autenticação do logon S4U.
/// Contas não suportadas (ex.: Entra ID) são recusadas pelo serviço.
/// </summary>
public enum AccountKind
{
    Local,
    Domain,
    Unsupported,
}
