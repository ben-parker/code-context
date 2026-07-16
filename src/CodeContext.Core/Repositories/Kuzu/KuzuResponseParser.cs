using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeContext.Core.Serialization;

namespace CodeContext.Core.Repositories.Kuzu;

/// <summary>
/// Helper class to parse responses from the Kuzu Python API
/// </summary>
public static class KuzuResponseParser
{
    /// <summary>
    /// Parse a response that may contain _query_stats metadata
    /// </summary>
    public static T? ParseResponse<T>(string json, JsonSerializerContext context, JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        // Check if the response is literally "null"
        if (json.Trim() == "null")
            return default;

        // Check if this is an error response
        if (IsErrorResponse(json))
        {
            throw ParseErrorResponse(json);
        }

        // Parse as JsonNode to handle different response formats
        var node = JsonNode.Parse(json);
        if (node == null)
            return default;

        // Check if response has _query_stats wrapper
        if (node is JsonObject obj && obj.ContainsKey("_query_stats"))
        {
            // Response is wrapped with query stats
            if (obj.ContainsKey("results"))
            {
                // List response wrapped in results property
                var resultsNode = obj["results"];
                if (resultsNode != null)
                {
                    return resultsNode.Deserialize(typeInfo);
                }
            }
            else if (obj.ContainsKey("result"))
            {
                // Single result wrapped in result property
                var resultNode = obj["result"];
                if (resultNode != null)
                {
                    return resultNode.Deserialize(typeInfo);
                }
            }
            else
            {
                // Direct object with _query_stats added
                // Remove _query_stats and deserialize the rest
                obj.Remove("_query_stats");
                return node.Deserialize(typeInfo);
            }
        }

        // No wrapper, deserialize directly
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    /// <summary>
    /// Check if the response is an error response from Kuzu
    /// </summary>
    public static bool IsErrorResponse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj && obj.ContainsKey("error") && obj["error"]?.GetValue<bool>() == true)
            {
                return true;
            }
        }
        catch
        {
            // Not valid JSON or not an error response
        }
        return false;
    }

    /// <summary>
    /// Parse an error response and throw appropriate exception
    /// </summary>
    public static Exception ParseErrorResponse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                var errorType = obj["error_type"]?.GetValue<string>() ?? "unknown";
                var message = obj["message"]?.GetValue<string>() ?? "Unknown error";
                var suggestions = obj["suggestions"]?.AsArray()?.Select(s => s?.GetValue<string>() ?? "").ToList();

                var fullMessage = message;
                if (suggestions != null && suggestions.Any())
                {
                    fullMessage += $"\nSuggestions:\n- {string.Join("\n- ", suggestions)}";
                }

                return errorType switch
                {
                    "query_timeout" => new TimeoutException(fullMessage),
                    "query_error" => new InvalidOperationException(fullMessage),
                    _ => new Exception(fullMessage)
                };
            }
        }
        catch
        {
            // Failed to parse error response
        }
        return new Exception($"Kuzu API error: {json}");
    }

    // NOTE: This method is commented out due to AOT incompatibility with generic deserialization
    // If query stats extraction is needed in the future, implement with proper JsonTypeInfo
    /*
    /// <summary>
    /// Extract query statistics from a response if present
    /// </summary>
    public static Dictionary<string, object>? ExtractQueryStats(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj && obj.ContainsKey("_query_stats"))
            {
                var statsNode = obj["_query_stats"];
                if (statsNode != null)
                {
                    return statsNode.Deserialize<Dictionary<string, object>>();
                }
            }
        }
        catch
        {
            // Failed to extract stats
        }
        return null;
    }
    */
}