using System.Text.Json;
using System.Text.Json.Nodes;
using Y3L.Supply.Api.Models;
using Y3L.Supply.Api.Repos;

namespace Y3L.Supply.Api.Services;

/// <summary>
/// Elasticsearch synchronization — keeps the admin dashboard in line with Postgres.
/// Mirrors the reference: dual-write (A) + polling worker (B).
/// </summary>
public class SyncService(EsClient es, string index, ILogger<SyncService> log)
{
    public string Index { get; } = index;

    /// <summary>Flatten a Postgres order (+ snapshots) into the ES `orders` document.</summary>
    public static JsonObject BuildEsDoc(OrderOut o) => new()
    {
        ["order_id"] = o.Id,
        ["order_date"] = o.OrderDate,
        ["status"] = o.Status,
        ["total_amount"] = o.TotalAmount,
        ["updated_at"] = o.UpdatedAt,
        ["customer"] = JsonSerializer.SerializeToNode(o.Customer),
        ["items"] = JsonSerializer.SerializeToNode(o.Items),
    };

    public async Task EnsureIndexAsync()
    {
        try
        {
            if (await es.IndexExistsAsync(Index)) return;
            var raw = await File.ReadAllTextAsync("es-mapping.json");
            var mappings = JsonNode.Parse(raw)!["mappings"]!.ToJsonString();
            await es.CreateIndexAsync(Index, mappings);
            log.LogInformation("created ES index {Index}", Index);
        }
        catch (Exception ex)
        {
            // never fail boot because ES is napping
            log.LogWarning("ES index ensure failed at startup: {Msg}", ex.Message);
        }
    }

    public async Task IndexOrderAsync(OrderOut order) =>
        await es.IndexDocAsync(Index, order.Id.ToString(),
            BuildEsDoc(order).ToJsonString(), refreshWaitFor: true);

    public async Task IndexOrdersBulkAsync(List<OrderOut> orders)
    {
        if (orders.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        foreach (var o in orders)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                index = new { _index = Index, _id = o.Id.ToString() },
            }));
            sb.AppendLine(BuildEsDoc(o).ToJsonString());
        }
        await es.BulkAsync(sb.ToString());
    }

    public async Task<int> ReindexAllAsync(OrderRepo repo)
    {
        var all = await repo.ListAllOrdersAsync();
        await IndexOrdersBulkAsync(all);
        return all.Count;
    }

    /// <summary>Build the exact search body the reference implementation sends.</summary>
    public static JsonObject BuildSearchBody(string? query, List<string>? statuses,
        string? dateFrom, string? dateTo, double? minTotal, double? maxTotal)
    {
        var must = new JsonArray();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // customer.name is top-level, but items.title lives inside a NESTED
            // field: a plain multi_match on it silently matches nothing.
            var should = new JsonArray
            {
                new JsonObject
                {
                    ["multi_match"] = new JsonObject
                    {
                        ["query"] = query,
                        ["fields"] = new JsonArray("customer.name"),
                        ["boost"] = 2.0,
                    },
                },
                new JsonObject
                {
                    ["nested"] = new JsonObject
                    {
                        ["path"] = "items",
                        ["query"] = new JsonObject
                        {
                            ["multi_match"] = new JsonObject
                            {
                                ["query"] = query,
                                ["fields"] = new JsonArray("items.title"),
                            },
                        },
                        ["score_mode"] = "avg",
                    },
                },
            };
            must.Add(new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["should"] = should,
                    ["minimum_should_match"] = 1,
                },
            });
        }

        var filters = new JsonArray();
        if (statuses is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var s in statuses) arr.Add(s);
            filters.Add(new JsonObject { ["terms"] = new JsonObject { ["status"] = arr } });
        }
        if (dateFrom is not null || dateTo is not null)
        {
            var r = new JsonObject();
            if (dateFrom is not null) r["gte"] = dateFrom;
            if (dateTo is not null) r["lte"] = dateTo;
            filters.Add(new JsonObject { ["range"] = new JsonObject { ["order_date"] = r } });
        }
        if (minTotal is not null || maxTotal is not null)
        {
            var r = new JsonObject();
            if (minTotal is not null) r["gte"] = minTotal.Value;
            if (maxTotal is not null) r["lte"] = maxTotal.Value;
            filters.Add(new JsonObject { ["range"] = new JsonObject { ["total_amount"] = r } });
        }

        if (must.Count == 0)
            must.Add(new JsonObject { ["match_all"] = new JsonObject() });

        return new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["bool"] = new JsonObject { ["must"] = must, ["filter"] = filters },
            },
            ["aggs"] = new JsonObject
            {
                ["by_status"] = new JsonObject
                {
                    ["terms"] = new JsonObject { ["field"] = "status" },
                },
                ["revenue"] = new JsonObject
                {
                    ["sum"] = new JsonObject { ["field"] = "total_amount" },
                },
            },
            ["sort"] = new JsonArray(
                new JsonObject
                {
                    ["order_date"] = new JsonObject { ["order"] = "desc" },
                }),
        };
    }
}
