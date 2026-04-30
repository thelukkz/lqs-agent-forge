using AgentMcpUploader.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace AgentMcpUploader.Servers;

static class VaultMcpServer
{
    public static async Task RunAsync()
    {
        var vaultRoot = Environment.GetEnvironmentVariable("VAULT_ROOT")
            ?? Path.GetFullPath("workspace/vault");
        var port = int.TryParse(Environment.GetEnvironmentVariable("VAULT_PORT"), out var p) ? p : 5001;

        Directory.CreateDirectory(vaultRoot);

        // Serve stored files over HTTP alongside the stdio MCP server
        _ = RunHttpFileServerAsync(vaultRoot, port);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<VaultTools>();

        await builder.Build().RunAsync();
    }

    private static async Task RunHttpFileServerAsync(string vaultRoot, int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }

            _ = HandleAsync(ctx, vaultRoot);
        }
    }

    private static async Task HandleAsync(HttpListenerContext ctx, string vaultRoot)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";

        if (!path.StartsWith("/files/"))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var filename = path["/files/".Length..];
        var filePath = Path.GetFullPath(Path.Combine(vaultRoot, filename));

        if (!filePath.StartsWith(vaultRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(filePath);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.ContentType = "application/octet-stream";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
