using RunAsLauncher.Policies;

namespace RunAsLauncher.Ipc;

// DTOs não polimórficos: o tipo da mensagem é sempre um campo explícito (`type`),
// nunca um discriminador de herança — o servidor não instancia tipos escolhidos pelo cliente.

/// <summary>Versão do protocolo, compartilhada por serviço e launcher.</summary>
public static class Protocol
{
    public const int Version = 1;
}

/// <summary>Operação pedida pelo launcher ao serviço.</summary>
public enum RequestType
{
    List,
    Run,
    Ping,
}

/// <summary>Requisição do launcher. <paramref name="ItemId"/> só é usado em <see cref="RequestType.Run"/>.</summary>
public sealed record Request(int Version, RequestType Type, string? ItemId);

/// <summary>Visão de um item da política exposta ao usuário (sem SIDs, hashes ou regras de editor).</summary>
public sealed record ItemView(string Id, string Title, string TargetPath, RunMode Mode, string? IconPath);

/// <summary>Resultado de uma requisição.</summary>
public enum ResultCode
{
    Ok,
    Denied,
    NotFound,
    VerificationFailed,
    UnprotectedLocation,
    UnsupportedAccount,
    PolicyRejected,
    RateLimited,
    LaunchFailed,
    BadRequest,
}

/// <summary>Resposta do serviço. <paramref name="Items"/> em List; <paramref name="ProcessId"/> em Run bem-sucedido.</summary>
public sealed record Response(int Version, ResultCode Code, string? Message,
                              IReadOnlyList<ItemView>? Items, int? ProcessId);
