using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunAsLauncher.Policies;

/// <summary>
/// Resultado de carregar uma política. Em caso de falha (JSON inválido, schema
/// desconhecido ou validação reprovada) o resultado é <see cref="Rejected"/> e o
/// chamador deve <b>negar tudo</b> — nunca há fallback para uma política permissiva.
/// </summary>
public sealed record PolicyLoadResult
{
    private PolicyLoadResult(bool success, Policy? policy, string? reason)
    {
        Success = success;
        Policy = policy;
        Reason = reason;
    }

    public bool Success { get; }

    public Policy? Policy { get; }

    public string? Reason { get; }

    public static PolicyLoadResult Ok(Policy policy) => new(true, policy, null);

    public static PolicyLoadResult Rejected(string reason) => new(false, null, reason);
}

/// <summary>Lançada por <see cref="PolicyStore.Save"/> quando a política é inválida.</summary>
public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(ValidationResult result)
        : base("A política é inválida: " + string.Join("; ", result.Errors.Select(e => $"[{e.Code}] {e.Message}")))
    {
        Result = result;
    }

    public ValidationResult Result { get; }
}

/// <summary>
/// Lê e grava a <see cref="Policy"/> em <c>policy.json</c> de forma atômica (<c>.tmp</c> → <c>File.Move</c>),
/// sempre validando. Segue o padrão do <c>SettingsStore</c> do Fragmo (camelCase, enums como string).
/// </summary>
public sealed class PolicyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;

    public PolicyStore(string directory)
    {
        _directory = directory;
    }

    public static PolicyStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunAsLauncher"));

    public string Directory => _directory;

    public string FilePath => Path.Combine(_directory, "policy.json");

    /// <summary>Carrega do caminho padrão. Arquivo ausente = política vazia (nega tudo), não um erro.</summary>
    public PolicyLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            return PolicyLoadResult.Ok(new Policy(PolicyValidator.CurrentSchemaVersion, Array.Empty<PolicyItem>()));
        }

        return Import(FilePath);
    }

    /// <summary>Carrega e valida de um caminho qualquer, devolvendo Ok ou Rejected.</summary>
    public PolicyLoadResult Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return PolicyLoadResult.Rejected($"Não foi possível ler '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return PolicyLoadResult.Rejected($"Acesso negado a '{path}': {ex.Message}");
        }

        Policy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<Policy>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            return PolicyLoadResult.Rejected($"JSON inválido: {ex.Message}");
        }

        if (policy is null)
        {
            return PolicyLoadResult.Rejected("JSON representa um valor nulo.");
        }

        var validation = PolicyValidator.Validate(policy);
        if (!validation.IsValid)
        {
            var reason = string.Join("; ", validation.Errors.Select(e => $"[{e.Code}] {e.Message}"));
            return PolicyLoadResult.Rejected(reason);
        }

        return PolicyLoadResult.Ok(policy);
    }

    /// <summary>Valida e grava no caminho padrão de forma atômica. Lança se inválida.</summary>
    public void Save(Policy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var validation = PolicyValidator.Validate(policy);
        if (!validation.IsValid)
        {
            throw new PolicyValidationException(validation);
        }

        System.IO.Directory.CreateDirectory(_directory);
        WriteAtomic(FilePath, policy);
    }

    /// <summary>Valida e grava num caminho qualquer. Lança se inválida.</summary>
    public void Export(string path, Policy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(policy);

        var validation = PolicyValidator.Validate(policy);
        if (!validation.IsValid)
        {
            throw new PolicyValidationException(validation);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        WriteAtomic(path, policy);
    }

    private static void WriteAtomic(string path, Policy policy)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(policy, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
