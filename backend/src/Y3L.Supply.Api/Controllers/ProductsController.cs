using Microsoft.AspNetCore.Mvc;
using Y3L.Supply.Api.Models;
using Y3L.Supply.Api.Repos;

namespace Y3L.Supply.Api.Controllers;

/// <summary>Product catalog routes — MongoDB ONLY.</summary>
[ApiController]
[Route("api/products")]
public class ProductsController(ProductRepo repo) : ControllerBase
{
    [HttpGet]
    public async Task<ProductList> List(
        [FromQuery] string? category, [FromQuery] string? q,
        [FromQuery(Name = "include_inactive")] bool includeInactive = false)
    {
        var items = await repo.ListAsync(category, q, includeInactive);
        return new ProductList(items, items.Count);
    }

    [HttpGet("{pid}")]
    public async Task<ProductDto> Get(string pid)
    {
        var p = await repo.GetAsync(pid);
        if (p is null) throw ApiException.NotFound("product not found");
        return p;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ProductDto> Create([FromBody] ProductCreate payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Sku) ||
            string.IsNullOrWhiteSpace(payload.Title) ||
            string.IsNullOrWhiteSpace(payload.Category))
            throw ApiException.BadRequest("sku, title and category are required");
        if (payload.Price is null || payload.Price <= 0)
            throw ApiException.BadRequest("price must be > 0");
        var p = await repo.CreateAsync(payload);
        Response.StatusCode = StatusCodes.Status201Created;
        return p;
    }

    [HttpPut("{pid}")]
    public async Task<ProductDto> Update(string pid, [FromBody] ProductUpdate payload)
    {
        if (payload.IsEmpty()) throw ApiException.BadRequest("empty update");
        if (payload.Price is not null && payload.Price <= 0)
            throw ApiException.BadRequest("price must be > 0");
        var p = await repo.UpdateAsync(pid, payload);
        if (p is null) throw ApiException.NotFound("product not found");
        return p;
    }
}
