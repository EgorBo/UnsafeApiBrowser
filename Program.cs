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

var builder = WebApplication.CreateBuilder(args[1..]);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tree", () => apiTree);

app.MapPost("/api/marker",async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<MarkerRequest>();
    if (body is null || string.IsNullOrEmpty(body.NodeId))
        return Results.BadRequest("Missing nodeId");

    if (!nodeIndex.TryGetValue(body.NodeId, out var node))
        return Results.NotFound($"Node not found: {body.NodeId}");

    if (!MarkerService.ToggleMarker(node, body.Marked))
        return Results.BadRequest("Cannot modify marker (no source location)");

    return Results.Ok(new { node.Id, node.IsMarked });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"Starting server on http://localhost:{port}");
app.Run($"http://localhost:{port}");
return 0;

record MarkerRequest(string NodeId, bool Marked);
