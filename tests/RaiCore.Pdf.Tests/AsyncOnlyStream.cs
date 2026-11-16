namespace RaiCore.Pdf.Tests;

/// <summary>
/// A stream that, like an ASP.NET Core response body, refuses synchronous writes and flushes; and, like one whose client
/// went away, fails once more than <c>failAfter</c> bytes would have been written.
/// </summary>
internal sealed class AsyncOnlyStream(long failAfter = long.MaxValue) : Stream
{
    private readonly MemoryStream _content = new();

    public int AsyncWrites { get; private set; }

    public bool Flushed { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] ToArray() => _content.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_content.Length + buffer.Length > failAfter)
        {
            throw new IOException("The client disconnected.");
        }

        AsyncWrites++;
        _content.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flushed = true;
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }

        base.Dispose(disposing);
    }
}
