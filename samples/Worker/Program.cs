// PDFs made in the background: a hosted service takes jobs from a queue and writes each PDF, with the converter's
// diagnostics going to the app's log. The same code fits an Azure Function (a queue-triggered function calls
// HtmlToPdf the way PdfWorker does) or any other job runner.
// Usage: dotnet run --project samples/Worker [output-directory]
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RaiCore.Pdf;
using RaiCore.Pdf.Html;

var output = Directory.CreateDirectory(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "output").FullName;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton(Channel.CreateUnbounded<PdfJob>());
builder.Services.AddHostedService(services => new PdfWorker(
    services.GetRequiredService<Channel<PdfJob>>().Reader,
    output,
    services.GetRequiredService<IHostApplicationLifetime>(),
    services.GetRequiredService<ILogger<PdfWorker>>()));
using var host = builder.Build();

// In a real app, requests or messages add jobs; here a few are queued and the app stops when they are done.
var jobs = host.Services.GetRequiredService<Channel<PdfJob>>().Writer;
for (var i = 1; i <= 3; i++)
{
    await jobs.WriteAsync(new PdfJob($"receipt-{i}", $"""
        <html lang="en"><head><title>Receipt {i}</title>
        <style>@page {"{"} size: A5; margin: 15mm; {"}"} html {"{"} font-family: Arial, sans-serif; {"}"}</style></head>
        <body><h1>Receipt {i}</h1><p>Thank you for your purchase.</p><img src="https://example.com/logo.png" alt="Logo"></body></html>
        """));
}

jobs.Complete();
await host.RunAsync();

internal sealed record PdfJob(string Name, string Html);

internal sealed partial class PdfWorker(ChannelReader<PdfJob> jobs, string output, IHostApplicationLifetime lifetime, ILogger<PdfWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new HtmlConverterOptions
        {
            // What the converter could not render as a browser would, e.g. the image no loader fetches here.
            Diagnostics = d => LogDiagnostic(d.Severity == HtmlDiagnosticSeverity.Warning ? LogLevel.Warning : LogLevel.Information, d.Subject, d.Message),

            // Untrusted or unusual input cannot keep a worker busy forever.
            Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromSeconds(30) },
        };

        await foreach (var job in jobs.ReadAllAsync(stoppingToken))
        {
            var path = Path.Combine(output, job.Name + ".pdf");
            try
            {
                await using var file = File.Create(path);
                await HtmlToPdf.ConvertToStreamAsync(job.Html, file, options, stoppingToken);
                LogWritten(path);
            }
            catch (ResourceLimitExceededException ex)
            {
                LogRejected(job.Name, ex.Message);
            }
        }

        lifetime.StopApplication(); // The queue is complete (only in this sample).
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote {Path}")]
    private partial void LogWritten(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Name} exceeded a resource limit: {Reason}")]
    private partial void LogRejected(string name, string reason);

    [LoggerMessage(Message = "{Subject}: {Message}")]
    private partial void LogDiagnostic(LogLevel level, string subject, string message);
}
