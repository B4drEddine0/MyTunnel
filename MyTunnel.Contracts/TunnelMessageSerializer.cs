using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyTunnel.Contracts;

public static class TunnelMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // The original ping put "type" first, but other implementations may not.
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        // Unknown fields are ignored so a newer peer can add optional data without breaking older ones.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
    };

    public static byte[] Serialize(TunnelMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <summary>Parses and validates one control message. Any failure is reported as <see cref="ProtocolException"/>.</summary>
    public static TunnelMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > TunnelProtocol.MaxControlMessageBytes)
        {
            throw new ProtocolException(ProtocolErrorCodes.MessageTooLarge, "Control message is too large.");
        }

        TunnelMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TunnelMessage>(utf8Json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            // Covers malformed JSON, a missing or unknown "type", wrong field types and missing required fields.
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Malformed control message.", ex);
        }

        if (message is null)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Control message must be a JSON object.");
        }

        message.Validate();
        return message;
    }
}
