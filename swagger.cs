"SwaggerAggregator": {
  "SourceUrls": [
    "http://localhost:5023/swagger/v1/swagger.json",
    "http://localhost:5013/swagger/v1/swagger.json",
    "http://localhost:5080/swagger/v1/swagger.json"
  ]
}


app.MapGet("/swagger/aggregate/swagger.json", async (IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    var sources = config.GetSection("SwaggerAggregator:SourceUrls").Get<string[]>() ?? Array.Empty<string>();

    var client = httpClientFactory.CreateClient();
    JsonObject? merged = null;

    foreach (var url in sources)
    {
        var json = await client.GetStringAsync(url);
        var doc = JsonNode.Parse(json)!.AsObject();

        if (merged is null)
        {
            merged = doc;
            merged["info"] = new JsonObject { ["title"] = "PMS Combined API", ["version"] = "v1" };
            continue;
        }

        var mergedPaths = merged["paths"]!.AsObject();
        foreach (var (path, value) in doc["paths"]!.AsObject())
            mergedPaths[path] = value?.DeepClone();

        var mergedSchemas = merged["components"]!["schemas"]!.AsObject();
        if (doc["components"]?["schemas"] is JsonObject schemas)
            foreach (var (name, value) in schemas)
                mergedSchemas[name] = value?.DeepClone();
    }

    return Results.Text(merged!.ToJsonString(), "application/json");
});
