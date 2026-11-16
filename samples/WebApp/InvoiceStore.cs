namespace WebApp;

/// <summary>Stands in for your database: the components inject it.</summary>
public sealed class InvoiceStore
{
    private readonly Invoice[] _invoices = [.. Enumerable.Range(1, 120).Select(id => new Invoice(
        id,
        $"Customer {id % 7 + 1}",
        new DateOnly(2026, 1, 1).AddDays(id * 3),
        [new InvoiceLine("Consulting", 7.5m, 950m), new InvoiceLine("Travel", 1, 120m + id)]))];

    public Invoice? Find(int id) => _invoices.FirstOrDefault(i => i.Id == id);

    public IReadOnlyList<Invoice> All() => _invoices;
}

public sealed record Invoice(int Id, string Customer, DateOnly Date, IReadOnlyList<InvoiceLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Quantity * l.Price);
}

public sealed record InvoiceLine(string Description, decimal Quantity, decimal Price);
