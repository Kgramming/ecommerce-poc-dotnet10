using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using Y3L.Supply.Api.Models;
using Y3L.Supply.Api.Services;

namespace Y3L.Supply.Api.Controllers;

/// <summary>Admin search route — Elasticsearch ONLY.</summary>
[ApiController]
[Route("api/search")]
public class SearchController(EsClient es, SyncService sync) : ControllerBase
{
    [HttpPost("orders")]
    public async Task<SearchResponse> Search([FromBody] SearchRequest req)
    {
        var body = SyncService.BuildSearchBody(req.Query, req.Statuses,
            req.DateFrom, req.DateTo, req.MinTotal, req.MaxTotal);
        var page = req.PageOrDefault();
        var pageSize = req.PageSizeOrDefault();
        var raw = await es.SearchAsync(sync.Index, body.ToJsonString(),
            (page - 1) * pageSize, pageSize);
        var resp = JsonNode.Parse(raw)!;

        var total = resp["hits"]?["total"]?["value"]?.GetValue<int>() ?? 0;
        var orders = new List<SearchOrderHit>();
        foreach (var h in resp["hits"]?["hits"]?.AsArray() ?? new JsonArray())
        {
            var s = h!["_source"]!;
            var cust = s["customer"]!;
            var items = new List<OrderItemOut>();
            foreach (var it in s["items"]?.AsArray() ?? new JsonArray())
            {
                items.Add(new OrderItemOut(
                    it!["product_id"]?.GetValue<string>() ?? "",
                    it["title"]?.GetValue<string>() ?? "",
                    it["quantity"]?.GetValue<int>() ?? 0,
                    it["unit_price"]?.GetValue<double>() ?? 0.0));
            }
            orders.Add(new SearchOrderHit(
                s["order_id"]?.GetValue<int>() ?? 0,
                s["order_date"]?.GetValue<string>() ?? "",
                s["status"]?.GetValue<string>() ?? "",
                s["total_amount"]?.GetValue<double>() ?? 0.0,
                new CustomerOut(
                    cust["id"]?.GetValue<int>() ?? 0,
                    cust["name"]?.GetValue<string>() ?? "",
                    cust["email"]?.GetValue<string>() ?? ""),
                items));
        }

        var byStatus = new Dictionary<string, int>();
        foreach (var b in resp["aggregations"]?["by_status"]?["buckets"]?.AsArray() ?? new JsonArray())
            byStatus[b!["key"]!.GetValue<string>()] = b["doc_count"]?.GetValue<int>() ?? 0;
        var revenue = resp["aggregations"]?["revenue"]?["value"]?.GetValue<double>() ?? 0.0;
        return new SearchResponse(total, orders, new SearchKpis(revenue, byStatus));
    }
}

/// <summary>Health probe.</summary>
[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public object Get() => new { status = "ok" };
}
