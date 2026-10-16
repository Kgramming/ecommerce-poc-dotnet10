using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Seed the whole data stack (idempotent: clears and reloads everything).
/// Order: schema -> users -> products (MongoDB) -> orders -> ES reindex
///        -> rename fixture (Wireless Mouse -> Wireless Mouse Pro, MongoDB only).
///
/// Run from the repo root:
///   dotnet run --project backend/src/Y3L.Supply.Seed
/// </summary>
static class Seed
{
    static string Env(string key, string def) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : def;

    static readonly string PgConn =
        $"Host={Env("PGHOST", "localhost")};Port={Env("PGPORT", "5435")};" +
        $"Database={Env("PGDATABASE", "ecommerce")};Username={Env("PGUSER", "ecommerce")};" +
        $"Password={Env("PGPASSWORD", "ecommerce")}";
    static readonly string MongoUri =
        $"mongodb://{Env("MONGOHOST", "localhost")}:{Env("MONGOPORT", "27017")}";
    static readonly string MongoDb = Env("MONGODB", "ecommerce");
    static readonly string EsUrl = $"http://{Env("ESHOST", "localhost")}:{Env("ESPORT", "9200")}";
    static readonly string EsIndex = Env("ESINDEX", "orders");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static async Task<int> Main()
    {
        if (!Directory.Exists("seed"))
        {
            Console.Error.WriteLine("run from the repo root (seed/ not found)");
            return 1;
        }

        Console.WriteLine("1/6  PostgreSQL schema ...");
        await RunSqlAsync("seed/schema.sql");
        Console.WriteLine("     clearing any previous seed data ...");
        await using (var conn = new NpgsqlConnection(PgConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "TRUNCATE TABLE order_items, orders, users RESTART IDENTITY CASCADE;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        Console.WriteLine("2/6  users ...");
        await RunSqlAsync("seed/users.sql");

        Console.WriteLine("3/6  products -> MongoDB ...");
        var productsJson = await File.ReadAllTextAsync("seed/products.json");
        var docs = new List<BsonDocument>();
        foreach (var el in JsonNode.Parse(productsJson)!.AsArray())
        {
            var doc = BsonDocument.Parse(el!.ToJsonString());
            doc["_id"] = ObjectId.Parse(doc["_id"].AsString);
            docs.Add(doc);
        }
        using (var mongo = new MongoClient(MongoUri))
        {
            var col = mongo.GetDatabase(MongoDb).GetCollection<BsonDocument>("products");
            await col.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await col.InsertManyAsync(docs);
            Console.WriteLine($"     inserted {docs.Count} products");

            Console.WriteLine("4/6  orders -> PostgreSQL ...");
            await RunSqlAsync("seed/orders.sql");

            Console.WriteLine("5/6  Elasticsearch reindex ...");
            var n = await ReindexEsAsync();
            Console.WriteLine($"     indexed {n} orders");

            Console.WriteLine("6/6  rename fixture: 'Wireless Mouse' -> 'Wireless Mouse Pro' ...");
            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var res = await col.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("sku", "WM-001"),
                Builders<BsonDocument>.Update.Combine(
                    Builders<BsonDocument>.Update.Set("title", "Wireless Mouse Pro"),
                    Builders<BsonDocument>.Update.Set("price", 4999),
                    Builders<BsonDocument>.Update.Set("updated_at", now)));
            Console.WriteLine($"     matched {res.MatchedCount} product " +
                              "(historical orders keep the old snapshot)");
        }
        Console.WriteLine("seed complete.");
        return 0;
    }

    static async Task RunSqlAsync(string path)
    {
        var sql = await File.ReadAllTextAsync(path);
        await using var conn = new NpgsqlConnection(PgConn);
        await conn.OpenAsync();
        // Npgsql executes multi-statement scripts in one call
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<int> ReindexEsAsync()
    {
        const string q = """
            SELECT o.id, o.user_id, o.order_date, o.status, o.total_amount,
                   o.updated_at, u.name, u.email,
                   i.product_id, i.title, i.quantity, i.unit_price
            FROM orders o
            JOIN users u ON u.id = o.user_id
            LEFT JOIN order_items i ON i.order_id = o.id
            ORDER BY o.id, i.id
            """;
        var docs = new List<JsonObject>();
        await using (var conn = new NpgsqlConnection(PgConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(q, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            JsonObject? cur = null;
            var curId = -1;
            while (await reader.ReadAsync())
            {
                var oid = reader.GetInt32(0);
                if (oid != curId)
                {
                    cur = new JsonObject
                    {
                        ["order_id"] = oid,
                        ["order_date"] = Iso(reader.GetDateTime(2)),
                        ["status"] = reader.GetString(3),
                        ["total_amount"] = reader.GetDouble(4),
                        ["updated_at"] = Iso(reader.GetDateTime(5)),
                        ["customer"] = new JsonObject
                        {
                            ["id"] = reader.GetInt32(1),
                            ["name"] = reader.GetString(6),
                            ["email"] = reader.GetString(7),
                        },
                        ["items"] = new JsonArray(),
                    };
                    docs.Add(cur);
                    curId = oid;
                }
                if (!await reader.IsDBNullAsync(8))
                {
                    cur!["items"]!.AsArray().Add(new JsonObject
                    {
                        ["product_id"] = reader.GetString(8),
                        ["title"] = reader.GetString(9),
                        ["quantity"] = reader.GetInt32(10),
                        ["unit_price"] = reader.GetDouble(11),
                    });
                }
            }
        }

        // ensure a PRISTINE index: PG was TRUNCATEd and MongoDB cleared above,
        // so ES must also start empty. Otherwise stale docs from previous runs
        // keep their old order_ids; a later dual-write would overwrite a stale
        // PENDING doc with a fresh PENDING doc and the search count would never
        // increase (t_sync_new_order would fail deterministically).
        using (var del = new HttpRequestMessage(HttpMethod.Delete, $"{EsUrl}/{EsIndex}"))
        using (var delResp = await Http.SendAsync(del))
        {
            // 200 = deleted, 404 = didn't exist; both are fine
        }
        {
            // PUT the whole file: ES expects {"mappings": {...}} at the top level
            var body = await File.ReadAllTextAsync("seed/es-mapping.json");
            using var put = new StringContent(body, Encoding.UTF8, "application/json");
            using var r2 = await Http.PutAsync($"{EsUrl}/{EsIndex}", put);
            r2.EnsureSuccessStatusCode();
        }

        if (docs.Count == 0) return 0;
        var sb = new StringBuilder();
        foreach (var d in docs)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                index = new { _index = EsIndex, _id = d["order_id"]!.GetValue<int>().ToString() },
            }));
            sb.AppendLine(d.ToJsonString());
        }
        using var bulk = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var bresp = await Http.PostAsync($"{EsUrl}/_bulk", bulk);
        bresp.EnsureSuccessStatusCode();
        return docs.Count;
    }

    static string Iso(DateTime dt) =>
        new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
}
