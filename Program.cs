using Marten;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;
using Test_Wolverin_Marten.Infrastructure;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Marten")
    ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Marten'.");

// ═══════════════════════════════════════════════════════════════════════════
// KROK 1 — MARTEN: document store na PostgreSQL
//
// AddMarten rejestruje w DI:
//   • IDocumentStore  — singleton, zarządza połączeniem i schematem
//   • IDocumentSession — scoped, jednostka pracy (unit of work)
//
// Każdy typ dokumentu dostaje własną tabelę JSON:  mt_doc_{typename}
//   Order → public.mt_doc_order
//
// Kolumny: id (PK), data (JSONB), mt_last_modified, mt_version, mt_dotnet_type
// ═══════════════════════════════════════════════════════════════════════════
builder.Services.AddMarten(opts =>
{
    opts.Connection(connectionString);

    if (builder.Environment.IsDevelopment())
    {
        // DLACZEGO TYLKO W DEV?
        //
        // AutoCreate.CreateOrUpdate = Marten tworzy/aktualizuje tabele przy
        // każdym starcie. Wygodne lokalnie, ale NIEBEZPIECZNE na produkcji:
        //   ✗ aplikacja modyfikuje schemat bez nadzoru DBA/DevOps
        //   ✗ możliwa niechciana zmiana produkcyjnej bazy przy wdrożeniu
        //
        // Na PRODUKCJI:
        //   dotnet marten-cli schema apply   ← w pipeline CI/CD przed deploymentem
        //   lub: opts.AutoCreateSchemaObjects = AutoCreate.None  + assert na starcie
        opts.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;
    }
})

// ═══════════════════════════════════════════════════════════════════════════
// KROK 2 — IntegrateWithWolverine(): transakcyjny outbox/inbox — GŁÓWNA MAGIA
//
// Bez IntegrateWithWolverine():
//   • Marten i Wolverine działają osobno
//   • zapis dokumentu i publikacja eventu = 2 operacje → możliwy split-brain
//
// Po IntegrateWithWolverine() powstają tabele Wolverine w tej samej bazie:
//   wolverine_incoming_envelopes  ← durable inbox  (wiadomości do przetworzenia)
//   wolverine_outgoing_envelopes  ← transactional outbox (wiadomości do wysłania)
//   wolverine_dead_letters        ← dead letters po wyczerpaniu retry
//
// GWARANCJA ATOMOWOŚCI (outbox pattern):
//   session.Store(order) + bus.PublishAsync(orderShipped)
//   = JEDNA transakcja PostgreSQL
//   → niemożliwe zapisanie Order BEZ eventu i odwrotnie
//   → crash procesu nie powoduje niespójności danych
// ═══════════════════════════════════════════════════════════════════════════
.IntegrateWithWolverine()

// UseLightweightSessions: IDocumentSession bez identity map (change tracking).
// Szybsze dla write-side — zawsze jawnie wołasz Store() + SaveChanges().
.UseLightweightSessions();

// Symulator awarii: 0 = zawsze przechodzi (produkcja).
// W testach nadpisywany przez ConfigureTestServices z N awariami.
builder.Services.AddSingleton(new FailureSimulator(0));

// ═══════════════════════════════════════════════════════════════════════════
// KROK 3 — WOLVERINE: message bus + background workers
// ═══════════════════════════════════════════════════════════════════════════
builder.Host.UseWolverine(opts =>
{
    // ── DURABLE LOCAL QUEUES ────────────────────────────────────────
    // BEZ durability (in-memory):
    //   PublishAsync → kolejka RAM → handler
    //   Crash między: wiadomość ZGUBIONA na zawsze
    //
    // Z UseDurableInbox():
    //   PublishAsync → INSERT wolverine_incoming_envelopes → handler
    //             → sukces: DELETE envelope
    //             → crash:  envelope zostaje w DB → Wolverine ponawia po restarcie
    opts.LocalQueueFor<CreateOrder>().UseDurableInbox();
    opts.LocalQueueFor<ConfirmOrder>().UseDurableInbox();
    opts.LocalQueueFor<OrderConfirmed>().UseDurableInbox();
    opts.LocalQueueFor<ReserveStock>().UseDurableInbox();

    // ── POLITYKA RETRY ──────────────────────────────────────────────
    // TransientException:
    //   próba 1 → fail → czekaj 100ms → próba 2 → fail → czekaj 500ms → próba 3
    //   próba 3 fail → MOVE do wolverine_dead_letters (nie ponawia)
    //
    // Każdy inny wyjątek (np. InvalidOperationException) → od razu dead letter.
    opts.OnException<TransientException>()
        .RetryWithCooldown(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

// ═══════════════════════════════════════════════════════════════════════════
// ENDPOINT: POST /orders
//
// PRZEPŁYW KROK PO KROKU:
//   1. ASP.NET deserializuje JSON → CreateOrder command
//   2. bus.SendAsync(command):
//        a) Wolverine generuje Envelope (id, payload, metadata)
//        b) INSERT do wolverine_incoming_envelopes w PostgreSQL
//        c) notify worker: wiadomość w kolejce RAM
//   3. HTTP odpowiada 202 Accepted NATYCHMIAST — nie czeka na handler!
//   4. Wolverine background worker dequeue'uje Envelope
//   5. CreateOrderHandler.Handle(command, session, ct):
//        a) session.Store(order)        — buforuje w unit of work
//        b) session.SaveChangesAsync()  — UPSERT mt_doc_order + DELETE envelope
//                                         obie operacje w JEDNEJ transakcji
//   6. Order pojawia się w mt_doc_order (eventual consistency, ~kilka ms lokalnie)
// ═══════════════════════════════════════════════════════════════════════════
app.MapPost("/orders", async (CreateOrder evnt, IMessageBus bus) =>
{
    await bus.SendAsync(evnt); // SendAsync = dokładnie 1 handler, czeka na jego wykonanie (czyli dla komend)
    return Results.Accepted($"/orders/{evnt.OrderId}");
});

// ═══════════════════════════════════════════════════════════════════════════
// ENDPOINT: POST /orders/{id}/confirm
//
// PRZEPŁYW Z RETRY:
//   1. SendAsync → INSERT wolverine_incoming_envelopes
//   2. ConfirmOrderHandler.Handle():
//
//      Scenariusz SUKCES:
//        → LoadAsync<Order> + zmiana Status → Confirmed
//        → Store + SaveChangesAsync — UPDATE + DELETE envelope (jedna transakcja)
//
//      Scenariusz RETRY (N awarii w FailureSimulator):
//        → próba 1: TransientException rzucony
//        → Wolverine: UPDATE envelope (attempts++, scheduled_at = now + 100ms)
//        → próba 2: fail → scheduled_at = now + 500ms
//        → próba 3: sukces → UPDATE mt_doc_order + DELETE envelope
//        → próba 3 fail → MOVE do wolverine_dead_letters
// ═══════════════════════════════════════════════════════════════════════════
app.MapPost("/orders/{orderId:guid}/confirm", async (Guid orderId, IMessageBus bus) =>
{
    await bus.SendAsync(new ConfirmOrder(orderId));
    return Results.Accepted();
});

app.MapGet("/inventory/{sku}", async (string sku, IQuerySession query, CancellationToken ct) =>
{
    var snapshot = await query.LoadAsync<InventorySnapshot>(sku, ct);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

app.Run();

public partial class Program;
