using System.Threading.Channels;

namespace RaiCore.Pdf.Html;

/// <summary>
/// The stream a conversion thread writes a PDF to when the caller's stream must be written asynchronously (ASP.NET
/// Core response bodies forbid synchronous writes): what the conversion writes is cut into chunks, and the caller's
/// side copies them with <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>. Only a few chunks
/// wait at a time, so the conversion pauses while the output is slow, and memory stays that of page-by-page writing.
/// </summary>
internal sealed class OutputPipe : Stream
{
    /// <summary>How much is collected before it is handed over.</summary>
    internal const int ChunkSize = 64 * 1024;

    private readonly Channel<byte[]> _chunks;
    private readonly CancellationTokenSource _aborted = new();
    private readonly MemoryStream _buffer = new();

    /// <param name="bounded">
    /// Whether the conversion waits for room: not where it runs on the caller's own thread (in the browser), since
    /// nothing would make room until it returns.
    /// </param>
    public OutputPipe(bool bounded)
    {
        _chunks = bounded
            ? Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true })
            : Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _buffer.Write(buffer);
        if (_buffer.Length >= ChunkSize)
        {
            Send();
        }
    }

    /// <summary>Hands over what was written so far.</summary>
    public override void Flush() => Send();

    /// <summary>Hands over the rest; <see cref="DrainToAsync"/> then ends. Called by the conversion when it ends, also on failure.</summary>
    public void Complete(bool succeeded)
    {
        if (succeeded)
        {
            Send();
        }

        _chunks.Writer.TryComplete();
    }

    /// <summary>Stops the conversion at its next write, e.g. because the output failed; nothing more is copied.</summary>
    public void Abort() => _aborted.Cancel();

    /// <summary>Writes the chunks to <paramref name="output"/> until the conversion completes the pipe.</summary>
    public async Task DrainToAsync(Stream output, CancellationToken cancellationToken)
    {
        while (await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_chunks.Reader.TryRead(out var chunk))
            {
                await output.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aborted.Dispose();
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Send()
    {
        _aborted.Token.ThrowIfCancellationRequested();
        if (_buffer.Length == 0)
        {
            return;
        }

        var chunk = _buffer.ToArray();
        _buffer.SetLength(0);
        if (!_chunks.Writer.TryWrite(chunk))
        {
            // Full: wait on this (the conversion's own) thread until the caller's side has written a chunk.
            _chunks.Writer.WriteAsync(chunk, _aborted.Token).AsTask().GetAwaiter().GetResult();
        }
    }
}
