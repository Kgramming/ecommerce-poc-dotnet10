using System.Text.Json.Serialization;

namespace Y3L.Supply.Api.Models;

/// <summary>JSON shapes are frozen by CONTRACT.md — wire names stay snake_case.</summary>
public record ProductDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("price")] double Price,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("tags")] List<string> Tags,
    [property: JsonPropertyName("attributes")] Dictionary<string, object?> Attributes,
    [property: JsonPropertyName("variants")] List<Dictionary<string, object?>> Variants,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public record ProductCreate(
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("price")] double? Price,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("tags")] List<string>? Tags,
    [property: JsonPropertyName("attributes")] Dictionary<string, object?>? Attributes,
    [property: JsonPropertyName("variants")] List<Dictionary<string, object?>>? Variants,
    [property: JsonPropertyName("active")] bool? Active);

public record ProductUpdate(
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("price")] double? Price,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("tags")] List<string>? Tags,
    [property: JsonPropertyName("attributes")] Dictionary<string, object?>? Attributes,
    [property: JsonPropertyName("variants")] List<Dictionary<string, object?>>? Variants,
    [property: JsonPropertyName("active")] bool? Active)
{
    public bool IsEmpty() =>
        Sku is null && Title is null && Description is null && Price is null &&
        Category is null && Tags is null && Attributes is null &&
        Variants is null && Active is null;
}

public record ProductList(
    [property: JsonPropertyName("products")] List<ProductDto> Products,
    [property: JsonPropertyName("total")] int Total);

public record OrderItemIn(
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("quantity")] int Quantity);

public record OrderPlace(
    [property: JsonPropertyName("user_id")] int UserId,
    [property: JsonPropertyName("items")] List<OrderItemIn>? Items);

public record OrderItemOut(
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit_price")] double UnitPrice);

public record CustomerOut(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email);

public record OrderOut(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("user_id")] int UserId,
    [property: JsonPropertyName("customer")] CustomerOut Customer,
    [property: JsonPropertyName("order_date")] string OrderDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("total_amount")] double TotalAmount,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("items")] List<OrderItemOut> Items);

public record StatusUpdate(
    [property: JsonPropertyName("status")] string? Status);

public record SearchRequest(
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("statuses")] List<string>? Statuses,
    [property: JsonPropertyName("date_from")] string? DateFrom,
    [property: JsonPropertyName("date_to")] string? DateTo,
    [property: JsonPropertyName("min_total")] double? MinTotal,
    [property: JsonPropertyName("max_total")] double? MaxTotal,
    [property: JsonPropertyName("page")] int? Page,
    [property: JsonPropertyName("page_size")] int? PageSize)
{
    public int PageOrDefault() => Page is >= 1 ? Page.Value : 1;
    public int PageSizeOrDefault() => Math.Min(Math.Max(PageSize ?? 20, 1), 1000);
}

public record SearchOrderHit(
    [property: JsonPropertyName("order_id")] int OrderId,
    [property: JsonPropertyName("order_date")] string OrderDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("total_amount")] double TotalAmount,
    [property: JsonPropertyName("customer")] CustomerOut Customer,
    [property: JsonPropertyName("items")] List<OrderItemOut> Items);

public record SearchKpis(
    [property: JsonPropertyName("total_revenue")] double TotalRevenue,
    [property: JsonPropertyName("count_by_status")] Dictionary<string, int> CountByStatus);

public record SearchResponse(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("orders")] List<SearchOrderHit> Orders,
    [property: JsonPropertyName("kpis")] SearchKpis Kpis);
