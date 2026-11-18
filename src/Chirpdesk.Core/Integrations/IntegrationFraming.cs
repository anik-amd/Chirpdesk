using System.Buffers.Binary;

namespace Chirpdesk.Core.Integrations;

/// <summary>
/// Length-prefixed frames for the helper pipe: a 4-byte little-endian unsigned length, then that
/// many UTF-8 JSON bytes. A frame is never empty and never larger than
/// <see cref="MaxFrameBytes"/>; anything else is a protocol error, not a message.
/// </summary>
public static class IntegrationFraming
{
    public const int HeaderBytes = 4;
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>
    /// Reads one frame. Returns null on a clean end of stream at a frame boundary; throws
    /// <see cref="InvalidDataException"/> for an oversized length, a zero length or a stream that
    /// ends inside a frame.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderBytes];
        var filled = 0;

        while (filled < HeaderBytes)
        {
            var read = await stream.ReadAsync(header.AsMemory(filled, HeaderBytes - filled), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                if (filled == 0)
                {
                    return null;
                }

                throw new InvalidDataException("stream ended inside a frame header");
            }

            filled += read;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"frame length {length} is outside 1..{MaxFrameBytes}");
        }

        var payload = new byte[length];
        filled = 0;

        while (filled < length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(filled, length - filled), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidDataException("stream ended inside a frame");
            }

            filled += read;
        }

        return payload;
    }

    /// <summary>Writes one frame and flushes it.</summary>
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length is <= 0 or > MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"frame length must be 1..{MaxFrameBytes} bytes");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
