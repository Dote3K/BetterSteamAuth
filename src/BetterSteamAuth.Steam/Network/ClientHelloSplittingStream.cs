using System.Buffers.Binary;
using System.Text;

namespace BetterSteamAuth.Steam.Network;

/// <summary>
/// Wraps a connection's stream and splits the first TLS handshake record (the ClientHello)
/// into two records, cut inside the server name, written as separate TCP sends. Everything
/// else passes through.
/// </summary>
public sealed class ClientHelloSplittingStream(Stream inner, string serverName) : Stream
{
    private const byte HandshakeRecord = 0x16;
    private const int RecordHeaderSize = 5;

    private readonly byte[] _serverName = Encoding.ASCII.GetBytes(serverName);
    private bool _split;

    public override bool CanRead => inner.CanRead;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Splits one TLS record into two records carrying the same handshake bytes. Returns null
    /// when <paramref name="data"/> isn't a single complete handshake record.
    /// </summary>
    public static (byte[] First, byte[] Second, int Consumed)? Split(ReadOnlySpan<byte> data, ReadOnlySpan<byte> serverName)
    {
        if (data.Length <= RecordHeaderSize || data[0] != HandshakeRecord || data[1] != 0x03)
        {
            return null;
        }

        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        if (payloadLength < 2 || data.Length < RecordHeaderSize + payloadLength)
        {
            return null;
        }

        var payload = data.Slice(RecordHeaderSize, payloadLength);
        int nameAt = serverName.IsEmpty ? -1 : payload.IndexOf(serverName);
        int cut = nameAt >= 0 ? nameAt + Math.Max(1, serverName.Length / 2) : payloadLength / 2;

        return (Record(data[..3], payload[..cut]), Record(data[..3], payload[cut..]), RecordHeaderSize + payloadLength);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_split && Split(buffer.Span, _serverName) is { } parts)
        {
            _split = true;
            await inner.WriteAsync(parts.First, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(parts.Second, cancellationToken).ConfigureAwait(false);
            if (parts.Consumed < buffer.Length)
            {
                await inner.WriteAsync(buffer[parts.Consumed..], cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] Record(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        byte[] record = new byte[RecordHeaderSize + payload.Length];
        header.CopyTo(record);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), (ushort)payload.Length);
        payload.CopyTo(record.AsSpan(RecordHeaderSize));
        return record;
    }
}
