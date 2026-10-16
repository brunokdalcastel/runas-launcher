using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunAsLauncher.Policies;
using RunAsLauncher.Verification;

namespace RunAsLauncher;

/// <summary>
/// Mantém a política vigente. Carrega no start e a cada mudança em <c>%ProgramData%\RunAsLauncher</c>
/// (debounce de 500 ms). Antes de cada carga confere a ACL da pasta e do <c>policy.json</c>: se um SID
/// não-administrador puder gravar, a política é <see cref="PolicyLoadResult.Rejected"/> (nega tudo).
/// </summary>
internal sealed class PolicyProvider : IHostedService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    // Rechecagem periódica: cobre mudanças de ACL só na pasta, que o FileSystemWatcher não notifica.
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> RiskySids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-1-0",      // Everyone
        "S-1-5-11",     // Authenticated Users
        "S-1-5-32-545", // BUILTIN\Users
        "S-1-5-4",      // Interactive
    };

    private readonly PolicyStore _store;
    private readonly AuditLog _audit;
    private readonly ILogger<PolicyProvider> _logger;
    private readonly object _reloadLock = new();

    private PolicyLoadResult _current = PolicyLoadResult.Rejected("Política ainda não carregada.");
    private string? _lastFingerprint;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _recheckTimer;

    public PolicyProvider(AuditLog audit, ILogger<PolicyProvider> logger)
        : this(PolicyStore.CreateDefault(), audit, logger)
    {
    }

    internal PolicyProvider(PolicyStore store, AuditLog audit, ILogger<PolicyProvider> logger)
    {
        _store = store;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Política vigente (Ok ou Rejected). Leitura sem lock.</summary>
    public PolicyLoadResult Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureDirectory();
        Reload();

        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _recheckTimer = new Timer(_ => Reload(), null, Recheck, Recheck);

        if (!Directory.Exists(_store.Directory))
        {
            // Sem pasta não há o que observar; a rechecagem periódica cobre a criação posterior.
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(_store.Directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                           NotifyFilters.CreationTime | NotifyFilters.Security | NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher da política falhou; recarregando.");
            ScheduleReload();
        };
        _watcher.EnableRaisingEvents = true;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _recheckTimer?.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void ScheduleReload() => _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    /// <summary>Verifica a ACL e (re)carrega. Só audita quando o resultado muda.</summary>
    internal void Reload()
    {
        lock (_reloadLock)
        {
            PolicyLoadResult result;
            try
            {
                result = CheckAcl() ?? _store.Load();
            }
            catch (Exception ex)
            {
                result = PolicyLoadResult.Rejected($"Erro inesperado ao carregar: {ex.Message}");
            }

            Volatile.Write(ref _current, result);

            string fingerprint = result.Success ? "ok:" + ContentHash() : "rejected:" + result.Reason;
            if (fingerprint == _lastFingerprint)
            {
                return;
            }

            _lastFingerprint = fingerprint;
            if (result.Success)
            {
                _audit.PolicyLoad(_store.FilePath, result.Policy!.Items.Count);
            }
            else
            {
                _audit.PolicyReject(_store.FilePath, result.Reason ?? "?");
            }
        }
    }

    private string ContentHash()
    {
        try
        {
            return File.Exists(_store.FilePath)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_store.FilePath)))
                : "empty";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    /// <summary>Rejected se a pasta ou o arquivo puderem ser gravados por não-admins; null se ok.</summary>
    private PolicyLoadResult? CheckAcl()
    {
        if (!Directory.Exists(_store.Directory))
        {
            return PolicyLoadResult.Rejected($"Pasta da política ausente: {_store.Directory}");
        }

        // Na pasta, criar arquivo também é perigoso (permitiria criar um policy.json): máscara completa.
        if (ProtectedLocationChecker.IsRiskyWritable(_store.Directory, RiskySids, WriteAccessEvaluator.DefaultWriteMask))
        {
            return PolicyLoadResult.Rejected($"A pasta '{_store.Directory}' é gravável por não-administradores.");
        }

        if (File.Exists(_store.FilePath) &&
            ProtectedLocationChecker.IsRiskyWritable(_store.FilePath, RiskySids, WriteAccessEvaluator.FileWriteMask))
        {
            return PolicyLoadResult.Rejected($"O arquivo '{_store.FilePath}' é gravável por não-administradores.");
        }

        return null;
    }

    /// <summary>
    /// Cria a pasta da política com DACL protegida se ela não existir (o watcher precisa dela).
    /// Se já existir, não mexe: a ACL é só verificada (uma pasta adulterada continua rejeitada).
    /// </summary>
    private void EnsureDirectory()
    {
        if (Directory.Exists(_store.Directory))
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        try
        {
            new DirectoryInfo(_store.Directory).Create(security);
            _logger.LogInformation("Pasta da política criada com ACL protegida: {Dir}", _store.Directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível criar a pasta da política {Dir}", _store.Directory);
        }
    }
}
