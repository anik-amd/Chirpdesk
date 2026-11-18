using System.IO.Pipes;
using System.Text.Json;
using Chirpdesk.Core.Integrations;

// Test-only helper: proves the host can launch a process, handshake, receive a toast and an
// unread count, answer a command and stop. It is never shipped or installed from a repository.

var pipeName = Environment.GetEnvironmentVariable("CHIRPDESK_PIPE");
var token = Environment.GetEnvironmentVariable("CHIRPDESK_TOKEN");
var protocolText = Environment.GetEnvironmentVariable("CHIRPDESK_PROTOCOL");

if (string.IsNullOrWhiteSpace(pipeName)
    || string.IsNullOrWhiteSpace(token)
    || !int.TryParse(protocolText, out var protocolVersion))
{
    return 2;
}

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(5000);

await IntegrationFraming.WriteAsync(
    pipe,
    JsonRpcCodec.Request(1, "initialize", new
    {
        protocolVersion,
        token,
        pluginVersion = "1.0.0",
        appVersion = "stub",
    }));

var initializeResponse = await ReadEnvelopeAsync(pipe);

if (initializeResponse is null || initializeResponse.Kind != JsonRpcKind.Response || initializeResponse.Error is not null)
{
    return 3;
}

await SendRequestAsync(pipe, 2, "notify", new
{
    title = "Stub integration",
    body = "The stub helper is running.",
    tag = "stub",
});

await SendRequestAsync(pipe, 3, "unread", new { count = 3 });

while (true)
{
    var envelope = await ReadEnvelopeAsync(pipe);

    if (envelope is null)
    {
        break;
    }

    if (envelope.Kind == JsonRpcKind.Notification)
    {
        if (string.Equals(envelope.Method, "shutdown", StringComparison.Ordinal))
        {
            break;
        }

        continue;
    }

    if (envelope.Kind != JsonRpcKind.Request)
    {
        continue;
    }

    if (string.Equals(envelope.Method, "ping", StringComparison.Ordinal))
    {
        await IntegrationFraming.WriteAsync(pipe, JsonRpcCodec.Response(envelope.Id, new { ok = true }));
        continue;
    }

    if (string.Equals(envelope.Method, "command", StringComparison.Ordinal))
    {
        var id = envelope.Params.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : "unknown";

        await IntegrationFraming.WriteAsync(
            pipe,
            JsonRpcCodec.Response(envelope.Id, new { ok = true, message = $"stub ran {id}" }));
        continue;
    }

    await IntegrationFraming.WriteAsync(
        pipe,
        JsonRpcCodec.ErrorResponse(envelope.Id, -32601, "method not found"));
}

return 0;

static async Task SendRequestAsync(Stream stream, long id, string method, object parameters)
{
    await IntegrationFraming.WriteAsync(stream, JsonRpcCodec.Request(id, method, parameters));

    // Requests the host answers are drained here so the pipe never fills.
    _ = await ReadEnvelopeAsync(stream);
}

static async Task<JsonRpcEnvelope?> ReadEnvelopeAsync(Stream stream)
{
    var frame = await IntegrationFraming.ReadAsync(stream);

    if (frame is null)
    {
        return null;
    }

    return JsonRpcCodec.TryParse(frame, out var envelope, out var error)
        ? envelope
        : throw new InvalidOperationException(error);
}
