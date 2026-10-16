using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RunAsLauncher.Ipc;
using RunAsLauncher.Policies;

namespace RunAsLauncher;

/// <summary>Quem pediu o lançamento, como identificado pelo pipe.</summary>
internal sealed record CallerInfo(string UserName, string UserSid, IReadOnlyList<string> GroupSids, uint SessionId);

/// <summary>
/// Auditoria no log <c>Application</c> (fonte <c>RunAsLauncher</c>) com os Event IDs fixos das convenções
/// do Spec. Falha ao gravar no Event Log nunca derruba o serviço: cai no <see cref="ILogger"/>.
/// </summary>
internal sealed class AuditLog
{
    public const string SourceName = "RunAsLauncher";
    public const string LogName = "Application";

    public const int Launched = 1000;
    public const int Denied = 1001;
    public const int LaunchError = 1002;
    public const int PolicyLoaded = 1100;
    public const int PolicyRejected = 1101;
    public const int ServiceStarted = 1200;
    public const int ServiceStopped = 1201;

    private readonly ILogger<AuditLog> _logger;

    public AuditLog(ILogger<AuditLog> logger)
    {
        _logger = logger;
    }

    /// <summary>Registra a fonte de eventos (manutenção; exige admin). Usado por <c>--register-eventsource</c>.</summary>
    public static void RegisterSource()
    {
        if (!EventLog.SourceExists(SourceName))
        {
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));
        }
    }

    /// <summary>Remove a fonte de eventos (manutenção; exige admin). Usado por <c>--unregister-eventsource</c>.</summary>
    public static void UnregisterSource()
    {
        if (EventLog.SourceExists(SourceName))
        {
            EventLog.DeleteEventSource(SourceName);
        }
    }

    public void ServiceStart() => Write(ServiceStarted, EventLogEntryType.Information, "Serviço RunAs Launcher iniciado.");

    public void ServiceStop() => Write(ServiceStopped, EventLogEntryType.Information, "Serviço RunAs Launcher parado.");

    public void PolicyLoad(string path, int itemCount) =>
        Write(PolicyLoaded, EventLogEntryType.Information, $"Política carregada: {path} ({itemCount} itens).");

    public void PolicyReject(string path, string reason) =>
        Write(PolicyRejected, EventLogEntryType.Error, $"Política rejeitada (todos os lançamentos serão negados): {path}\r\nMotivo: {reason}");

    public void Launch(CallerInfo caller, PolicyItem item, string? sha256, int pid) =>
        Write(Launched, EventLogEntryType.Information,
            Describe("Item lançado.", caller, item.Id, item.TargetPath, sha256, item.Mode, ResultCode.Ok, pid, null));

    public void Deny(CallerInfo caller, string? itemId, PolicyItem? item, string? sha256, ResultCode code, string detail) =>
        Write(Denied, EventLogEntryType.Warning,
            Describe("Lançamento negado.", caller, itemId, item?.TargetPath, sha256, item?.Mode, code, null, detail));

    public void LaunchFailure(CallerInfo caller, PolicyItem item, string? sha256, string detail) =>
        Write(LaunchError, EventLogEntryType.Error,
            Describe("Erro ao lançar o item.", caller, item.Id, item.TargetPath, sha256, item.Mode, ResultCode.LaunchFailed, null, detail));

    private static string Describe(string headline, CallerInfo caller, string? itemId, string? path, string? sha256,
                                   RunMode? mode, ResultCode result, int? pid, string? detail)
    {
        var sb = new StringBuilder(headline).AppendLine();
        sb.Append("Usuário: ").Append(caller.UserName).Append(" (").Append(caller.UserSid).AppendLine(")");
        sb.Append("Sessão: ").Append(caller.SessionId).AppendLine();
        sb.Append("Item: ").AppendLine(itemId ?? "-");
        sb.Append("Caminho: ").AppendLine(path ?? "-");
        sb.Append("SHA-256: ").AppendLine(sha256 ?? "-");
        sb.Append("Modo: ").AppendLine(mode?.ToString() ?? "-");
        sb.Append("Resultado: ").Append(result).AppendLine();
        sb.Append("PID: ").Append(pid?.ToString() ?? "-");
        if (!string.IsNullOrEmpty(detail))
        {
            sb.AppendLine().Append("Detalhe: ").Append(detail);
        }

        return sb.ToString();
    }

    private void Write(int eventId, EventLogEntryType type, string message)
    {
        try
        {
            EventLog.WriteEntry(SourceName, message, type, eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gravar evento {EventId} no Event Log: {Message}", eventId, message);
        }
    }
}
