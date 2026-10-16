using RunAsLauncher.Ipc;
using RunAsLauncher.Localization;
using RunAsLauncher.Policies;

namespace RunAsLauncher;

/// <summary>
/// Todos os textos visíveis do launcher, em português e inglês lado a lado.
/// O idioma vem de <see cref="Text"/> (Core), compartilhado com o console de administração.
/// </summary>
internal static class Strings
{
    private static string T(string pt, string en) => Text.T(pt, en);

    public const string AppName = "RunAs Launcher";

    public static string ServiceUnavailable => T(
        "Serviço RunAs Launcher não está em execução. Peça ao administrador para verificar a instalação.",
        "The RunAs Launcher service is not running. Ask your administrator to check the installation.");

    public static string ServiceProtocolError => T(
        "O serviço respondeu de forma inesperada. Tente novamente ou peça ajuda ao administrador.",
        "The service returned an unexpected response. Try again or ask your administrator for help.");

    public static string Details => T("Detalhes: {0}", "Details: {0}");

    // Janela principal
    public static string SearchPlaceholder => T("Buscar programa…", "Search program…");
    public static string Refresh => T("Atualizar", "Refresh");
    public static string ViewIcons => T("Ícones", "Icons");
    public static string ViewDetails => T("Detalhes", "Details");
    public static string ColumnTitle => T("Programa", "Program");
    public static string ColumnMode => T("Modo", "Mode");
    public static string ColumnPath => T("Caminho", "Path");
    public static string Loading => T("Carregando…", "Loading…");
    public static string ItemCount => T("{0} programa(s) disponível(is)", "{0} program(s) available");
    public static string NoItems => T("Nenhum programa liberado para você.", "No programs are available to you.");
    public static string Started => T("\"{0}\" iniciado.", "\"{0}\" started.");

    public static string MenuRun => T("Executar", "Run");
    public static string MenuShortcut => T("Criar atalho na área de trabalho", "Create desktop shortcut");
    public static string MenuCopyCommand => T("Copiar comando", "Copy command");
    public static string MenuOpenLocation => T("Abrir local do arquivo", "Open file location");

    public static string ShortcutCreated => T("Atalho criado: {0}", "Shortcut created: {0}");
    public static string ShortcutFailed => T("Não foi possível criar o atalho: {0}", "Could not create the shortcut: {0}");
    public static string CommandCopied => T("Comando copiado.", "Command copied.");
    public static string OpenLocationFailed => T("Não foi possível abrir a pasta: {0}", "Could not open the folder: {0}");

    public static string ModeText(RunMode mode) => mode switch
    {
        RunMode.Elevated => T("Administrador", "Administrator"),
        RunMode.Standard => T("Padrão", "Standard"),
        RunMode.Restricted => T("Restrito", "Restricted"),
        _ => mode.ToString(),
    };

    // Linha de comando
    public static string Usage => T(
        "Uso:\n  RunAsLauncher              abre a lista de programas\n  RunAsLauncher run <id>     executa o programa <id>\n  RunAsLauncher --help       mostra esta ajuda",
        "Usage:\n  RunAsLauncher              opens the program list\n  RunAsLauncher run <id>     runs program <id>\n  RunAsLauncher --help       shows this help");

    /// <summary>Texto em linguagem simples para cada <see cref="ResultCode"/>, no idioma atual.</summary>
    public static string ForResult(ResultCode code)
    {
        var (pt, en) = ResultTexts(code);
        return T(pt, en);
    }

    /// <summary>Texto do resultado seguido, se houver, do detalhe técnico enviado pelo serviço.</summary>
    public static string Describe(ResultCode code, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? ForResult(code)
            : ForResult(code) + "\n\n" + string.Format(Details, detail);

    /// <summary>Par (pt, en) de cada <see cref="ResultCode"/>. Exposto para os testes conferirem a cobertura.</summary>
    internal static (string Pt, string En) ResultTexts(ResultCode code) => code switch
    {
        ResultCode.Ok => (
            "Programa iniciado.",
            "Program started."),
        ResultCode.Denied => (
            "Você não tem permissão para executar este programa. Peça acesso ao administrador.",
            "You are not allowed to run this program. Ask your administrator for access."),
        ResultCode.NotFound => (
            "Este programa não está mais na lista. Clique em Atualizar.",
            "This program is no longer on the list. Click Refresh."),
        ResultCode.VerificationFailed => (
            "O programa foi alterado desde que o administrador o liberou e foi bloqueado por segurança. Avise o administrador.",
            "The program has changed since your administrator approved it and was blocked for safety. Tell your administrator."),
        ResultCode.UnprotectedLocation => (
            "O programa está numa pasta que qualquer usuário pode alterar. Peça ao administrador para movê-lo.",
            "The program is in a folder that any user can change. Ask your administrator to move it."),
        ResultCode.UnsupportedAccount => (
            "Sua conta (Microsoft Entra ID) não é suportada. Use uma conta local ou de domínio.",
            "Your account (Microsoft Entra ID) is not supported. Use a local or domain account."),
        ResultCode.PolicyRejected => (
            "A configuração do RunAs Launcher está insegura ou corrompida, então nada pode ser executado. Avise o administrador.",
            "The RunAs Launcher configuration is unsafe or corrupted, so nothing can run. Tell your administrator."),
        ResultCode.RateLimited => (
            "Muitas tentativas em pouco tempo. Aguarde um minuto e tente de novo.",
            "Too many attempts in a short time. Wait a minute and try again."),
        ResultCode.LaunchFailed => (
            "O Windows não conseguiu iniciar o programa.",
            "Windows could not start the program."),
        ResultCode.BadRequest => (
            "Pedido inválido. Confira o atalho ou o comando usado.",
            "Invalid request. Check the shortcut or command you used."),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
