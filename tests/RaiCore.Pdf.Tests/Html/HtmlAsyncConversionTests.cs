using System.Diagnostics;
using RaiCore.Pdf.Benchmarks;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>The asynchronous counterparts: <see cref="HtmlToPdf.ConvertToStreamAsync(string, Stream, HtmlConverterOptions?, CancellationToken)"/> and <see cref="HtmlToPdf.ConvertToBytesAsync"/>.</summary>
public class HtmlAsyncConversionTests
{
    private static readonly HtmlConverterOptions Fixed = new() { TimeProvider = new DeterminismTests.FixedClock(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero)) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task Converting_to_a_stream_asynchronously_never_writes_synchronously()
    {
        var html = Benchmarks.Documents.ReportHtml(chapters: 40);
        using var output = new AsyncOnlyStream();
        await HtmlToPdf.ConvertToStreamAsync(html, output, Fixed, TestContext.Current.CancellationToken);

        using var expected = new MemoryStream();
        HtmlToPdf.ConvertToStream(html, expected, Fixed);
        Assert.True(expected.ToArray().AsSpan().SequenceEqual(output.ToArray()), "The asynchronous output differs from the synchronous one.");
        Assert.True(output.AsyncWrites > 6, $"Only {output.AsyncWrites} writes of up to {OutputPipe.ChunkSize} bytes: too few to fill the pipe.");
        Assert.True(output.Flushed);
    }

    [Fact(Timeout = 60_000)] // A conversion left waiting for room in the pipe would hang.
    public async Task A_failing_output_stops_the_conversion_with_its_own_error()
    {
        using var output = new AsyncOnlyStream(failAfter: 1000);
        var started = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<IOException>(() => HtmlToPdf.ConvertToStreamAsync(Benchmarks.Documents.ReportHtml(chapters: 40), output, Fixed, TestContext.Current.CancellationToken));

        Assert.Equal("The client disconnected.", ex.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failing_conversion_reports_its_error_after_writing_what_it_had()
    {
        using var output = new AsyncOnlyStream();
        var options = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxPages = 2 } };

        var ex = await Assert.ThrowsAsync<ResourceLimitExceededException>(() => HtmlToPdf.ConvertToStreamAsync(Benchmarks.Documents.ReportHtml(chapters: 8), output, options, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ResourceLimits.MaxPages), ex.LimitName);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_canceled_token_writes_nothing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var output = new AsyncOnlyStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToPdf.ConvertToStreamAsync("<p>x</p>", output, cancellationToken: new CancellationToken(canceled: true)));

        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Converting_to_bytes_asynchronously_gives_the_same_bytes()
    {
        var html = Benchmarks.Documents.ReportHtml(chapters: 1);

        Assert.Equal(HtmlToPdf.ConvertToBytes(html, Fixed), await HtmlToPdf.ConvertToBytesAsync(html, Fixed, Ct));
    }

    [Fact]
    public async Task The_pipe_makes_the_conversion_wait_while_the_output_is_slow()
    {
        using var pipe = new OutputPipe(bounded: true);
        var chunk = new byte[OutputPipe.ChunkSize];
        var writer = new Thread(() =>
        {
            for (var i = 0; i < 12; i++)
            {
                chunk[0] = (byte)i;
                pipe.Write(chunk);
            }

            pipe.Complete(succeeded: true);
        });
        writer.Start();

        Assert.False(writer.Join(TimeSpan.FromMilliseconds(300)), "The writer did not wait for room.");
        using var output = new MemoryStream();
        await pipe.DrainToAsync(output, Ct);

        Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
        var written = output.ToArray();
        Assert.Equal(12 * OutputPipe.ChunkSize, written.Length);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), Enumerable.Range(0, 12).Select(i => written[i * OutputPipe.ChunkSize]));
    }

    [Fact]
    public void Aborting_the_pipe_stops_the_conversion_at_its_next_write_even_with_room_left()
    {
        using var pipe = new OutputPipe(bounded: true);
        pipe.Write(new byte[OutputPipe.ChunkSize]); // One of the four places is taken.

        pipe.Abort();

        Assert.ThrowsAny<OperationCanceledException>(() => pipe.Write(new byte[OutputPipe.ChunkSize]));
    }

    [Fact]
    public void Aborting_the_pipe_stops_a_waiting_conversion()
    {
        using var pipe = new OutputPipe(bounded: true);
        Exception? error = null;
        var writer = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    pipe.Write(new byte[OutputPipe.ChunkSize]);
                }
            }
            catch (OperationCanceledException ex)
            {
                error = ex;
            }
        });
        writer.Start();
        Assert.False(writer.Join(TimeSpan.FromMilliseconds(300)));

        pipe.Abort();

        Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<OperationCanceledException>(error, exactMatch: false);
    }
}
