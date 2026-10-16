# 05 — One Transaction for Order Plus Items

Source files in this byte: `backend/src/Y3L.Supply.Api/Repos/OrderRepo.cs`

---

### Byte 5: One transaction for order plus items

**Builds on:** 3, 4

**In plain terms:** A *transaction* is a group of writes that succeed or fail together. `CreateOrderTxAsync` inserts the order row and all its item rows inside one transaction. It opens the transaction explicitly with `BeginTransactionAsync`. If any insert fails, the database rolls back everything. There is never an order without items, and never items without an order.

**The code:**
```csharp
// OrderRepo.cs — all-or-nothing write
await using var tx = await conn.BeginTransactionAsync();
await using var cmd = new NpgsqlCommand(
    "INSERT INTO orders (user_id, order_date, status, total_amount) " +
    "VALUES (@u, now(), 'PENDING', @t) RETURNING id", conn, tx);
...
var orderId = (int)(await cmd.ExecuteScalarAsync())!;
// ... insert each item with orderId ...
await tx.CommitAsync();
```

**What's happening:** The code opens a transaction on the connection. It inserts the order and reads back the new id with `RETURNING`. It then inserts each snapshot item with that id, on the same transaction. `CommitAsync` makes everything permanent. Any exception skips the commit and the transaction rolls back.

**Why it matters:** Money data must never be half-written. Without the transaction, a crash could leave an order with no items and a wrong total. The transaction makes the write atomic.

---
