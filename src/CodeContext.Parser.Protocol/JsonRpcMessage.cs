using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeContext.Parser.Protocol;

/// <summary>
/// JSON-RPC 2.0 envelope. Requests have <c>id</c> + <c>method</c>, notifications
/// have <c>method</c> only, responses have <c>id</c> + (<c>result</c> xor <c>error</c>).
/// Only numeric ids are used by this protocol.
/// </summary>
public sealed class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    [JsonRequired]
    public string JsonRpc { get; set; } = ParserProtocol.JsonRpcVersion;

    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    /// <summary>
    /// The result; a JSON <c>null</c> (the schema's result of <c>shutdown</c>) is a present element of kind
    /// <see cref="JsonValueKind.Null"/>, so a response that carries it has a result. Absent when the member is.
    /// </summary>
    [JsonPropertyName("result")]
    [JsonConverter(typeof(PresentNullJsonElementConverter))]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonIgnore]
    public bool IsResponse => Id is not null && Method is null;

    [JsonIgnore]
    public bool IsRequest => Id is not null && Method is not null;

    [JsonIgnore]
    public bool IsNotification => Id is null && Method is not null;
}

/// <summary>
/// Reads a member written as JSON <c>null</c> as an element of kind <see cref="JsonValueKind.Null"/>, not as no value:
/// for a nullable <see cref="JsonElement"/> the serializer otherwise makes the two the same, and a response's
/// <c>"result": null</c> would read as a response with neither result nor error. A member left out stays null.
/// </summary>
public sealed class PresentNullJsonElementConverter : JsonConverter<JsonElement?>
{
    public override bool HandleNull => true;

    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } element) element.WriteTo(writer);
        else writer.WriteNullValue();
    }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>The remote endpoint answered a request with a JSON-RPC error.</summary>
public sealed class JsonRpcRemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>The byte stream violated the framing or JSON-RPC envelope contract.</summary>
public sealed class ParserProtocolViolationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The connection closed (EOF or disposal) while requests were outstanding.</summary>
public sealed class ParserConnectionClosedException(string message)
    : Exception(message);
