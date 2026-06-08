using Marten;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;
using Test_Wolverin_Marten.Infrastructure;
using Wolverine;
using Wolverine.Marten;

namespace Test_Wolverin_Marten.Handlers;

/// <summary>
/// Handler komendy ConfirmOrder.
///
/// KLUCZOWE: konstruktor pobiera FailureSimulator przez DI.
/// Wolverine automatycznie wstrzykuje zależności do handlerów — nie musisz ręcznie
/// tworzyć instancji ani rejestrować handlera w DI jako usługę.
/// </summary>
public sealed class ConfirmOrderHandler(FailureSimulator simulator)
{
    /// <summary>
    /// Wolverine odnajduje tę metodę po konwencji nazwy "Handle" i typie parametru
    /// ConfirmOrder — zero rejestracji, zero refleksji w runtime.
    /// IDocumentSession jest wstrzykiwany przez pipeline Wolverine+Marten.
    /// </summary>
    public async Task Handle(ConfirmOrder command, IMartenOutbox outbox, CancellationToken ct)
    {
        // ── SYMULACJA AWARII ─────────────────────────────────────────────────
        // W testach FailureSimulator jest skonfigurowany z N awariami.
        // Rzucony TransientException jest łapany przez Wolverine pipeline,
        // NIE przez nas — nie ma tu try/catch celowo.
        // Wolverine decyduje o retry na podstawie polityki z UseWolverine(opts).
        simulator.ThrowIfShouldFail();

        // ── LOGIKA DOMENOWA ──────────────────────────────────────────────────
        var session = outbox.Session
            ?? throw new InvalidOperationException("Marten outbox session is not available.");

        var order = await session.LoadAsync<Order>(command.OrderId, ct);
        if (order is null)
            throw new InvalidOperationException($"Order {command.OrderId} not found.");

        order.Status = OrderStatus.Confirmed;

        // session.Store() + PublishAsync() + SaveChangesAsync() = jedna transakcja.
        // Order update oraz wiadomość wychodząca do outbox commitują się atomowo.
        session.Store(order);
        await outbox.PublishAsync(new OrderConfirmed(
            order.Id,
            order.CustomerName,
            order.TotalAmount,
            order.Sku,
            order.Quantity));

        await session.SaveChangesAsync(ct);
    }

    public static async Task Handle(OrderConfirmed @event, IMessageBus bus)
    {
        await bus.PublishAsync(new ReserveStock(@event.OrderId, @event.Sku, @event.Quantity));
    }
}
