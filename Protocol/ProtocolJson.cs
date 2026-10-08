using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempLab.Protocol;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e
            ? e.Deserialize<T>(Options)
            : default;

    public static byte[] SerializeEnvelope(Envelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    /// <exception cref="ProtocolException">Body is not a valid envelope.</exception>
    public static Envelope DeserializeEnvelope(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(utf8, Options);
            if (env is null || string.IsNullOrEmpty(env.Type))
                throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "envelope missing type");
            return env;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "frame body is not valid JSON: " + ex.Message, ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "frame body is not valid UTF-8", ex);
        }
    }

    public static Envelope Request(string type, string id, object? payload = null) => new()
    {
        Type = type,
        Id = id,
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), Options),
    };

    public static Envelope Ok(string? id, object? result = null) => new()
    {
        Type = MessageTypes.Response,
        Id = id,
        Payload = ToElement(new ResponsePayload
        {
            Ok = true,
            Result = result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), Options),
        }),
    };

    public static Envelope Error(string? id, string code, string message) => new()
    {
        Type = MessageTypes.Response,
        Id = id,
        Payload = ToElement(new ResponsePayload { Ok = false, Error = new ErrorInfo(code, message) }),
    };

    public static Envelope Event(string type, object payload) => new()
    {
        Type = type,
        Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), Options),
    };
}
