using AgentMcpUploader.Agent;
using AgentMcpUploader.Servers;
using Microsoft.Extensions.Configuration;

if (args.Contains("--files-server"))
{
    await FilesMcpServer.RunAsync();
    return;
}

if (args.Contains("--vault-server"))
{
    await VaultMcpServer.RunAsync();
    return;
}

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

await AgentRunner.RunAsync(config);
