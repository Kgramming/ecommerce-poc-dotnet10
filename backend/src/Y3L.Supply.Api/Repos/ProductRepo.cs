using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using Y3L.Supply.Api.Models;

namespace Y3L.Supply.Api.Repos;

/// <summary>MongoDB product-catalog repository. Products are read from MongoDB ONLY.</summary>
public class ProductRepo(IMongoClient client, string database)
{
    private readonly IMongoCollection<BsonDocument> _col =
        client.GetDatabase(database).GetCollection<BsonDocument>("products");

    private static string UtcNow() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    internal static ProductDto ToProduct(BsonDocument doc) => new(
        doc["_id"].AsObjectId.ToString(),
        doc.GetValue("sku", "").AsString,
        doc.GetValue("title", "").AsString,
        doc.GetValue("description", "").AsString,
        doc.GetValue("price", 0).ToDouble(),
        doc.GetValue("category", "").AsString,
        doc.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToList(),
        doc.GetValue("attributes", new BsonDocument()).AsBsonDocument
            .ToDictionary(e => e.Name, e => (object?)BsonToObject(e.Value)),
        doc.GetValue("variants", new BsonArray()).AsBsonArray
            .Select(v => v.AsBsonDocument.ToDictionary(e => e.Name, e => (object?)BsonToObject(e.Value)))
            .ToList(),
        doc.GetValue("active", true).AsBoolean,
        doc.GetValue("updated_at", "").AsString);

    private static object? BsonToObject(BsonValue v) => v.BsonType switch
    {
        BsonType.String => v.AsString,
        BsonType.Int32 => v.AsInt32,
        BsonType.Int64 => v.AsInt64,
        BsonType.Double => v.AsDouble,
        BsonType.Boolean => v.AsBoolean,
        BsonType.Array => v.AsBsonArray.Select(BsonToObject).ToList(),
        BsonType.Document => v.AsBsonDocument.ToDictionary(e => e.Name, e => BsonToObject(e.Value)),
        BsonType.ObjectId => v.AsObjectId.ToString(),
        _ => v.ToString(),
    };

    public async Task<List<ProductDto>> ListAsync(string? category, string? q, bool includeInactive)
    {
        var filters = new List<FilterDefinition<BsonDocument>>();
        if (!includeInactive)
            filters.Add(Builders<BsonDocument>.Filter.Eq("active", true));
        if (!string.IsNullOrWhiteSpace(category))
            // case-insensitive exact match: the UI sends "Audio", the DB stores "audio"
            filters.Add(Builders<BsonDocument>.Filter.Regex("category",
                new BsonRegularExpression($"^{Regex.Escape(category)}$", "i")));
        if (!string.IsNullOrWhiteSpace(q))
            filters.Add(Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("title", new BsonRegularExpression(q, "i")),
                Builders<BsonDocument>.Filter.Regex("tags", new BsonRegularExpression(q, "i"))));

        var filter = filters.Count == 0
            ? Builders<BsonDocument>.Filter.Empty
            : Builders<BsonDocument>.Filter.And(filters);
        var docs = await _col.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("title"))
            .ToListAsync();
        return docs.Select(ToProduct).ToList();
    }

    public async Task<ProductDto?> GetAsync(string pid)
    {
        if (!ObjectId.TryParse(pid, out var oid)) return null;
        var doc = await _col.Find(Builders<BsonDocument>.Filter.Eq("_id", oid))
            .FirstOrDefaultAsync();
        return doc is null ? null : ToProduct(doc);
    }

    public async Task<Dictionary<string, ProductDto>> GetManyAsync(List<string> pids)
    {
        var oids = pids.Where(p => ObjectId.TryParse(p, out _))
            .Select(ObjectId.Parse).ToList();
        if (oids.Count == 0) return new();
        var docs = await _col.Find(Builders<BsonDocument>.Filter.In("_id", oids)).ToListAsync();
        return docs.ToDictionary(d => d["_id"].AsObjectId.ToString(), ToProduct);
    }

    public async Task<ProductDto> CreateAsync(ProductCreate data)
    {
        var doc = new BsonDocument
        {
            ["sku"] = data.Sku,
            ["title"] = data.Title,
            ["description"] = data.Description ?? "",
            ["price"] = data.Price!.Value,
            ["category"] = data.Category,
            ["tags"] = new BsonArray(data.Tags ?? []),
            ["attributes"] = new BsonDocument(),
            ["variants"] = new BsonArray(),
            ["active"] = data.Active ?? true,
            ["updated_at"] = UtcNow(),
        };
        // attributes / variants may carry nested values; store as-is via JSON round-trip
        if (data.Attributes is not null)
            doc["attributes"] = BsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(data.Attributes));
        if (data.Variants is not null)
            doc["variants"] = BsonArray.Create(data.Variants.Select(v =>
                BsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(v))));
        await _col.InsertOneAsync(doc);
        return ToProduct(doc);
    }

    public async Task<ProductDto?> UpdateAsync(string pid, ProductUpdate patch)
    {
        if (!ObjectId.TryParse(pid, out var oid)) return null;
        var updates = new List<UpdateDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Update.Set("updated_at", UtcNow()),
        };
        if (patch.Sku is not null) updates.Add(Builders<BsonDocument>.Update.Set("sku", patch.Sku));
        if (patch.Title is not null) updates.Add(Builders<BsonDocument>.Update.Set("title", patch.Title));
        if (patch.Description is not null) updates.Add(Builders<BsonDocument>.Update.Set("description", patch.Description));
        if (patch.Price is not null) updates.Add(Builders<BsonDocument>.Update.Set("price", patch.Price.Value));
        if (patch.Category is not null) updates.Add(Builders<BsonDocument>.Update.Set("category", patch.Category));
        if (patch.Tags is not null) updates.Add(Builders<BsonDocument>.Update.Set("tags", new BsonArray(patch.Tags)));
        if (patch.Attributes is not null) updates.Add(Builders<BsonDocument>.Update.Set("attributes",
            BsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(patch.Attributes))));
        if (patch.Variants is not null) updates.Add(Builders<BsonDocument>.Update.Set("variants",
            BsonArray.Create(patch.Variants.Select(v =>
                BsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(v))))));
        if (patch.Active is not null) updates.Add(Builders<BsonDocument>.Update.Set("active", patch.Active.Value));

        var doc = await _col.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", oid),
            Builders<BsonDocument>.Update.Combine(updates),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        return doc is null ? null : ToProduct(doc);
    }
}
