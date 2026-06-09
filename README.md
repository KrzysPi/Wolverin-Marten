# Wolverine + Marten — POC

A proof-of-concept exploring the integration of **Wolverine** (message bus) and **Marten** (document store / event store on PostgreSQL) in an ASP.NET Core Web API.

The goal was to understand how the transactional outbox pattern works in practice and how Wolverine's durable inbox/outbox eliminates the split-brain problem between persistence and messaging.

---

## What's inside

- **ASP.NET Core Web API** — minimal API, two endpoints (`POST /orders`, `POST /orders/{id}/confirm`)
- **Marten** — document store backed by PostgreSQL (JSONB), lightweight sessions
- **Wolverine** — message bus with durable local queues (`UseDurableInbox`)
- **Transactional outbox** via `IntegrateWithWolverine()` — document save and message publish happen in a single PostgreSQL transaction
- **Retry policy** — configurable cooldown on `TransientException`, dead-letter after exhausted retries
- **Clean Architecture layout** — `Domain`, `Contracts`, `Handlers`, `Infrastructure`
- **Integration tests** — including failure simulation via `FailureSimulator`
- **Docker Compose** — spins up PostgreSQL locally

---

## Why Wolverine + Marten?

The classic problem: you save a document to the database and publish a message to a queue. If the process crashes between the two operations, you end up with inconsistent state — a saved order with no event, or an event with no order.

`IntegrateWithWolverine()` solves this with the outbox pattern:

```
session.Store(order) + bus.PublishAsync(orderCreated)
  → single PostgreSQL transaction
  → either both succeed or neither does
```

Wolverine maintains `wolverine_incoming_envelopes` and `wolverine_outgoing_envelopes` tables in the same database, guaranteeing atomicity without a distributed transaction.

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

API will be available at `https://localhost:5001`. OpenAPI docs at `/openapi/v1.json` (development only).

---

## Endpoints

```
POST /orders
Body: { "orderId": "guid", "productName": "string", "quantity": int }
→ 202 Accepted — command published to durable queue, processed asynchronously

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
├── Test_Wolverin_Marten.IntegrationTests/   # Integration tests
├── Program.cs          # Composition root — Marten + Wolverine setup
└── docker-compose.yml  # PostgreSQL
```

---

## Key concepts explored

| Concept | Implementation |
|---|---|
| Transactional outbox | `IntegrateWithWolverine()` |
| Durable inbox | `UseDurableInbox()` on local queues |
| Retry with cooldown | `OnException<TransientException>().RetryWithCooldown(...)` |
| Dead letters | Wolverine moves failed envelopes to `wolverine_dead_letters` |
| Schema management | `AutoCreate.CreateOrUpdate` in dev, CLI in production |
| Lightweight sessions | Write-side without identity map / change tracking |

---

## Notes

This is a learning project — not production-ready. Schema auto-creation is enabled only in development. For production use, schema migrations should run via `dotnet marten-cli schema apply` in the CI/CD pipeline.
