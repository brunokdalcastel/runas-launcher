using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using RunAsLauncher.Elevation;
using RunAsLauncher.Identity;
using RunAsLauncher.Ipc;
using RunAsLauncher.Native;
using RunAsLauncher.Policies;
using RunAsLauncher.Verification;

namespace RunAsLauncher;

/// <summary>Resultado de um pedido <see cref="RequestType.Run"/>.</summary>
internal sealed record LaunchResult(ResultCode Code, string? Message, int? ProcessId = null);

/// <summary>
/// Decide e executa lançamentos. Cada etapa falha cedo com um <see cref="ResultCode"/> e todo pedido
/// é auditado (1000 lançado, 1001 negado, 1002 erro de lançamento).
/// </summary>
internal sealed class LaunchService : IDisposable
{
    private const string SeAssignPrimaryTokenPrivilege = "SeAssignPrimaryTokenPrivilege";
    private const string SeIncreaseQuotaPrivilege = "SeIncreaseQuotaPrivilege";

    private readonly PolicyProvider _policy;
    private readonly AuditLog _audit;
    private readonly ILogger<LaunchService> _logger;
    private readonly SessionLauncher _launcher = new();
    private readonly TokenFactory _tokens = new();
    private readonly Lazy<SecurityIdentifier> _machineSid = new(LookupMachineSid);

    public LaunchService(PolicyProvider policy, AuditLog audit, ILogger<LaunchService> logger)
    {
        _policy = policy;
        _audit = audit;
        _logger = logger;

        // CreateProcessAsUser com token de outro usuário. LocalSystem tem os dois; fora dele (debug) só avisa.
        foreach (var privilege in new[] { SeAssignPrimaryTokenPrivilege, SeIncreaseQuotaPrivilege })
        {
            try
            {
                Privileges.Enable(privilege);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Não foi possível habilitar {Privilege}; lançamentos devem falhar.", privilege);
            }
        }
    }

    /// <summary>Itens que o chamador pode lançar. <c>null</c> se a política foi rejeitada.</summary>
    public IReadOnlyList<ItemView>? List(CallerInfo caller)
    {
        var current = _policy.Current;
        if (!current.Success)
        {
            return null;
        }

        return current.Policy!.Items
            .Where(i => Authorization.IsAllowed(i, caller.UserSid, caller.GroupSids))
            .Select(i => new ItemView(i.Id, i.Title, i.TargetPath, i.Mode, i.IconPath))
            .ToList();
    }

    public LaunchResult Run(string itemId, CallerInfo caller)
    {
        // 1. Política rejeitada → nega tudo.
        var current = _policy.Current;
        if (!current.Success)
        {
            return Deny(caller, itemId, null, null, ResultCode.PolicyRejected, current.Reason ?? "Política rejeitada.");
        }

        // 2. Item existe e o chamador está autorizado.
        var item = current.Policy!.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return Deny(caller, itemId, null, null, ResultCode.NotFound, "Item inexistente.");
        }

        if (!Authorization.IsAllowed(item, caller.UserSid, caller.GroupSids))
        {
            return Deny(caller, itemId, item, null, ResultCode.Denied, "Usuário fora de AllowedSids.");
        }

        // 3. Tipo de conta (Entra ID e afins não são suportados).
        AccountKind kind = AccountClassifier.Classify(new SecurityIdentifier(caller.UserSid), _machineSid.Value);
        if (kind == AccountKind.Unsupported)
        {
            return Deny(caller, itemId, item, null, ResultCode.UnsupportedAccount, "Conta não suportada (ex.: Entra ID).");
        }

        // 4. Localização protegida.
        if (item.RequireProtectedLocation)
        {
            var (ok, offending) = ProtectedLocationChecker.Check(item.TargetPath, caller.UserSid, caller.GroupSids);
            if (!ok)
            {
                return Deny(caller, itemId, item, null, ResultCode.UnprotectedLocation, $"Gravável por não-admin: {offending}");
            }
        }

        // 5. Alvo bloqueado do início da verificação até depois do CreateProcessAsUser.
        LockedTarget locked;
        try
        {
            locked = LockedTarget.Open(item.TargetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or TargetMismatchException)
        {
            return Deny(caller, itemId, item, null, ResultCode.VerificationFailed, $"Não foi possível abrir o alvo: {ex.Message}");
        }

        using (locked)
        {
            string sha256;
            try
            {
                sha256 = FileHasher.Sha256(locked.Stream);
            }
            catch (IOException ex)
            {
                return Deny(caller, itemId, item, null, ResultCode.VerificationFailed, $"Falha ao ler o alvo: {ex.Message}");
            }

            string? failure = Verify(item, locked, sha256);
            if (failure is not null)
            {
                return Deny(caller, itemId, item, sha256, ResultCode.VerificationFailed, failure);
            }

            // 6–8. Token conforme o modo, criação do processo e auditoria.
            try
            {
                using SafeKernelHandle token = CreateToken(item.Mode, caller, kind);
                int pid = _launcher.Start(token, item);
                _audit.Launch(caller, item, sha256, pid);
                return new LaunchResult(ResultCode.Ok, null, pid);
            }
            catch (IdentityMismatchException ex)
            {
                return Deny(caller, itemId, item, sha256, ResultCode.Denied, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao lançar {ItemId}", itemId);
                _audit.LaunchFailure(caller, item, sha256, ex.Message);
                return new LaunchResult(ResultCode.LaunchFailed, "Falha ao lançar o item.");
            }
        }
    }

    public void Dispose() => _launcher.Dispose();

    /// <summary>Tamanho, SHA-256 e/ou Authenticode. Devolve o motivo da falha, ou null se tudo confere.</summary>
    private static string? Verify(PolicyItem item, LockedTarget locked, string sha256)
    {
        if (item.ExpectedSize is long size && locked.Length != size)
        {
            return $"Tamanho {locked.Length} difere do esperado {size}.";
        }

        if (item.Sha256 is not null && !string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "SHA-256 não confere.";
        }

        if (item.Publisher is not null)
        {
            var trust = AuthenticodeVerifier.Verify(locked.FinalPath);
            if (!AuthenticodeVerifier.Matches(item.Publisher, trust))
            {
                return trust.Valid
                    ? $"Assinatura de '{trust.Subject}' não casa com a regra do editor."
                    : "Assinatura Authenticode inválida ou ausente.";
            }
        }

        return null;
    }

    private SafeKernelHandle CreateToken(RunMode mode, CallerInfo caller, AccountKind kind)
    {
        if (mode == RunMode.Elevated)
        {
            var (domain, user) = SplitName(caller.UserName);
            // MSV1_0 aceita "." como domínio da máquina local (validado no spike da Fase 1).
            string realm = kind == AccountKind.Local ? "." : domain;
            return _tokens.CreateElevatedUserToken(user, realm, kind, caller.SessionId, out _);
        }

        Privileges.Enable(Privileges.SeTcbPrivilege);
        if (!Wtsapi32.WTSQueryUserToken(caller.SessionId, out SafeKernelHandle sessionToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSQueryUserToken({caller.SessionId}) falhou");
        }

        try
        {
            // O token da sessão é do usuário interativo, que pode não ser quem chamou (ex.: runas /user).
            using (var sessionIdentity = new WindowsIdentity(sessionToken.DangerousGetHandle()))
            {
                if (!string.Equals(sessionIdentity.User?.Value, caller.UserSid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IdentityMismatchException(
                        $"O usuário interativo da sessão {caller.SessionId} não é o chamador.");
                }
            }

            if (mode == RunMode.Standard)
            {
                return sessionToken;
            }

            if (!Advapi32.CreateRestrictedToken(
                    sessionToken, Advapi32.DISABLE_MAX_PRIVILEGE | Advapi32.LUA_TOKEN,
                    0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out SafeKernelHandle restricted))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken falhou");
            }

            sessionToken.Dispose();
            return restricted;
        }
        catch
        {
            sessionToken.Dispose();
            throw;
        }
    }

    private LaunchResult Deny(CallerInfo caller, string itemId, PolicyItem? item, string? sha256, ResultCode code, string detail)
    {
        _audit.Deny(caller, itemId, item, sha256, code, detail);
        return new LaunchResult(code, detail);
    }

    private static (string Domain, string User) SplitName(string qualified)
    {
        int slash = qualified.IndexOf('\\');
        return slash < 0 ? (".", qualified) : (qualified[..slash], qualified[(slash + 1)..]);
    }

    /// <summary>SID do domínio de contas local (para distinguir conta local de conta de domínio).</summary>
    private static SecurityIdentifier LookupMachineSid()
    {
        string name = Environment.MachineName;
        uint cbSid = 0, cchDomain = 0;
        ServiceNative.LookupAccountNameW(null, name, null, ref cbSid, null, ref cchDomain, out _);
        if (cbSid == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupAccountName({name}) falhou");
        }

        var sid = new byte[cbSid];
        var domain = new char[cchDomain];
        if (!ServiceNative.LookupAccountNameW(null, name, sid, ref cbSid, domain, ref cchDomain, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupAccountName({name}) falhou");
        }

        return new SecurityIdentifier(sid, 0);
    }

    /// <summary>O token da sessão pertence a outro usuário; tratado como negação, não como erro.</summary>
    private sealed class IdentityMismatchException(string message) : Exception(message);
}
