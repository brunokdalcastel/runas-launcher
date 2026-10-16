using RunAsLauncher.Localization;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Admin;

/// <summary>
/// Todos os textos visíveis do console de administração, em português e inglês lado a lado.
/// O idioma vem de <see cref="Text"/> (Core), compartilhado com o launcher.
/// </summary>
internal static class Strings
{
    private static string T(string pt, string en) => Text.T(pt, en);

    public static string AppName => T("RunAs Launcher — Administração", "RunAs Launcher — Administration");

    // Abas
    public static string TabItems => T("Itens", "Items");
    public static string TabAudit => T("Auditoria", "Audit");

    // Botões da lista
    public static string Add => T("Adicionar…", "Add…");
    public static string Edit => T("Editar…", "Edit…");
    public static string Duplicate => T("Duplicar", "Duplicate");
    public static string Remove => T("Remover", "Remove");
    public static string MoveUp => T("Subir", "Move up");
    public static string MoveDown => T("Descer", "Move down");
    public static string Import => T("Importar…", "Import…");
    public static string Export => T("Exportar…", "Export…");
    public static string RehashAll => T("Atualizar hash de todos", "Update all hashes");
    public static string Save => T("Salvar", "Save");

    // Colunas
    public static string ColumnTitle => T("Título", "Title");
    public static string ColumnPath => T("Caminho", "Path");
    public static string ColumnMode => T("Modo", "Mode");
    public static string ColumnVerification => T("Verificação", "Verification");
    public static string ColumnCurrent => T("Arquivo atual", "Current file");

    public static string VerifyHash => T("Hash", "Hash");
    public static string VerifyPublisher => T("Assinatura", "Signature");
    public static string VerifyBoth => T("Hash + assinatura", "Hash + signature");
    public static string VerifyNone => T("Nenhuma", "None");

    public static string StatusChecking => T("verificando…", "checking…");
    public static string StatusOk => T("✔ confere", "✔ matches");
    public static string StatusMissing => T("✖ arquivo ausente", "✖ file missing");
    public static string StatusHashMismatch => T("✖ hash diferente", "✖ hash differs");
    public static string StatusSizeMismatch => T("✖ tamanho diferente", "✖ size differs");
    public static string StatusSignatureMismatch => T("✖ assinatura não confere", "✖ signature does not match");
    public static string StatusUnreadable => T("✖ ilegível: {0}", "✖ unreadable: {0}");

    // Barra de status / mensagens
    public static string ItemCount => T("{0} item(ns)", "{0} item(s)");
    public static string Unsaved => T(" — alterações não salvas", " — unsaved changes");
    public static string Saved => T("Política salva em {0}. O serviço recarrega em instantes.", "Policy saved to {0}. The service reloads it shortly.");
    public static string Exported => T("Política exportada para {0}.", "Policy exported to {0}.");
    public static string Imported => T("{0} item(ns) importado(s) de {1}. Clique em Salvar para aplicar.", "{0} item(s) imported from {1}. Click Save to apply.");
    public static string Rehashed => T("{0} hash(es) atualizado(s). Clique em Salvar para aplicar.", "{0} hash(es) updated. Click Save to apply.");
    public static string RehashConfirm => T(
        "Recalcular o SHA-256 de todos os itens que usam hash a partir dos arquivos atuais?\n\nFaça isso só depois de confirmar que os arquivos novos são legítimos (ex.: após uma atualização do programa).",
        "Recalculate the SHA-256 of every item that uses a hash from the current files?\n\nOnly do this after confirming the new files are legitimate (e.g. after a program update).");
    public static string RehashFailures => T("Não foi possível ler:\n{0}", "Could not read:\n{0}");
    public static string RemoveConfirm => T("Remover \"{0}\"?", "Remove \"{0}\"?");
    public static string DiscardConfirm => T("Há alterações não salvas. Descartar?", "There are unsaved changes. Discard them?");
    public static string LoadRejected => T(
        "A política atual foi rejeitada e não pôde ser carregada:\n\n{0}\n\nO console abrirá vazio; salvar substituirá o arquivo.",
        "The current policy was rejected and could not be loaded:\n\n{0}\n\nThe console will start empty; saving will replace the file.");
    public static string ImportRejected => T("Arquivo rejeitado:\n\n{0}", "File rejected:\n\n{0}");
    public static string SaveInvalid => T("A política tem erros e não foi salva:\n\n{0}", "The policy has errors and was not saved:\n\n{0}");
    public static string SaveWarnings => T("Avisos:\n\n{0}\n\nSalvar mesmo assim?", "Warnings:\n\n{0}\n\nSave anyway?");
    public static string SaveFailed => T("Não foi possível gravar: {0}", "Could not write: {0}");
    public static string DropNotExe => T("Ignorado (não é um .exe): {0}", "Ignored (not an .exe): {0}");
    public static string DropLnkFailed => T("Não foi possível ler o atalho {0}: {1}", "Could not read shortcut {0}: {1}");
    public static string JsonFilter => T("Política JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*", "JSON policy (*.json)|*.json|All files (*.*)|*.*");
    public static string General => T("(geral)", "(general)");

    // Editor de item
    public static string EditorTitle => T("Item da política", "Policy item");
    public static string FieldId => T("Id", "Id");
    public static string FieldTitle => T("Título", "Title");
    public static string FieldTarget => T("Programa (.exe)", "Program (.exe)");
    public static string FieldArguments => T("Argumentos fixos", "Fixed arguments");
    public static string FieldWorkingDirectory => T("Pasta de trabalho", "Working directory");
    public static string FieldMode => T("Modo", "Mode");
    public static string FieldWindow => T("Janela", "Window");
    public static string FieldChildren => T("Processos filhos", "Child processes");
    public static string FieldSha256 => T("SHA-256", "SHA-256");
    public static string FieldSize => T("Tamanho esperado (bytes)", "Expected size (bytes)");
    public static string FieldSubject => T("Assinante contém", "Signer contains");
    public static string FieldThumbprint => T("Thumbprint", "Thumbprint");
    public static string FieldProtected => T("Exigir pasta protegida (recomendado)", "Require protected folder (recommended)");
    public static string FieldIcon => T("Ícone (opcional)", "Icon (optional)");
    public static string FieldAllowed => T("Usuários e grupos permitidos", "Allowed users and groups");
    public static string WorkingDirectoryHint => T("vazio = pasta do programa", "empty = program folder");
    public static string SizeHint => T("vazio = não conferir", "empty = do not check");
    public static string AccountHint => T("DOMÍNIO\\usuário, grupo ou SID", "DOMAIN\\user, group or SID");
    public static string Browse => T("Procurar…", "Browse…");
    public static string ComputeHash => T("Calcular hash", "Compute hash");
    public static string ReadSignature => T("Ler assinatura", "Read signature");
    public static string AddAccount => T("Adicionar", "Add");
    public static string RemoveAccount => T("Remover", "Remove");
    public static string Ok => T("OK", "OK");
    public static string Cancel => T("Cancelar", "Cancel");
    public static string ExeFilter => T("Programas (*.exe)|*.exe", "Programs (*.exe)|*.exe");
    public static string IconFilter => T("Ícones e programas (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll", "Icons and programs (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll");

    public static string HashFailed => T("Não foi possível calcular o hash: {0}", "Could not compute the hash: {0}");
    public static string NotSigned => T("O arquivo não tem assinatura Authenticode legível.", "The file has no readable Authenticode signature.");
    public static string SignatureUntrusted => T(
        "A assinatura foi lida, mas o Windows não a considera confiável. O item será negado até que a assinatura seja válida.",
        "The signature was read, but Windows does not trust it. The item will be denied until the signature is valid.");
    public static string AccountNotFound => T("Conta ou grupo não encontrado: {0}", "Account or group not found: {0}");
    public static string ItemInvalid => T("Corrija antes de continuar:\n\n{0}", "Fix before continuing:\n\n{0}");
    public static string DuplicateId => T("Já existe outro item com o id \"{0}\".", "Another item already uses the id \"{0}\".");
    public static string SizeInvalid => T("Tamanho esperado deve ser um número inteiro não negativo.", "Expected size must be a non-negative integer.");

    public static string WarnHighRiskTarget => T(
        "⚠ Alto risco: {0} elevado equivale a dar admin completo ao usuário. Aprove só se você daria admin a ele.",
        "⚠ High risk: {0} running elevated is the same as giving the user full admin. Approve only if you would give them admin.");
    public static string WarnHighRiskChildren => T(
        "⚠ Alto risco: modo Administrador com processos filhos permitidos — o programa pode abrir qualquer outro programa como admin.",
        "⚠ High risk: Administrator mode with child processes allowed — the program can open any other program as admin.");
    public static string WarnUnprotected => T(
        "⚠ Pasta não protegida: usuários comuns podem alterar \"{0}\". Com \"Exigir pasta protegida\" o item será negado; sem ela, o binário pode ser trocado.",
        "⚠ Unprotected folder: regular users can modify \"{0}\". With \"Require protected folder\" the item will be denied; without it, the binary can be replaced.");
    public static string WarnNoAccounts => T("Nenhum usuário ou grupo: ninguém poderá usar este item.", "No users or groups: nobody will be able to use this item.");

    public static string ModeText(RunMode mode) => mode switch
    {
        RunMode.Elevated => T("Administrador", "Administrator"),
        RunMode.Standard => T("Padrão", "Standard"),
        RunMode.Restricted => T("Restrito", "Restricted"),
        _ => mode.ToString(),
    };

    public static string WindowText(WindowMode mode) => mode switch
    {
        WindowMode.Normal => T("Normal", "Normal"),
        WindowMode.Hidden => T("Oculta", "Hidden"),
        WindowMode.Minimized => T("Minimizada", "Minimized"),
        WindowMode.Maximized => T("Maximizada", "Maximized"),
        _ => mode.ToString(),
    };

    public static string ChildrenText(ChildProcessPolicy policy) => policy switch
    {
        ChildProcessPolicy.Allow => T("Permitir", "Allow"),
        ChildProcessPolicy.Deny => T("Negar", "Deny"),
        ChildProcessPolicy.KillTreeOnExit => T("Encerrar todos ao fechar", "End all on exit"),
        _ => policy.ToString(),
    };

    // Auditoria
    public static string AuditRefresh => T("Atualizar", "Refresh");
    public static string AuditExport => T("Exportar CSV…", "Export CSV…");
    public static string AuditDate => T("Data", "Date");
    public static string AuditEvent => T("Evento", "Event");
    public static string AuditUser => T("Usuário", "User");
    public static string AuditItem => T("Item", "Item");
    public static string AuditResult => T("Resultado", "Result");
    public static string AuditDetail => T("Detalhe", "Detail");
    public static string AuditCount => T("{0} evento(s) (até {1} mais recentes)", "{0} event(s) (up to the {1} most recent)");
    public static string AuditNoSource => T(
        "Nenhum evento do RunAs Launcher no log Application (o serviço já foi instalado?).",
        "No RunAs Launcher events in the Application log (has the service been installed?).");
    public static string AuditReadFailed => T("Não foi possível ler o Event Log: {0}", "Could not read the Event Log: {0}");
    public static string AuditExported => T("Auditoria exportada para {0}.", "Audit exported to {0}.");
    public static string CsvFilter => T("CSV (*.csv)|*.csv", "CSV (*.csv)|*.csv");

    public static string EventText(int eventId) => eventId switch
    {
        1000 => T("Lançado", "Launched"),
        1001 => T("Negado", "Denied"),
        1002 => T("Erro de lançamento", "Launch error"),
        1100 => T("Política carregada", "Policy loaded"),
        1101 => T("Política rejeitada", "Policy rejected"),
        1200 => T("Serviço iniciado", "Service started"),
        1201 => T("Serviço parado", "Service stopped"),
        _ => eventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
