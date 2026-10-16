namespace RunAsLauncher.Policies;

/// <summary>Como o alvo é lançado em termos de privilégio do token.</summary>
public enum RunMode
{
    Elevated,
    Standard,
    Restricted,
}

/// <summary>Estado inicial da janela do processo lançado.</summary>
public enum WindowMode
{
    Normal,
    Hidden,
    Minimized,
    Maximized,
}

/// <summary>Política para processos filhos do alvo.</summary>
public enum ChildProcessPolicy
{
    Allow,
    Deny,
    KillTreeOnExit,
}

/// <summary>
/// Um item da política: um programa que o administrador aprovou para ser lançado
/// pelo serviço. Imutável; validado por <see cref="PolicyValidator"/> antes de usar.
/// </summary>
public sealed record PolicyItem(
    string Id,                          // slug [a-z0-9-]{1,40}, único na política
    string Title,
    string TargetPath,                  // caminho absoluto local, sem variáveis de ambiente do usuário
    string? Arguments,                  // fixos; o cliente nunca envia argumentos
    string? WorkingDirectory,           // null = pasta do alvo
    RunMode Mode,
    WindowMode Window,
    ChildProcessPolicy Children,
    string? Sha256,                     // hex maiúsculo (64 chars); null só se Publisher != null
    PublisherRule? Publisher,
    long? ExpectedSize,
    bool RequireProtectedLocation,      // F4, default true
    IReadOnlyList<string> AllowedSids,  // usuários/grupos; vazio = ninguém
    string? IconPath);

/// <summary>Regra de assinatura Authenticode aceita para um item.</summary>
public sealed record PublisherRule(string SubjectContains, string? Thumbprint);

/// <summary>Política completa carregada/gravada como JSON.</summary>
public sealed record Policy(int SchemaVersion, IReadOnlyList<PolicyItem> Items);
