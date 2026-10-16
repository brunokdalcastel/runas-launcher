using System.Text.RegularExpressions;

namespace RunAsLauncher.Policies;

/// <summary>Severidade de uma mensagem de validação.</summary>
public enum ValidationSeverity
{
    Error,
    Warning,
}

/// <summary>Uma falha (ou aviso) de validação, ligada a um item quando aplicável.</summary>
public sealed record ValidationError(ValidationSeverity Severity, string? ItemId, string Code, string Message);

/// <summary>Resultado da validação de uma <see cref="Policy"/>.</summary>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Messages)
{
    public IEnumerable<ValidationError> Errors => Messages.Where(m => m.Severity == ValidationSeverity.Error);

    public IEnumerable<ValidationError> Warnings => Messages.Where(m => m.Severity == ValidationSeverity.Warning);

    /// <summary>Verdadeiro quando não há nenhum erro (avisos não invalidam).</summary>
    public bool IsValid => !Errors.Any();
}

/// <summary>
/// Validação pura (sem I/O) de uma <see cref="Policy"/>. Em caso de qualquer erro a
/// política inteira é rejeitada pelo <see cref="PolicyStore"/> — nunca cai para um padrão permissivo.
/// </summary>
public static partial class PolicyValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxItems = 500;

    // Everyone: permitido, mas sempre com aviso de alto risco.
    private const string EveryoneSid = "S-1-1-0";

    [GeneratedRegex(@"^[a-z0-9-]{1,40}$")]
    internal static partial Regex SlugRegex();

    [GeneratedRegex(@"^[0-9A-F]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")]
    private static partial Regex SidRegex();

    public static ValidationResult Validate(Policy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var messages = new List<ValidationError>();

        if (policy.SchemaVersion != CurrentSchemaVersion)
        {
            messages.Add(new(ValidationSeverity.Error, null, "SCHEMA_VERSION",
                $"Versão de schema não suportada: {policy.SchemaVersion} (esperado {CurrentSchemaVersion})."));
            // Schema desconhecido: não tente interpretar o resto.
            return new ValidationResult(messages);
        }

        if (policy.Items is null)
        {
            messages.Add(new(ValidationSeverity.Error, null, "NO_ITEMS", "A lista de itens é nula."));
            return new ValidationResult(messages);
        }

        if (policy.Items.Count > MaxItems)
        {
            messages.Add(new(ValidationSeverity.Error, null, "TOO_MANY_ITEMS",
                $"A política tem {policy.Items.Count} itens (máximo {MaxItems})."));
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in policy.Items)
        {
            ValidateItem(item, seenIds, messages);
        }

        return new ValidationResult(messages);
    }

    private static void ValidateItem(PolicyItem item, HashSet<string> seenIds, List<ValidationError> messages)
    {
        var id = item.Id ?? string.Empty;

        if (!SlugRegex().IsMatch(id))
        {
            messages.Add(new(ValidationSeverity.Error, id, "INVALID_ID",
                "Id deve ser um slug [a-z0-9-] com 1 a 40 caracteres."));
        }
        else if (!seenIds.Add(id))
        {
            messages.Add(new(ValidationSeverity.Error, id, "DUPLICATE_ID", $"Id duplicado: '{id}'."));
        }

        ValidateTargetPath(item, messages);

        // Hash OU publisher: nunca os dois nulos.
        if (item.Sha256 is null && item.Publisher is null)
        {
            messages.Add(new(ValidationSeverity.Error, id, "NO_HASH_OR_PUBLISHER",
                "É obrigatório informar Sha256 ou Publisher."));
        }

        if (item.Sha256 is not null && !Sha256Regex().IsMatch(item.Sha256))
        {
            messages.Add(new(ValidationSeverity.Error, id, "INVALID_HASH",
                "Sha256 deve ter 64 dígitos hexadecimais maiúsculos."));
        }

        if (item.Publisher is not null && string.IsNullOrWhiteSpace(item.Publisher.SubjectContains))
        {
            messages.Add(new(ValidationSeverity.Error, id, "INVALID_PUBLISHER",
                "PublisherRule.SubjectContains não pode ser vazio."));
        }

        if (item.ExpectedSize is < 0)
        {
            messages.Add(new(ValidationSeverity.Error, id, "INVALID_SIZE",
                "ExpectedSize não pode ser negativo."));
        }

        if (item.WorkingDirectory is { } wd && !Path.IsPathFullyQualified(wd))
        {
            messages.Add(new(ValidationSeverity.Error, id, "WORKDIR_NOT_ABSOLUTE",
                "WorkingDirectory, se presente, deve ser um caminho absoluto."));
        }

        ValidateSids(id, item.AllowedSids, messages);
    }

    private static void ValidateTargetPath(PolicyItem item, List<ValidationError> messages)
    {
        var id = item.Id ?? string.Empty;
        var path = item.TargetPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            messages.Add(new(ValidationSeverity.Error, id, "TARGET_EMPTY", "TargetPath é obrigatório."));
            return;
        }

        if (IsUnc(path))
        {
            messages.Add(new(ValidationSeverity.Error, id, "TARGET_UNC",
                "Caminhos UNC não são permitidos na v1."));
        }
        else if (!Path.IsPathFullyQualified(path))
        {
            messages.Add(new(ValidationSeverity.Error, id, "TARGET_NOT_ABSOLUTE",
                "TargetPath deve ser um caminho absoluto local."));
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new(ValidationSeverity.Error, id, "TARGET_NOT_EXE",
                "TargetPath deve terminar em .exe."));
        }
    }

    private static void ValidateSids(string id, IReadOnlyList<string>? sids, List<ValidationError> messages)
    {
        if (sids is null)
        {
            return;
        }

        foreach (var sid in sids)
        {
            if (sid is null || !SidRegex().IsMatch(sid))
            {
                messages.Add(new(ValidationSeverity.Error, id, "INVALID_SID",
                    $"SID inválido em AllowedSids: '{sid}'."));
            }
            else if (sid == EveryoneSid)
            {
                messages.Add(new(ValidationSeverity.Warning, id, "EVERYONE_SID",
                    "AllowedSids inclui Everyone (S-1-1-0): qualquer usuário poderá lançar este item."));
            }
        }
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
