using Marten;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;
using Test_Wolverin_Marten.Infrastructure;

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
    public async Task Handle(ConfirmOrder command, IDocumentSession session, CancellationToken ct)
    {
        // ── SYMULACJA AWARII ─────────────────────────────────────────────────
        // W testach FailureSimulator jest skonfigurowany z N awariami.
        // Rzucony TransientException jest łapany przez Wolverine pipeline,
        // NIE przez nas — nie ma tu try/catch celowo.
        // Wolverine decyduje o retry na podstawie polityki z UseWolverine(opts).
        simulator.ThrowIfShouldFail();

        // ── LOGIKA DOMENOWA ──────────────────────────────────────────────────
        var order = await session.LoadAsync<Order>(command.OrderId, ct)
            ?? throw new InvalidOperationException($"Order {command.OrderId} not found.");

        order.Status = OrderStatus.Confirmed;

        // session.Store() + SaveChangesAsync() = jeden UPDATE w PostgreSQL.
        // Dzięki IntegrateMarten() jest to część transakcji Wolverine outbox:
        // "wiadomość obsłużona" i zmiana dokumentu commitują się atomowo.
        session.Store(order);
        await session.SaveChangesAsync(ct);
    }
}
