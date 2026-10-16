using System.Text;

namespace Y3L.Supply.Api.Services;

/// <summary>
/// Minimal Elasticsearch HTTP client — sends the same raw JSON bodies the
/// reference implementation builds, so behavior stays identical.
/// </summary>
public class EsClient(string baseUrl)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _base = baseUrl.TrimEnd('/');

    private async Task<string> RequestAsync(HttpMethod method, string path,
        string? body = null, string? contentType = null)
    {
        using var req = new HttpRequestMessage(method, _base + path);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ES {method} {path} -> {(int)resp.StatusCode}: {text}");
        return text;
    }

    public async Task<bool> IndexExistsAsync(string index)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{_base}/{index}");
        using var resp = await Http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    public Task CreateIndexAsync(string index, string mappingsJson) =>
        RequestAsync(HttpMethod.Put, $"/{index}", mappingsJson);

    /// <summary>Index one doc; refresh=wait_for makes it searchable before the API returns.</summary>
    public Task IndexDocAsync(string index, string id, string docJson, bool refreshWaitFor) =>
        RequestAsync(HttpMethod.Put,
            $"/{index}/_doc/{id}" + (refreshWaitFor ? "?refresh=wait_for" : ""), docJson);

    public Task BulkAsync(string ndJson) =>
        RequestAsync(HttpMethod.Post, "/_bulk", ndJson, "application/x-ndjson");

    public Task<string> SearchAsync(string index, string bodyJson, int from, int size) =>
        RequestAsync(HttpMethod.Post, $"/{index}/_search?from={from}&size={size}", bodyJson);
}
