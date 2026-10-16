using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RunAsLauncher.Ipc;
using RunAsLauncher.Native;

namespace RunAsLauncher;

/// <summary>
/// Servidor do named pipe <see cref="PipeNames.Main"/> com até 4 instâncias simultâneas.
/// Anti pipe squatting (R5): a primeira instância usa <see cref="PipeOptions.FirstPipeInstance"/> e sempre há
/// uma instância nossa viva (a próxima é criada antes de a atual ser entregue ao handler), então ninguém
/// consegue criar o pipe antes do serviço nem "roubar" o nome entre conexões.
/// </summary>
internal sealed class PipeServer : BackgroundService
{
    public const int ProtocolVersion = Protocol.Version;

    private const int MaxInstances = 4;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly LaunchService _launch;
    private readonly AuditLog _audit;
    private readonly ILogger<PipeServer> _logger;
    private readonly RateLimiter _rateLimiter = new(limit: 20, window: TimeSpan.FromMinutes(1));

    // Uma instância fica sempre escutando; as demais (até MaxInstances - 1) atendem conexões.
    private readonly SemaphoreSlim _handlerSlots = new(MaxInstances - 1, MaxInstances - 1);

    public PipeServer(LaunchService launch, AuditLog audit, ILogger<PipeServer> logger)
    {
        _launch = launch;
        _audit = audit;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _audit.ServiceStart();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _audit.ServiceStop();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Se outro processo já criou o pipe, isto lança e o serviço não sobe (fail-closed).
        NamedPipeServerStream listening = CreateInstance(first: true);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await listening.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await _handlerSlots.WaitAsync(stoppingToken).ConfigureAwait(false);

                NamedPipeServerStream connected = listening;
                listening = await CreateNextInstanceAsync(stoppingToken).ConfigureAwait(false);

                _ = Task.Run(() => HandleAsync(connected, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await listening.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<NamedPipeServerStream> CreateNextInstanceAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return CreateInstance(first: false);
            }
            catch (IOException ex)
            {
                // A instância conectada continua viva, então o nome segue nosso enquanto tentamos de novo.
                _logger.LogWarning(ex, "Falha ao criar instância do pipe; tentando novamente.");
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateInstance(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            PipeNames.Main, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            options, inBufferSize: 0, outBufferSize: 0, BuildSecurity());
    }

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // ReadWrite não inclui CreateNewInstance: usuários comuns não conseguem criar instâncias do pipe.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Defesa em profundidade além do PIPE_REJECT_REMOTE_CLIENTS: nega acesso pela rede.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(ReadTimeout);

            Request request;
            try
            {
                request = await Framing.ReadAsync<Request>(pipe, timeout.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                await ReplyAsync(pipe, new Response(ProtocolVersion, ResultCode.BadRequest, ex.Message, null, null), stoppingToken).ConfigureAwait(false);
                return;
            }

            // Impersonação só funciona depois de ler dados do pipe.
            CallerInfo? caller = IdentifyCaller(pipe);
            if (caller is null)
            {
                await ReplyAsync(pipe, new Response(ProtocolVersion, ResultCode.Denied, "Cliente não identificado.", null, null), stoppingToken).ConfigureAwait(false);
                return;
            }

            Response response = Dispatch(request, caller);
            await ReplyAsync(pipe, response, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
        {
            _logger.LogDebug(ex, "Conexão do pipe encerrada sem resposta.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atender conexão do pipe.");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _handlerSlots.Release();
        }
    }

    private Response Dispatch(Request request, CallerInfo caller)
    {
        if (request.Version != ProtocolVersion)
        {
            return new Response(ProtocolVersion, ResultCode.BadRequest, $"Versão de protocolo não suportada: {request.Version}.", null, null);
        }

        if (!_rateLimiter.TryAcquire(caller.UserSid))
        {
            if (request.Type == RequestType.Run)
            {
                _audit.Deny(caller, request.ItemId, null, null, ResultCode.RateLimited, "Limite de 20 requisições/min excedido.");
            }

            return new Response(ProtocolVersion, ResultCode.RateLimited, "Muitas requisições; aguarde um minuto.", null, null);
        }

        switch (request.Type)
        {
            case RequestType.Ping:
                return new Response(ProtocolVersion, ResultCode.Ok, null, null, null);

            case RequestType.List:
                var items = _launch.List(caller);
                return items is null
                    ? new Response(ProtocolVersion, ResultCode.PolicyRejected, "Política rejeitada pelo serviço.", Array.Empty<ItemView>(), null)
                    : new Response(ProtocolVersion, ResultCode.Ok, null, items, null);

            case RequestType.Run:
                if (string.IsNullOrEmpty(request.ItemId))
                {
                    return new Response(ProtocolVersion, ResultCode.BadRequest, "ItemId obrigatório em Run.", null, null);
                }

                LaunchResult result;
                try
                {
                    result = _launch.Run(request.ItemId, caller);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro inesperado em Run({ItemId})", request.ItemId);
                    result = new LaunchResult(ResultCode.LaunchFailed, "Erro interno do serviço.");
                }

                return new Response(ProtocolVersion, result.Code, result.Message, null, result.ProcessId);

            default:
                return new Response(ProtocolVersion, ResultCode.BadRequest, "Tipo de requisição desconhecido.", null, null);
        }
    }

    /// <summary>Usuário, grupos habilitados e sessão do cliente. null se anônimo ou não identificável.</summary>
    private CallerInfo? IdentifyCaller(NamedPipeServerStream pipe)
    {
        try
        {
            CallerInfo? caller = null;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.IsAnonymous || identity.User is null)
                {
                    return;
                }

                var groups = identity.Groups?
                    .OfType<SecurityIdentifier>()
                    .Select(g => g.Value)
                    .ToList() ?? new List<string>();
                caller = new CallerInfo(identity.Name, identity.User.Value, groups, 0);
            });

            if (caller is null)
            {
                return null;
            }

            if (!ServiceNative.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint sessionId))
            {
                _logger.LogWarning("GetNamedPipeClientSessionId falhou ({Error}).", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return null;
            }

            return caller with { SessionId = sessionId };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível identificar o cliente do pipe.");
            return null;
        }
    }

    private static Task ReplyAsync(NamedPipeServerStream pipe, Response response, CancellationToken ct) =>
        Framing.WriteAsync(pipe, response, ct);

    public override void Dispose()
    {
        _handlerSlots.Dispose();
        base.Dispose();
    }
}
