using Npgsql;
using Y3L.Supply.Api.Models;

namespace Y3L.Supply.Api.Repos;

/// <summary>
/// PostgreSQL order repository. Orders are the source of truth: every write
/// goes through a real DB transaction. Reads here hit Postgres ONLY.
/// </summary>
public class OrderRepo(string connStr)
{
    private const string OrderSelect = """
        SELECT o.id, o.user_id, o.order_date, o.status, o.total_amount,
               o.updated_at, u.name, u.email,
               i.product_id, i.title, i.quantity, i.unit_price
        FROM orders o
        JOIN users u ON u.id = o.user_id
        LEFT JOIN order_items i ON i.order_id = o.id
        """;

    private NpgsqlConnection Open() => new(connStr);

    private static string Iso(object v) => v switch
    {
        DateTime dt => new DateTimeOffset(
            DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz"),
        DateTimeOffset dto => dto.ToOffset(TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz"),
        _ => v?.ToString() ?? "",
    };

    /// <summary>Groups the flat join rows into orders, mirroring the reference _group().</summary>
    internal static List<OrderOut> Group(List<Dictionary<string, object?>> rows)
    {
        var accs = new Dictionary<int, (OrderOut order, List<OrderItemOut> items)>();
        foreach (var r in rows)
        {
            var oid = Convert.ToInt32(r["id"]);
            if (!accs.TryGetValue(oid, out var acc))
            {
                var userId = Convert.ToInt32(r["user_id"]);
                var order = new OrderOut(
                    oid, userId,
                    new CustomerOut(userId, (string)r["name"]!, (string)r["email"]!),
                    Iso(r["order_date"]!), (string)r["status"]!,
                    Convert.ToDouble(r["total_amount"]), Iso(r["updated_at"]!),
                    new List<OrderItemOut>());
                acc = (order, new List<OrderItemOut>());
                accs[oid] = acc;
            }
            if (r["product_id"] is not null && r["product_id"] is not DBNull)
            {
                acc.items.Add(new OrderItemOut(
                    (string)r["product_id"]!, (string)r["title"]!,
                    Convert.ToInt32(r["quantity"]), Convert.ToDouble(r["unit_price"])));
            }
        }
        return accs.Values.Select(a => a.order with { Items = a.items }).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAllAsync(NpgsqlCommand cmd)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<CustomerOut?> GetUserAsync(int userId)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, email FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new CustomerOut(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>Insert one order + N items atomically. All-or-nothing. Returns the order id.</summary>
    public async Task<int> CreateOrderTxAsync(int userId, List<OrderItemOut> snapshots, double total)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orders (user_id, order_date, status, total_amount) " +
            "VALUES (@u, now(), 'PENDING', @t) RETURNING id", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("t", total);
        var orderId = (int)(await cmd.ExecuteScalarAsync())!;

        foreach (var s in snapshots)
        {
            await using var icmd = new NpgsqlCommand(
                "INSERT INTO order_items (order_id, product_id, title, quantity, unit_price) " +
                "VALUES (@o, @p, @t, @q, @u)", conn, tx);
            icmd.Parameters.AddWithValue("o", orderId);
            icmd.Parameters.AddWithValue("p", s.ProductId);
            icmd.Parameters.AddWithValue("t", s.Title);
            icmd.Parameters.AddWithValue("q", s.Quantity);
            icmd.Parameters.AddWithValue("u", s.UnitPrice);
            await icmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return orderId;
    }

    public async Task<OrderOut?> GetOrderAsync(int orderId)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            OrderSelect + " WHERE o.id = @id ORDER BY i.id", conn);
        cmd.Parameters.AddWithValue("id", orderId);
        var grouped = Group(await ReadAllAsync(cmd));
        return grouped.Count == 0 ? null : grouped[0];
    }

    public async Task<OrderOut?> UpdateStatusAsync(int orderId, string status)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE orders SET status = @s, updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", orderId);
        if (await cmd.ExecuteNonQueryAsync() == 0) return null;
        return await GetOrderAsync(orderId);
    }

    public async Task<List<int>> ListOrdersSinceAsync(DateTimeOffset since)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM orders WHERE updated_at > @s ORDER BY updated_at", conn);
        cmd.Parameters.AddWithValue("s", since);
        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<List<OrderOut>> ListAllOrdersAsync()
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(OrderSelect + " ORDER BY o.id, i.id", conn);
        return Group(await ReadAllAsync(cmd));
    }
}
