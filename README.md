# Wolverine + Marten — POC

A proof-of-concept exploring **message durability** in distributed systems using **Wolverine** (message bus) and **Marten** (document store on PostgreSQL) in an ASP.NET Core Web API.

The core question: *what happens to your messages when the process crashes?*

---

## The problem this solves

In a typical setup, saving data and publishing a message are two separate operations:

```
1. database.Save(order)   ✓
2. queue.Publish(event)   ✗  ← process crashes here
```

The order is saved. The event is never sent. Downstream services never know the order exists. **Data is consistent, but the system is broken.**

The naive fix — publish first, then save — just moves the problem:

```
1. queue.Publish(event)   ✓  ← event sent
2. database.Save(order)   ✗  ← process crashes here
```

Now the event is out there but the order doesn't exist yet. Consumers will try to process something that isn't in the database.

---

## How Wolverine + Marten solve it

`IntegrateWithWolverine()` implements the **transactional outbox pattern**: the message is written to the *same PostgreSQL database* as your documents, inside the *same transaction*:

```
BEGIN TRANSACTION
  INSERT mt_doc_order          ← your document
  INSERT wolverine_outgoing_envelopes  ← your message
COMMIT
```

Either both land in the database or neither does. The message is then picked up by Wolverine's background worker and delivered to the actual destination. If the process crashes after the commit but before delivery — the envelope is still in the database. On restart, Wolverine picks it up and delivers it.

**The message is never lost.**

On the consumer side, `UseDurableInbox()` provides the same guarantee in reverse — incoming messages are persisted before processing, so a crash mid-handler doesn't silently drop work.

---

## What's inside

- **ASP.NET Core Web API** — two endpoints (`POST /orders`, `POST /orders/{id}/confirm`)
- **Marten** — document store backed by PostgreSQL (JSONB), lightweight sessions
- **Wolverine** — message bus with durable local queues
- **Retry policy** — cooldown on `TransientException`, dead-letter queue after exhausted retries
- **Failure simulation** — `FailureSimulator` injects controlled failures to verify retry behavior
- **Integration tests** — covering retry and recovery scenarios
- **Clean Architecture layout** — `Domain`, `Contracts`, `Handlers`, `Infrastructure`
- **Docker Compose** — PostgreSQL out of the box

---

## Getting started

**Prerequisites:** .NET 8+, Docker

```bash
git clone https://github.com/KrzysPi/Wolverin-Marten.git
cd Wolverin-Marten

# Start PostgreSQL
docker-compose up -d

# Run the API
dotnet run
```

API available at `https://localhost:5001`. OpenAPI at `/openapi/v1.json` (dev only).

---

## Endpoints

```
POST /orders
Body: { "orderId": "guid", "productName": "string", "quantity": int }
→ 202 Accepted — command written to durable inbox, processed asynchronously

POST /orders/{orderId}/confirm
→ 202 Accepted — ConfirmOrder command published, status updated to Confirmed
```

---

## Project structure

```
├── Contracts/          # Commands and DTOs (CreateOrder, ConfirmOrder)
├── Domain/             # Domain models (Order, OrderStatus)
├── Handlers/           # Wolverine message handlers
├── Infrastructure/     # FailureSimulator, supporting services
├── Test_Wolverin_Marten.IntegrationTests/
├── Program.cs          # Composition root — Marten + Wolverine setup
└── docker-compose.yml
```

---

## Beyond durability — triggering side effects across services

The outbox pattern isn't only about not losing messages. It's also the right way to trigger side effects in other services or infrastructure components **as a consequence of a state change** — without coupling them to the handler.

A real-world example: when an order is confirmed, denormalized data in other parts of the system may become stale. The handler doesn't need to know about Redis, search indexes, or notification services. It just publishes an event:

```csharp
// ConfirmOrderHandler — knows nothing about Redis or other services
session.Store(order);
await bus.PublishAsync(new OrderConfirmed(order.OrderId));
// single transaction — order updated + event envelope persisted
```

Separate handlers react to that event independently:

```csharp
// Runs in the same service or a different one
public static async Task Handle(OrderConfirmed evt, IRedisCache cache)
{
    await cache.InvalidateAsync($"order:{evt.OrderId}");
    // or: rebuild the cached projection with fresh data
}
```

This keeps handlers focused and decoupled. Adding a new side effect (e.g. updating a search index, sending a notification) means adding a new handler — not touching the existing one. And because the event goes through the outbox, the side effect is guaranteed to eventually happen even if the consumer is temporarily down.

---

## Durability guarantees at a glance

| Failure scenario | Without outbox | With Wolverine + Marten |
|---|---|---|
| Crash after DB save, before publish | Message lost | Message delivered on restart |
| Crash after publish, before DB save | Phantom event | Transaction rolled back, no event |
| Handler crash mid-processing | Message dropped | Retried from inbox on restart |
| Transient error (e.g. DB timeout) | Depends on caller | Auto-retry with cooldown |
| Repeated failure | — | Dead-letter after N attempts |

---

## Notes

POC — not production-ready. Schema auto-creation (`AutoCreate.CreateOrUpdate`) is enabled in development only. For production, run `dotnet marten-cli schema apply` in the CI/CD pipeline before deployment.
