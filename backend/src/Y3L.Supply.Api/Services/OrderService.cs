using Y3L.Supply.Api.Models;
using Y3L.Supply.Api.Repos;

namespace Y3L.Supply.Api.Services;

/// <summary>
/// Order placement orchestration — the transactional write path.
/// 1. Validate the user exists (Postgres).
/// 2. Validate every line item against the LIVE MongoDB catalog (active products).
/// 3. Snapshot title + unit price at order time.
/// 4. Insert order + items inside ONE Postgres transaction (all-or-nothing).
/// 5. Fire the ES sync trigger (dual-write now; the polling worker also picks it up).
/// </summary>
public class OrderService(
    OrderRepo orders, ProductRepo products, SyncService sync,
    string syncStrategy, ILogger<OrderService> log)
{
    public async Task<OrderOut> PlaceOrderAsync(int userId, List<OrderItemIn>? items)
    {
        var user = await orders.GetUserAsync(userId);
        if (user is null) throw ApiException.BadRequest($"unknown user_id {userId}");
        if (items is null || items.Count == 0)
            throw ApiException.BadRequest("items must not be empty");
        foreach (var it in items)
            if (it.Quantity <= 0) throw ApiException.BadRequest("quantity must be > 0");

        var wanted = items.GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.First().Quantity);
        var found = await products.GetManyAsync(wanted.Keys.ToList());

        var snapshots = new List<OrderItemOut>();
        foreach (var (pid, qty) in wanted)
        {
            if (!found.TryGetValue(pid, out var p))
                throw ApiException.BadRequest($"unknown product_id {pid}");
            if (!p.Active)
                throw ApiException.BadRequest($"product '{p.Title}' is not active");
            snapshots.Add(new OrderItemOut(pid, p.Title, qty, p.Price));
        }

        var total = Math.Round(snapshots.Sum(s => s.UnitPrice * s.Quantity), 2);
        var orderId = await orders.CreateOrderTxAsync(userId, snapshots, total);
        var order = (await orders.GetOrderAsync(orderId))!;

        if (syncStrategy == "dual_write")
        {
            try { await sync.IndexOrderAsync(order); }
            catch (Exception ex)
            {
                // Write path already committed: never fail the order because ES
                // is napping. Logged; polling worker or manual reindex recovers.
                log.LogError("dual-write to ES failed for order {Id}: {Msg}", orderId, ex.Message);
            }
        }
        return order;
    }
}
