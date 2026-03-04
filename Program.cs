using System.Text.Json;
using UnsafeApiBrowser;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run -- <path-to-dotnet-runtime-repo>");
    Console.Error.WriteLine("Example: dotnet run -- C:\\prj\\runtime-main2");
    return 1;
}

var runtimePath = args[0];
if (!Directory.Exists(runtimePath))
{
    Console.Error.WriteLine($"Directory not found: {runtimePath}");
    return 1;
}

Console.WriteLine($"Parsing public API from: {runtimePath}");
var apiTree = ApiParser.Parse(runtimePath);
var nodeIndex = MarkerService.BuildIndex(apiTree);
Console.WriteLine($"Loaded {nodeIndex.Count} API nodes.");

// Populate relative source paths for GitHub links
foreach (var node in nodeIndex.Values)
{
    if (node.SourceFile is not null)
    {
        node.Src = Path.GetRelativePath(runtimePath, node.SourceFile).Replace('\\', '/');
        node.Line = node.SourceLine;
    }
}

// Pre-serialize the tree JSON once for fast responses
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};
var treeJson = JsonSerializer.Serialize(apiTree, jsonOptions);
Console.WriteLine($"Tree JSON: {treeJson.Length / 1024}KB");

var builder = WebApplication.CreateBuilder(args[1..]);
builder.Services.AddResponseCompression();

var app = builder.Build();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tree", () => Results.Text(treeJson, "application/json"));

app.MapPost("/api/marker",async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<MarkerRequest>();
    if (body is null || string.IsNullOrEmpty(body.NodeId))
        return Results.BadRequest("Missing nodeId");

    if (!nodeIndex.TryGetValue(body.NodeId, out var node))
        return Results.NotFound($"Node not found: {body.NodeId}");

    if (!MarkerService.ToggleMarker(node, body.Marked))
        return Results.BadRequest("Cannot modify marker (no source location)");

    // Refresh cached tree JSON after marker change
    treeJson = JsonSerializer.Serialize(apiTree, jsonOptions);
    return Results.Ok(new { node.Id, node.IsMarked });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"Starting server on http://localhost:{port}");
app.Run($"http://0.0.0.0:{port}");
return 0;

record MarkerRequest(string NodeId, bool Marked);
