using Marten;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;

namespace Test_Wolverin_Marten.Handlers;

public sealed class CreateOrderHandler
{
    public static async Task Handle(CreateOrder command, IDocumentSession session, CancellationToken cancellationToken)
    {
        var order = new Order
        {
            Id = command.OrderId,
            CustomerName = command.CustomerName,
            TotalAmount = command.TotalAmount,
            Sku = command.Sku,
            Quantity = command.Quantity,
            CreatedAtUtc = DateTime.UtcNow
        };

        session.Store(order);
        await session.SaveChangesAsync(cancellationToken);
    }
}
