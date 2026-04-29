using AgentMcpCore.Client;
using AgentMcpCore.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--server"))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders(); // stdio must stay clean — all output goes to stdout
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<McpTools>()
        .WithResources<McpResources>()
        .WithPrompts<McpPrompts>();

    await builder.Build().RunAsync();
    return;
}

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

await ClientRunner.RunAsync(config);
