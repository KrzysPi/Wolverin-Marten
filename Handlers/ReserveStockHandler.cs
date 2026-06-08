using Marten;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;

namespace Test_Wolverin_Marten.Handlers;

public sealed class ReserveStockHandler
{
    public static async Task Handle(ReserveStock command, IDocumentSession session, CancellationToken ct)
    {
        var snapshot = await session.LoadAsync<InventorySnapshot>(command.Sku, ct)
            ?? new InventorySnapshot
            {
                Id = command.Sku,
                Available = 100,
                Reserved = 0,
                LastUpdatedUtc = DateTime.UtcNow
            };

        if (snapshot.Available < command.Quantity)
            throw new InvalidOperationException($"Not enough stock for SKU {command.Sku}");

        snapshot.Available -= command.Quantity;
        snapshot.Reserved += command.Quantity;
        snapshot.LastUpdatedUtc = DateTime.UtcNow;

        session.Store(snapshot);
        await session.SaveChangesAsync(ct);
    }
}
