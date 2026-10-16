using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace Y3L.Supply.Api.Controllers;

/// <summary>
/// Knowledge Bytes API — serves the repo's knowledge-bytes/*.md as JSON.
/// GET /api/knowledge-bytes      -> {"bytes": [{"id": "...", "title": "..."}], "total": 8}
/// GET /api/knowledge-bytes/{id} -> {"id": "...", "title": "...", "markdown": "..."} | 404
/// </summary>
[ApiController]
[Route("api/knowledge-bytes")]
public class KnowledgeBytesController : ControllerBase
{
    private static readonly Regex ByteFile = new(@"^\d{2}_.*\.md$", RegexOptions.Compiled);
    private static readonly Regex Slug = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex Title = new(@"^#\s+\d+\s+[—–-]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string? ResolveDir()
    {
        var env = Environment.GetEnvironmentVariable("KNOWLEDGE_BYTES_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var cwd = Directory.GetCurrentDirectory();
        foreach (var cand in new[]
                 {
                     Path.Combine(cwd, "knowledge-bytes"),
                     Path.Combine(cwd, "..", "knowledge-bytes"),
                     Path.Combine(cwd, "..", "..", "knowledge-bytes"),
                     // dotnet run --project forces CWD to backend/src/Y3L.Supply.Api
                     Path.Combine(cwd, "..", "..", "..", "knowledge-bytes"),
                 })
        {
            var full = Path.GetFullPath(cand);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    private static (string title, string text) Parse(string path)
    {
        var text = System.IO.File.ReadAllText(path);
        var m = Title.Match(text);
        var title = m.Success ? m.Groups[1].Value.Trim()
            : Path.GetFileNameWithoutExtension(path);
        return (title, text);
    }

    [HttpGet]
    public object List()
    {
        var items = new List<object>();
        var dir = ResolveDir();
        if (dir is not null)
        {
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x))
            {
                var fn = Path.GetFileName(f);
                if (!ByteFile.IsMatch(fn)) continue;
                var slug = Path.GetFileNameWithoutExtension(fn);
                var (title, _) = Parse(f);
                items.Add(new { id = slug, title });
            }
        }
        return new { bytes = items, total = items.Count };
    }

    [HttpGet("{slug}")]
    public object Get(string slug)
    {
        if (!Slug.IsMatch(slug) || !ByteFile.IsMatch(slug + ".md"))
            throw new ApiException(404, "knowledge byte not found");
        var dir = ResolveDir();
        var path = dir is not null ? Path.Combine(dir, slug + ".md") : null;
        if (path is null || !System.IO.File.Exists(path))
            throw new ApiException(404, "knowledge byte not found");
        var (title, text) = Parse(path);
        return new { id = slug, title, markdown = text };
    }
}
