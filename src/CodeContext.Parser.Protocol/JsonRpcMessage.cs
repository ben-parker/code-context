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

    [JsonPropertyName("result")]
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
