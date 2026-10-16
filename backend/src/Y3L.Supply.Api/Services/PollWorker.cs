using Y3L.Supply.Api.Repos;

namespace Y3L.Supply.Api.Services;

/// <summary>
/// Strategy B: periodically sync Postgres -&gt; ES. Only runs when
/// SYNC_STRATEGY=polling (default is dual_write, like the reference).
/// </summary>
public class PollWorker(
    OrderRepo orders, SyncService sync, string strategy,
    ILogger<PollWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (strategy != "polling") return;
        log.LogInformation("polling sync worker started (every 15s)");

        var watermark = DateTimeOffset.UtcNow.AddSeconds(-60);
        try // full reindex on worker start so nothing is ever missed
        {
            var n = await sync.ReindexAllAsync(orders);
            log.LogInformation("poll worker initial reindex: {N} orders", n);
        }
        catch (Exception ex)
        {
            log.LogWarning("poll worker initial reindex failed: {Msg}", ex.Message);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // overlap: idempotent, so nothing is missed
                var ids = await orders.ListOrdersSinceAsync(watermark.AddSeconds(-30));
                if (ids.Count > 0)
                {
                    var fresh = new List<Models.OrderOut>();
                    foreach (var id in ids)
                    {
                        var o = await orders.GetOrderAsync(id);
                        if (o is not null) fresh.Add(o);
                    }
                    await sync.IndexOrdersBulkAsync(fresh);
                    log.LogInformation("poll worker synced {N} orders", fresh.Count);
                }
                watermark = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                log.LogWarning("poll worker iteration failed: {Msg}", ex.Message);
            }
        }
    }
}
