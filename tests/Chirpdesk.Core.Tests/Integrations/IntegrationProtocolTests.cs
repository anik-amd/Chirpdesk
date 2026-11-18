using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Chirpdesk.Core.Integrations;

namespace Chirpdesk.Core.Tests.Integrations;

public sealed class IntegrationProtocolTests
{
    [Fact]
    public async Task Frames_round_trip_and_end_cleanly()
    {
        using var stream = new MemoryStream();
        await IntegrationFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"a":1}"""));
        await IntegrationFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"b":2}"""));

        stream.Position = 0;
        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString((await IntegrationFraming.ReadAsync(stream))!));
        Assert.Equal("""{"b":2}""", Encoding.UTF8.GetString((await IntegrationFraming.ReadAsync(stream))!));
        Assert.Null(await IntegrationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Frames_split_across_reads_are_reassembled()
    {
        using var payload = new MemoryStream();
        await IntegrationFraming.WriteAsync(payload, Encoding.UTF8.GetBytes("""{"method":"ping"}"""));
        payload.Position = 0;

        using var trickle = new OneByteStream(payload.ToArray());
        var frame = await IntegrationFraming.ReadAsync(trickle);

        Assert.Equal("""{"method":"ping"}""", Encoding.UTF8.GetString(frame!));
    }

    [Fact]
    public async Task An_oversized_frame_is_refused()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, IntegrationFraming.MaxFrameBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => IntegrationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task A_truncated_frame_is_refused()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        stream.Write(header);
        stream.Write([1, 2, 3]);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => IntegrationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Empty_frames_are_never_written()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => IntegrationFraming.WriteAsync(stream, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Requests_and_notifications_parse()
    {
        var request = JsonRpcCodec.Request(7, "command", new { id = "open" });
        Assert.True(JsonRpcCodec.TryParse(request, out var parsedRequest, out var requestError), requestError);
        Assert.NotNull(parsedRequest);
        Assert.Equal(JsonRpcKind.Request, parsedRequest.Kind);
        Assert.Equal(7, parsedRequest.Id);
        Assert.Equal("command", parsedRequest.Method);
        Assert.Equal("open", parsedRequest.Params.GetProperty("id").GetString());

        var notification = JsonRpcCodec.Notification("notify", new { title = "Hello" });
        Assert.True(JsonRpcCodec.TryParse(notification, out var parsedNotification, out var notificationError), notificationError);
        Assert.NotNull(parsedNotification);
        Assert.Equal(JsonRpcKind.Notification, parsedNotification.Kind);
        Assert.Equal(0, parsedNotification.Id);
        Assert.Equal("notify", parsedNotification.Method);
    }

    [Fact]
    public void Responses_parse_with_result_and_error()
    {
        Assert.True(JsonRpcCodec.TryParse(
            JsonRpcCodec.Response(3, new { ok = true }),
            out var success,
            out var successError), successError);
        Assert.NotNull(success);
        Assert.Equal(JsonRpcKind.Response, success.Kind);
        Assert.True(success.Result.GetProperty("ok").GetBoolean());
        Assert.Null(success.Error);

        Assert.True(JsonRpcCodec.TryParse(
            JsonRpcCodec.ErrorResponse(4, -32601, "no such method"),
            out var failure,
            out var failureError), failureError);
        Assert.NotNull(failure);
        Assert.Equal(-32601, failure.Error!.Code);
        Assert.Equal("no such method", failure.Error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"jsonrpc":"1.0","method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"1","method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":0,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{},"error":{"code":1,"message":"x"}}""")]
    [InlineData("not json")]
    public void Invalid_envelopes_are_refused(string frame)
    {
        Assert.False(JsonRpcCodec.TryParse(Encoding.UTF8.GetBytes(frame), out var envelope, out var error));
        Assert.Null(envelope);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_method_allowlist_is_closed()
    {
        Assert.True(IntegrationProtocol.IsHelperMethod("notify"));
        Assert.True(IntegrationProtocol.IsHelperMethod("initialize"));
        Assert.False(IntegrationProtocol.IsHelperMethod("command"));
        Assert.False(IntegrationProtocol.IsHelperMethod("eval"));

        Assert.True(IntegrationProtocol.IsHostMethod("command"));
        Assert.True(IntegrationProtocol.IsHostMethod("shutdown"));
        Assert.False(IntegrationProtocol.IsHostMethod("notify"));
    }

    [Fact]
    public void Notification_and_unread_methods_map_to_permissions()
    {
        Assert.Equal("notifications", IntegrationProtocol.RequiredPermission("notify"));
        Assert.Equal("unread", IntegrationProtocol.RequiredPermission("unread"));
        Assert.Null(IntegrationProtocol.RequiredPermission("status"));
        Assert.Null(IntegrationProtocol.RequiredPermission("log"));
        Assert.Null(IntegrationProtocol.RequiredPermission("initialize"));
    }

    [Fact]
    public void Initialize_requires_a_version_and_a_token()
    {
        Assert.True(
            IntegrationProtocol.TryReadInitialize(
                Params("""{"protocolVersion":1,"token":"abc"}"""),
                out var message,
                out var error), error);
        Assert.NotNull(message);
        Assert.Equal(1, message.ProtocolVersion);
        Assert.Equal("abc", message.Token);

        Assert.False(IntegrationProtocol.TryReadInitialize(
            Params("""{"protocolVersion":1}"""),
            out _,
            out var missingToken));
        Assert.Contains("token", missingToken, StringComparison.Ordinal);
    }

    [Fact]
    public void Notify_payloads_are_capped_and_typed()
    {
        Assert.True(
            IntegrationProtocol.TryReadNotify(
                Params("""{"title":"Hello","body":"World","tag":"chat","silent":true,"activateCommand":"open"}"""),
                out var message,
                out var error), error);
        Assert.NotNull(message);
        Assert.Equal("Hello", message.Title);
        Assert.True(message.Silent);
        Assert.Equal("open", message.ActivateCommand);

        var tooLongTitle = new string('x', IntegrationProtocol.MaxTitleLength + 1);
        Assert.False(IntegrationProtocol.TryReadNotify(
            Params($$"""{"title":"{{tooLongTitle}}"}"""),
            out _,
            out var titleError));
        Assert.Contains("longer", titleError, StringComparison.Ordinal);

        Assert.False(IntegrationProtocol.TryReadNotify(
            Params("""{"title":"Hello","activateCommand":"Not An Id"}"""),
            out _,
            out var commandError));
        Assert.Contains("activateCommand", commandError, StringComparison.Ordinal);

        Assert.False(IntegrationProtocol.TryReadNotify(
            Params("""{"title":"Hello","silent":"yes"}"""),
            out _,
            out var silentError));
        Assert.Contains("silent", silentError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"count":0}""", true)]
    [InlineData("""{"count":9999}""", true)]
    [InlineData("""{"count":-1}""", false)]
    [InlineData("""{"count":10000}""", false)]
    [InlineData("""{"count":"3"}""", false)]
    public void Unread_counts_are_bounded(string json, bool valid)
    {
        Assert.Equal(
            valid,
            IntegrationProtocol.TryReadUnread(Params(json), out var message, out _));
        if (valid)
        {
            Assert.NotNull(message);
        }
    }

    [Theory]
    [InlineData("""{"state":"ready"}""", true)]
    [InlineData("""{"state":"busy","detail":"syncing"}""", true)]
    [InlineData("""{"state":"sleeping"}""", false)]
    [InlineData("""{"state":"error","detail":"line one"}""", true)]
    public void Status_states_are_closed(string json, bool valid)
    {
        Assert.Equal(valid, IntegrationProtocol.TryReadStatus(Params(json), out _, out _));
    }

    [Theory]
    [InlineData("""{"level":"info","message":"started"}""", true)]
    [InlineData("""{"level":"verbose","message":"x"}""", false)]
    [InlineData("""{"level":"warn"}""", false)]
    [InlineData("""{"level":"error","message":""}""", false)]
    public void Log_lines_are_level_checked(string json, bool valid)
    {
        Assert.Equal(valid, IntegrationProtocol.TryReadLog(Params(json), out _, out _));
    }

    [Fact]
    public void Command_arguments_are_bounded()
    {
        Assert.True(
            IntegrationProtocol.TryReadCommand(
                Params("""{"id":"send","argument":"hi"}"""),
                out var message,
                out var error), error);
        Assert.NotNull(message);
        Assert.Equal("send", message.Id);
        Assert.Equal("hi", message.Argument);

        Assert.False(IntegrationProtocol.TryReadCommand(Params("""{"id":"BAD"}"""), out _, out _));

        var tooLong = new string('x', IntegrationProtocol.MaxArgumentLength + 1);
        Assert.False(IntegrationProtocol.TryReadCommand(
            Params($$"""{"id":"send","argument":"{{tooLong}}"}"""),
            out _,
            out var argumentError));
        Assert.Contains("longer", argumentError, StringComparison.Ordinal);
    }

    private static JsonElement Params(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Returns one byte per read so the framing loop is exercised across reads.</summary>
    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length)
            {
                return 0;
            }

            buffer[0] = data[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
