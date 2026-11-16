namespace Snippets;

public sealed record Invoice(string Number, IReadOnlyList<InvoiceLine> Lines);

public sealed record InvoiceLine(string Description, decimal Amount);

internal static class Data
{
    public static Invoice Invoice { get; } = new("2026-0042", [new("Consulting", 7125m), new("Travel", 240m)]);
}
