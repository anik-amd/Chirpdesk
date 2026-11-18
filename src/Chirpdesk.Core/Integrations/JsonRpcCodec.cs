using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chirpdesk.Core.Integrations;

public enum JsonRpcKind
{
    Request,
    Notification,
    Response,
}

public sealed record JsonRpcError(int Code, string Message);

/// <summary>
/// Parsed JSON-RPC 2.0 envelope. A request always carries a positive integer id and a method; a
/// notification carries a method and no id; a response carries an id and exactly one of result or
/// error. Anything else is refused before a dispatcher sees it.
/// </summary>
public sealed class JsonRpcEnvelope
{
    public required JsonRpcKind Kind { get; init; }

    public long Id { get; init; }

    public string Method { get; init; } = string.Empty;

    public JsonElement Params { get; init; }

    public JsonElement Result { get; init; }

    public JsonRpcError? Error { get; init; }
}

public static class JsonRpcCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses one frame. <paramref name="envelope"/> is null and <paramref name="error"/> explains
    /// why when the frame is not a valid JSON-RPC 2.0 message.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out JsonRpcEnvelope? envelope, out string? error)
    {
        envelope = null;
        error = null;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(frame.ToArray());
        }
        catch (JsonException ex)
        {
            error = $"frame is not valid json: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "json-rpc message must be an object";
                return false;
            }

            if (!root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String
                || !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal))
            {
                error = "json-rpc version must be 2.0";
                return false;
            }

            var hasId = root.TryGetProperty("id", out var idElement);
            var hasMethod = root.TryGetProperty("method", out var methodElement);

            if (hasMethod)
            {
                if (methodElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(methodElement.GetString()))
                {
                    error = "method must be a non-empty string";
                    return false;
                }

                var method = methodElement.GetString()!;

                if (hasId
                    && (idElement.ValueKind != JsonValueKind.Number
                        || !idElement.TryGetInt64(out var requestId)
                        || requestId < 1))
                {
                    error = "request id must be a positive integer";
                    return false;
                }

                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement.Clone()
                    : default;

                envelope = hasId
                    ? new JsonRpcEnvelope
                    {
                        Kind = JsonRpcKind.Request,
                        Id = idElement.GetInt64(),
                        Method = method,
                        Params = parameters,
                    }
                    : new JsonRpcEnvelope
                    {
                        Kind = JsonRpcKind.Notification,
                        Method = method,
                        Params = parameters,
                    };

                return true;
            }

            if (!hasId
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var responseId)
                || responseId < 1)
            {
                error = "response id must be a positive integer";
                return false;
            }

            var hasResult = root.TryGetProperty("result", out var resultElement);
            var hasError = root.TryGetProperty("error", out var errorElement);

            if (hasResult == hasError)
            {
                error = "response needs exactly one of result or error";
                return false;
            }

            JsonRpcError? rpcError = null;

            if (hasError)
            {
                if (errorElement.ValueKind != JsonValueKind.Object)
                {
                    error = "error must be an object";
                    return false;
                }

                var code = errorElement.TryGetProperty("code", out var codeElement)
                    && codeElement.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : -32603;

                var message = errorElement.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? "error"
                    : "error";

                rpcError = new JsonRpcError(code, message);
            }

            envelope = new JsonRpcEnvelope
            {
                Kind = JsonRpcKind.Response,
                Id = responseId,
                Result = hasResult ? resultElement.Clone() : default,
                Error = rpcError,
            };

            return true;
        }
    }

    public static byte[] Request(long id, string method, object? parameters = null) =>
        Serialize(new WireMessage { Id = id, Method = method, Params = parameters });

    public static byte[] Notification(string method, object? parameters = null) =>
        Serialize(new WireMessage { Method = method, Params = parameters });

    public static byte[] Response(long id, object? result) =>
        Serialize(new WireMessage { Id = id, Result = result });

    public static byte[] ErrorResponse(long id, int code, string message) =>
        Serialize(new WireMessage { Id = id, Error = new JsonRpcError(code, message) });

    private static byte[] Serialize(WireMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    private sealed class WireMessage
    {
        public string Jsonrpc { get; set; } = "2.0";

        public long? Id { get; set; }

        public string? Method { get; set; }

        public object? Params { get; set; }

        public object? Result { get; set; }

        public JsonRpcError? Error { get; set; }
    }
}
