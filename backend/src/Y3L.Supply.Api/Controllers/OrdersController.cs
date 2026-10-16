using Microsoft.AspNetCore.Mvc;
using Y3L.Supply.Api.Models;
using Y3L.Supply.Api.Repos;
using Y3L.Supply.Api.Services;

namespace Y3L.Supply.Api.Controllers;

/// <summary>Order routes — Postgres is the source of truth.</summary>
[ApiController]
[Route("api/orders")]
public class OrdersController(
    OrderService orderService, OrderRepo repo, SyncService sync,
    ILogger<OrdersController> log) : ControllerBase
{
    private static readonly HashSet<string> ValidStatuses =
        new() { "PENDING", "PROCESSING", "SHIPPED" };

    [HttpPost]
    public async Task<OrderOut> Create([FromBody] OrderPlace payload)
    {
        var o = await orderService.PlaceOrderAsync(payload.UserId, payload.Items);
        Response.StatusCode = StatusCodes.Status201Created;
        return o;
    }

    [HttpGet("{oid:int}")]
    public async Task<OrderOut> Get(int oid)
    {
        var o = await repo.GetOrderAsync(oid);
        if (o is null) throw ApiException.NotFound("order not found");
        return o;
    }

    [HttpPatch("{oid:int}/status")]
    public async Task<OrderOut> PatchStatus(int oid, [FromBody] StatusUpdate payload)
    {
        if (payload.Status is null || !ValidStatuses.Contains(payload.Status))
            throw ApiException.Unprocessable(
                "status must be one of [" + string.Join(", ", ValidStatuses.Order()) + "]");
        var o = await repo.UpdateStatusAsync(oid, payload.Status);
        if (o is null) throw ApiException.NotFound("order not found");
        // Keep the dashboard accurate: refresh the ES doc. If ES is down the
        // write path still succeeded — the polling worker recovers it.
        try { await sync.IndexOrderAsync(o); }
        catch (Exception ex) { log.LogWarning("ES status-sync failed for order {Id}: {Msg}", oid, ex.Message); }
        return o;
    }
}
