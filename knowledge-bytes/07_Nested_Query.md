# 07 — The Nested Query Trap

Source files in this byte: `backend/src/Y3L.Supply.Api/Services/SyncService.cs`, `backend/src/Y3L.Supply.Api/es-mapping.json`

---

### Byte 7: The nested query trap

**Builds on:** 6

**In plain terms:** In the Elasticsearch mapping, `items` is a *nested* field. A nested field stores each array element as a separate hidden document. A plain `multi_match` on `items.title` silently matches nothing. The query must wrap the match in a `nested` block that names the path. This bug cost a full debug cycle in the Python port: the search returned zero rows with no error.

**The code:**
```csharp
// SyncService.cs — BuildSearchBody, the fix for the silent empty result
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
```

**What's happening:** `BuildSearchBody` builds two clauses for a text query. Customer names use a plain `multi_match` with a 2x boost. Item titles use the `nested` wrapper above. The wrapper tells Elasticsearch to search inside the hidden item documents and average their scores.

**Why it matters:** Silent wrong results are worse than errors. The admin dashboard is the main demo screen. Without this wrapper, product-name search shows nothing and the demo fails.

---
