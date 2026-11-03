using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexArcana.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ClientMessage))]
[JsonSerializable(typeof(ServerMessage))]
internal sealed partial class WireContext : JsonSerializerContext;

/// <summary>Codifica e decodifica dei messaggi, generata a compilazione (niente reflection, compatibile con AOT).</summary>
public static class Wire
{
    public const int ProtocolVersion = 4;

    public static string Encode(ClientMessage message) => JsonSerializer.Serialize(message, WireContext.Default.ClientMessage);
    public static string Encode(ServerMessage message) => JsonSerializer.Serialize(message, WireContext.Default.ServerMessage);

    public static ClientMessage DecodeClient(string line) => Decode(line, WireContext.Default.ClientMessage);
    public static ServerMessage DecodeServer(string line) => Decode(line, WireContext.Default.ServerMessage);

    private static T Decode<T>(string line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(line, type) ?? throw new ProtocolException("Messaggio vuoto.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ProtocolException("Messaggio non valido: " + ex.Message);
        }
    }
}

public sealed class ProtocolException(string message) : Exception(message);
