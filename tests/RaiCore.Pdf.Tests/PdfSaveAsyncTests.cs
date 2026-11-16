using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Tests.Security;

namespace RaiCore.Pdf.Tests;

/// <summary><see cref="PdfDocument.SaveAsync"/>, and the small core additions of the API review (#60).</summary>
public class PdfSaveAsyncTests
{
    private static readonly DateTimeOffset Date = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveAsync_gives_the_same_file_as_Save()
    {
        var saved = Dated(EncryptionSamples.CreateRichSample(null)).ToArray();

        using var output = new AsyncOnlyStream();
        await Dated(EncryptionSamples.CreateRichSample(null)).SaveAsync(output, TestContext.Current.CancellationToken);

        Assert.Equal(saved, output.ToArray());
        Assert.True(output.AsyncWrites > 1, "Each page should be written as soon as it is encoded.");
        Assert.True(output.Flushed);
    }

    [Fact]
    public async Task SaveAsync_never_writes_synchronously()
    {
        // AsyncOnlyStream throws on every synchronous write, as an ASP.NET Core response body does.
        using var output = new AsyncOnlyStream();
        var document = new PdfDocument();
        document.AddPage().Canvas.DrawText("Hello", StandardFont.Helvetica, 12, 72, 720);
        await document.SaveAsync(output, TestContext.Current.CancellationToken);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(output.ToArray(), 0, 5), StringComparison.Ordinal);

        // The same stream makes Save fail, so it really stands in for one that forbids synchronous writes.
        using var other = new AsyncOnlyStream();
        Assert.Throws<InvalidOperationException>(() => Sample().Save(other));
    }

    [Fact]
    public async Task SaveAsync_checks_like_Save_and_honors_cancellation()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PdfDocument().SaveAsync(output, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sample().SaveAsync(null!, TestContext.Current.CancellationToken));

        // AsyncOnlyStream ignores the token, so SaveAsync itself must stop before writing anything.
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        using var ignoring = new AsyncOnlyStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sample().SaveAsync(ignoring, canceled.Token));
        Assert.Equal(0, ignoring.AsyncWrites);
    }

    [Fact]
    public void Every_exception_for_rejected_input_is_a_PdfException()
    {
        Assert.IsAssignableFrom<PdfException>(Assert.ThrowsAny<Exception>(() => PdfReader.Open("not a pdf"u8.ToArray())));
        Assert.IsAssignableFrom<PdfException>(Assert.ThrowsAny<Exception>(() => PdfFont.Load(new byte[16])));
        Assert.IsAssignableFrom<PdfException>(Assert.ThrowsAny<Exception>(() => PdfImage.Load(new byte[16])));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfPasswordException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(ResourceLimitExceededException)));
        Assert.Equal("RaiCore.Pdf.Fonts", typeof(FontFormatException).Namespace);
        Assert.Equal("RaiCore.Pdf", typeof(ImageFormatException).Namespace);
    }

    [Fact]
    public void Page_sizes_can_be_given_in_inches()
    {
        Assert.Equal(PdfPageSize.Letter, PdfPageSize.FromInches(8.5, 11));
        Assert.Equal(new PdfPageSize(72, 144), PdfPageSize.FromInches(1, 2));
    }

    private static PdfDocument Sample()
    {
        var document = new PdfDocument();
        document.AddPage();
        return document;
    }

    private static PdfDocument Dated(PdfDocument document)
    {
        document.Info.CreationDate = document.Info.ModificationDate = Date;
        return document;
    }

    /// <summary>A stream that, like an ASP.NET Core response body, refuses synchronous writes and flushes.</summary>
    private sealed class AsyncOnlyStream : Stream
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
}
