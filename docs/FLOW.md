# Wolverine + Marten Flow — Jak to działa od A do Z

## Przed wszystkim: Co to jest?

- **Wolverine**: message bus + asynchroniczny worker
- **Marten**: ORM do PostgreSQL, przechowuje dokumenty jako JSON
- **Razem**: gwarancja że wiadomości NIGDY się nie gubią, nawet jak aplikacja się wyloży

---

## Scenariusz 1: Tworzenie zamówienia (POST /orders)

### Co wysyłasz (HTTP request)
```
POST http://localhost:5234/orders
Content-Type: application/json

{
  "orderId": "550e8400-e29b-41d4-a716-446655440000",
  "customerName": "Jan Kowalski",
  "totalAmount": 199.99
}
```

### KROK 1: HTTP endpoint odbiera żądanie
```csharp
app.MapPost("/orders", async (CreateOrder command, IMessageBus bus, CancellationToken ct) =>
{
    await bus.PublishAsync(command);
    return Results.Accepted($"/orders/{command.OrderId}");
});
```

**Co się dzieje:**
- ASP.NET deserializuje JSON do obiektu `CreateOrder(orderId, customerName, totalAmount)`
- Wolverine wstrzykuje `IMessageBus` automatycznie

### KROK 2: PublishAsync — zapisanie wiadomości do bazy
```csharp
await bus.PublishAsync(command);
```

**Tu dzieje się magia:**
- Wolverine **nie wykonuje handlera od razu**
- Zamiast tego tworzy "Envelope" (opakowanie):
  - ID wiadomości (UUID)
  - Typ: `Test_Wolverin_Marten.Contracts.CreateOrder`
  - Treść: wiadomość jako bajty

**Zapis do PostgreSQL:**
```sql
INSERT INTO wolverine_incoming_envelopes (id, message_type, body, status, owner_id)
VALUES (
  'aa-bb-cc-dd-ee',
  'Test_Wolverin_Marten.Contracts.CreateOrder',
  b'\x00\x01\x02...',  -- wiadomość w binarnym formacie
  'unprocessed',
  1
);
```

**HTTP natychmiast odpowiada:**
```
202 Accepted
Location: /orders/550e8400-e29b-41d4-a716-446655440000
```
Nie czeka na handler! Wiadomość jest bezpieczna w bazie.

### KROK 3: Wolverine background worker obudził się
```
[Wolverine background worker thread]
  SELECT id, message_type, body FROM wolverine_incoming_envelopes 
  WHERE status = 'unprocessed' 
  LIMIT 1;
→ znalazł naszą wiadomość
```

**Wolverine:**
1. Deserializuje treść wiadomości → `CreateOrder` obiekt
2. Szuka handlera po sygnaturze (konwencja):
   - typ parametru = `CreateOrder`
   - metodę zwaną `Handle`
   - klasa = `CreateOrderHandler` (szuka w assembly)

### KROK 4: CreateOrderHandler wykonuje się
```csharp
public static async Task Handle(CreateOrder command, IDocumentSession session, CancellationToken cancellationToken)
{
    var order = new Order
    {
        Id = command.OrderId,
        CustomerName = command.CustomerName,
        TotalAmount = command.TotalAmount,
        CreatedAtUtc = DateTime.UtcNow
    };

    session.Store(order);
    await session.SaveChangesAsync(cancellationToken);
}
```

**Co się dzieje w SaveChangesAsync:**

Marten działa w trybie Unit of Work:
1. `session.Store(order)` → bufferuje Order w pamięci
2. `SaveChangesAsync()` → wysyła WSZYSTKO do bazy w jednej transakcji

**SQL execute'a się w PostgreSQL:**
```sql
BEGIN TRANSACTION;
  INSERT INTO mt_doc_order (id, data) VALUES (
    '550e8400-e29b-41d4-a716-446655440000',
    '{"Id":"550e8400...","CustomerName":"Jan Kowalski","TotalAmount":199.99,"Status":"Pending","CreatedAtUtc":"2026-05-28T..."}'
  );
  
  DELETE FROM wolverine_incoming_envelopes WHERE id = 'aa-bb-cc-dd-ee';
COMMIT;
```

**Czyli: w JEDNEJ transakcji:**
- INSERT Order do `mt_doc_order` (tabela dokumentów)
- DELETE wiadomość z `wolverine_incoming_envelopes` (potwierdzenie przetworzenia)

Jeśli crash dokładnie w połowie = db rollback = wszystko się nie stało.
Jeśli sukces = Order zapisany, envelope usunięty = wiadomość oprocesowana.

### KROK 5: Order pojawia się w bazie
```sql
SELECT * FROM mt_doc_order WHERE id = '550e8400-...';
```
Widzisz:
- `id`: 550e8400-e29b-41d4-a716-446655440000
- `data`: `{"Id":"550e8400...","CustomerName":"Jan Kowalski",...}`

**Eventual consistency:**
- HTTP zwrócił 202 za ~10ms
- Handler wykonał się za ~50ms
- Dane są w bazie za ~60ms
- Gotowe do odczytu!

---

## Scenariusz 2: Potwierdzenie zamówienia Z RETRY (POST /orders/{id}/confirm)

### Co wysyłasz
```
POST http://localhost:5234/orders/550e8400-e29b-41d4-a716-446655440000/confirm
```

### KROK 1: Endpoint publikuje ConfirmOrder
```csharp
app.MapPost("/orders/{orderId:guid}/confirm", async (Guid orderId, IMessageBus bus) =>
{
    await bus.PublishAsync(new ConfirmOrder(orderId));
    return Results.Accepted();
});
```

**Wiadomość: `ConfirmOrder(orderId)`**
Zapisuje się do `wolverine_incoming_envelopes` tak samo jak poprzednio.

### KROK 2: Worker próbuje przetworzyć
Szuka handlera dla `ConfirmOrder` → znalazł `ConfirmOrderHandler`:

```csharp
public sealed class ConfirmOrderHandler(FailureSimulator simulator)
{
    public async Task Handle(ConfirmOrder command, IDocumentSession session, CancellationToken ct)
    {
        simulator.ThrowIfShouldFail();  // ← RZUCA WYJĄTEK
        
        var order = await session.LoadAsync<Order>(command.OrderId, ct);
        order.Status = OrderStatus.Confirmed;
        session.Store(order);
        await session.SaveChangesAsync(ct);
    }
}
```

### KROK 3: Symulator rzuca TransientException (próba 1)
```
TransientException('Simulated transient failure - Wolverine will retry automatically.')
```

**Wolverine łapie wyjątek (NIE catch w handlerze, ale w pipeline Wolverine):**

```csharp
opts.OnException<TransientException>()
    .RetryWithCooldown(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500));
```

**CO WOLVERINE ROBI:**
1. Loguje błąd
2. UPDATE envelope w bazie:
   ```sql
   UPDATE wolverine_incoming_envelopes 
   SET attempts = 1, 
       execution_time = now() + 100ms
   WHERE id = '...';
   ```
3. Czeka 100ms

### KROK 4: Próba 2 (po 100ms)
Worker znowu wykonuje handler:
- `simulator.ThrowIfShouldFail()` → FAIL (bo simulator ma planowane 2 awarie)
- Wolverine: UPDATE attempts = 2, scheduled_at = now() + 500ms
- Czeka 500ms

### KROK 5: Próba 3 (po 500ms)
- `simulator.ThrowIfShouldFail()` → SUKCES (wyczerpane awarie)
- Reszta handlera wykonuje się normalnie
- Order zmienia status na `Confirmed`
- Transaction commit
- DELETE envelope

**Efekt końcowy:**
```sql
SELECT * FROM mt_doc_order WHERE id = '550e8400-...';
```
Widzisz: `Status = "Confirmed"`

---

## Diagram całego systemu

```
┌──────────────────────────────────────────────────────────────────────┐
│                          Your Application                            │
│                                                                      │
│  HTTP POST /orders                                                   │
│      ↓                                                                │
│  app.MapPost("/orders", async (CreateOrder cmd, IMessageBus bus) → {│
│      await bus.PublishAsync(cmd);                                    │
│      return 202 Accepted;  ← wróć ZARAZ bez czekania                 │
│  })                                                                   │
│      ↓                                                                │
│      PublishAsync:                                                    │
│      1. Tworzy Envelope                                               │
│      2. INSERT do wolverine_incoming_envelopes    ← DB!               │
│      3. Notifies background worker                                    │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
                                ↓
                    [Wolverine Background Worker]
                    
                    1. Polling: SELECT * FROM wolverine_incoming_envelopes
                    2. Znalazł wiadomość
                    3. Deserialize → CreateOrder(...)
                    4. Szuka handler po konwencji → CreateOrderHandler
                    5. Wstrzykuje: IDocumentSession
                    6. Invokes: handler.Handle(cmd, session, ct)
                    
                                ↓
                    ┌─────────────────────────────────┐
                    │  CreateOrderHandler.Handle()    │
                    │                                 │
                    │  session.Store(order)           │
                    │  await session.SaveChangesAsync │
                    └─────────────────────────────────┘
                                ↓
                        [Marten Unit of Work]
                        
                    ┌─────────────────────────┐
                    │  PostgreSQL Transaction  │
                    │  BEGIN;                 │
                    │  INSERT mt_doc_order    │
                    │  DELETE envelopes       │
                    │  COMMIT;                │
                    └─────────────────────────┘
                                ↓
                        Order w bazie! ✓
```

---

## Gwarancje (Why it never loses messages)

### Bez Wolverine (zwykły async code):
```csharp
await handler.ProcessAsync(message);  // ← crash tutaj = lost message forever
```

### Z Wolverine + durable inbox:
```
PublishAsync → INSERT envelope
               ↓
            Handler execute
               ↓
            Success → DELETE envelope
               ↓
            Crash? Envelope zostaje w DB
               ↓
            App restart → Wolverine znalazł orphaned envelope
               ↓
            Ponowne przetworzenie (retry)
```

---

## Czemu AutoCreate tylko w Development?

### Development:
```csharp
if (builder.Environment.IsDevelopment())
    opts.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;
```
- Marten **automatycznie** tworzy tabele
- Wygodne do testowania
- Szybkie
- Zero migracji

### Production:
```csharp
// opts.AutoCreateSchemaObjects = AutoCreate.None;
// W pipeline CI/CD:
// dotnet marten-cli schema apply --connection "Production-DB"
```
- DBA/DevOps **kontroluje** schema changes
- Migracje w Git
- Audit trail
- Safe rollback

---

## Co to jest outbox pattern?

Gwarancja że "zmiana + publikacja" = atomowo:

```
❌ NIEBEZPIECZNE (bez outbox):
    Save Order to DB
    ↓ crash
    Publish Order event  ← nigdy się nie wykonało

✓ BEZPIECZNE (z outbox/IntegrateWithWolverine):
    BEGIN TRANSACTION
      Save Order to DB
      INSERT event to outgoing_envelopes
    COMMIT
    ↓ crash = rollback ALL
    
    BEZ CRASH:
      Delete event from outgoing_envelopes (przetworzony)
```

---

## Podsumowanie: 3 kluczowe komponenty

| Komponent | Rola | Gwarancja |
|-----------|------|-----------|
| **Wolverine** | Message bus + durability | Wiadomość nie zginie |
| **Marten** | Document store (PostgreSQL JSON) | Dane trwałe w bazie |
| **IntegrateWithWolverine()** | Połączenie atomowe | Wiadomość + Dane = jedna transakcja |

---

## Przykład: Czym się różni stary flow od nowego?

### STARY (bez Wolverine):
```
HTTP POST /orders
  ↓
Handler execute (synchroniczny)
  ↓
Save to DB
  ↓
HTTP 200 OK
  ↓
CPU zajęty przez 100ms — blokuje inne requesty
```

### NOWY (z Wolverine):
```
HTTP POST /orders
  ↓
Save wiadomość do DB (10ms)
  ↓
HTTP 202 Accepted ZARAZ
  ↓
Handler execute w tle (w background worker)
  ↓
CPU free dla następnych requestów
  ↓
Nawet jak app crash → wiadomość w DB → retry po restart
```
