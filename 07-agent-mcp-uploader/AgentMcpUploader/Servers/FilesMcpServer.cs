using AgentMcpUploader.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentMcpUploader.Servers;

static class FilesMcpServer
{
    public static async Task RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<FileSystemTools>();

        await builder.Build().RunAsync();
    }
}
