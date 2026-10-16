using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Ipc;

/// <summary>
/// Enquadramento das mensagens no pipe: prefixo de 4 bytes little-endian com o tamanho,
/// seguido do JSON UTF-8. Limites: 16 KiB para <see cref="Request"/>, 1 MiB para <see cref="Response"/>.
/// Qualquer violação de formato vira <see cref="InvalidDataException"/>; stream terminado no meio
/// de uma mensagem vira <see cref="EndOfStreamException"/>.
/// </summary>
public static class Framing
{
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxResponseBytes = 1024 * 1024;

    private const int HeaderSize = sizeof(int);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Inteiros não são aceitos como enum: `"type": 99` não pode virar um valor indefinido.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        int max = MaxBytesFor<T>();

        Validate(message);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (json.Length > max)
            throw new InvalidDataException($"Mensagem {typeof(T).Name} excede o limite de {max} bytes ({json.Length}).");

        // Cabeçalho e corpo num único Write: em pipe modo mensagem, chega como uma unidade.
        byte[] frame = new byte[HeaderSize + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, HeaderSize);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        int max = MaxBytesFor<T>();

        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > max)
            throw new InvalidDataException($"Tamanho de mensagem inválido: {length} (máximo {max}).");

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON de mensagem inválido.", ex);
        }

        if (message is null)
            throw new InvalidDataException("Mensagem vazia (null).");

        Validate(message);
        return message;
    }

    private static int MaxBytesFor<T>()
    {
        if (typeof(T) == typeof(Request)) return MaxRequestBytes;
        if (typeof(T) == typeof(Response)) return MaxResponseBytes;
        throw new NotSupportedException($"Tipo de mensagem não suportado: {typeof(T).Name}.");
    }

    private static void Validate<T>(T message)
    {
        switch (message)
        {
            case Request r:
                if (!Enum.IsDefined(r.Type))
                    throw new InvalidDataException($"Tipo de requisição desconhecido: {(int)r.Type}.");
                if (r.ItemId is not null && !PolicyValidator.SlugRegex().IsMatch(r.ItemId))
                    throw new InvalidDataException("ItemId inválido (esperado slug [a-z0-9-]{1,40}).");
                break;
            case Response resp:
                if (!Enum.IsDefined(resp.Code))
                    throw new InvalidDataException($"Código de resultado desconhecido: {(int)resp.Code}.");
                break;
        }
    }
}
