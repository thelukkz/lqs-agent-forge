using AgentMcpTranslator;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

// ── MCP server mode ──────────────────────────────────────────────────────────

if (args.Contains("--server"))
{
    await McpFileServer.RunAsync(args);
    return;
}

// ── Client / orchestrator mode ───────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var apiKey = config["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey not set. Add via user secrets.");
var model   = config["OpenAI:Model"]   ?? "openai/gpt-4o-mini";
var baseUrl = config["OpenAI:BaseUrl"];

var sourceDir      = Path.GetFullPath(config["Translator:SourceDir"] ?? "workspace/translate");
var targetDir      = Path.GetFullPath(config["Translator:TargetDir"] ?? "workspace/translated");
var pollIntervalMs = int.Parse(config["Translator:PollIntervalMs"]   ?? "5000");

var oaiOptions = baseUrl is not null
    ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
    : new OpenAIClientOptions();

var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), oaiOptions);

// ── Spawn self as MCP subprocess ─────────────────────────────────────────────

var workspaceRoot = Path.GetFullPath(Path.Combine(sourceDir, ".."));
var exePath       = Process.GetCurrentProcess().MainModule!.FileName;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command   = exePath,
    Arguments = ["--server"],
    Name      = "AgentMcpTranslator",
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["FS_ROOT"] = workspaceRoot
    }
});

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== MCP Translator ===");
Console.WriteLine($"Model:     {model}");
Console.WriteLine($"Source:    {sourceDir}");
Console.WriteLine($"Target:    {targetDir}");
Console.WriteLine();

await using var mcpClient = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation { Name = "AgentMcpTranslator", Version = "1.0.0" }
});

var tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
Console.WriteLine($"MCP tools: {string.Join(", ", tools.Select(t => t.Name))}");
Console.WriteLine();

var agent       = new TranslatorAgent(openAiClient, model, mcpClient);
var fileWatcher = new FileWatcher(agent, sourceDir, targetDir, pollIntervalMs);

// ── HTTP server ───────────────────────────────────────────────────────────────

var httpPort = int.Parse(config["HttpServer:Port"] ?? "3000");
var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{httpPort}/");
listener.Start();

Console.WriteLine($"HTTP API:  http://localhost:{httpPort}/api/translate");
Console.WriteLine();

var httpTask = HandleHttpRequestsAsync(listener, agent, cts.Token);

// ── File watcher ──────────────────────────────────────────────────────────────

await Task.WhenAll(
    fileWatcher.RunAsync(cts.Token),
    httpTask);

listener.Stop();
Console.WriteLine("[shutdown] Done.");

// ── HTTP request handler ──────────────────────────────────────────────────────

static async Task HandleHttpRequestsAsync(
    HttpListener listener, TranslatorAgent agent, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext? ctx;
        try { ctx = await listener.GetContextAsync().WaitAsync(ct); }
        catch (OperationCanceledException) { break; }
        catch { break; }

        _ = HandleSingleRequestAsync(ctx, agent, ct);
    }
}

static async Task HandleSingleRequestAsync(
    HttpListenerContext ctx, TranslatorAgent agent, CancellationToken ct)
{
    var req  = ctx.Request;
    var resp = ctx.Response;

    resp.ContentType = "application/json; charset=utf-8";
    resp.Headers.Add("Access-Control-Allow-Origin", "*");

    if (req.HttpMethod == "OPTIONS")
    {
        resp.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        resp.StatusCode = 204;
        resp.Close();
        return;
    }

    if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/api/translate")
    {
        resp.StatusCode = 404;
        await WriteJson(resp, new { error = "Not found. Use POST /api/translate" });
        return;
    }

    try
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body    = await reader.ReadToEndAsync(ct);
        var payload = JsonSerializer.Deserialize<JsonElement>(body);

        if (!payload.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { error = "Body must be JSON with a 'text' string field." });
            return;
        }

        var text   = textEl.GetString()!;
        var prompt = $"Translate the following text to English. Preserve tone, formatting, and nuances:\n\n{text}";

        var (translation, stats) = await agent.RunAsync(prompt, ct);
        await WriteJson(resp, new { translation, stats = new { stats.InputTokens, stats.CachedTokens, stats.OutputTokens } });
    }
    catch (Exception ex)
    {
        resp.StatusCode = 500;
        await WriteJson(resp, new { error = ex.Message });
    }
}

static async Task WriteJson(HttpListenerResponse resp, object payload)
{
    var json  = JsonSerializer.Serialize(payload);
    var bytes = Encoding.UTF8.GetBytes(json);
    resp.ContentLength64 = bytes.Length;
    await resp.OutputStream.WriteAsync(bytes);
    resp.Close();
}
