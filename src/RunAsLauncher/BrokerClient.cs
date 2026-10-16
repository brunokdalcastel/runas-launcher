using System.IO.Pipes;
using System.Security.Principal;
using RunAsLauncher.Ipc;

namespace RunAsLauncher;

/// <summary>Falha de comunicação com o serviço. <see cref="Exception.Message"/> já é o texto para o usuário.</summary>
internal sealed class BrokerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Cliente do pipe do serviço: uma conexão por requisição (o servidor responde uma vez e fecha).
/// Nível de impersonação <see cref="TokenImpersonationLevel.Identification"/>: o serviço só precisa saber
/// quem somos, nunca agir em nosso nome.
/// </summary>
internal sealed class BrokerClient
{
    private const int ConnectTimeoutMs = 3000;

    // Run inclui hash e verificação de assinatura do alvo; arquivos grandes podem demorar.
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);

    public Task<Response> ListAsync(CancellationToken ct = default) =>
        SendAsync(new Request(Protocol.Version, RequestType.List, null), ct);

    public Task<Response> RunAsync(string id, CancellationToken ct = default) =>
        SendAsync(new Request(Protocol.Version, RequestType.Run, id), ct);

    private static async Task<Response> SendAsync(Request request, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeNames.Main, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new BrokerException(Strings.ServiceUnavailable, ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            await Framing.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            return await Framing.ReadAsync<Response>(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new BrokerException(Strings.ServiceProtocolError, ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // EndOfStreamException deriva de IOException: serviço caiu ou fechou sem responder.
            throw new BrokerException(Strings.ServiceProtocolError, ex);
        }
    }
}
