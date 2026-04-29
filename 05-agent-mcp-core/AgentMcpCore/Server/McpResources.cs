using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace AgentMcpCore.Server;

[McpServerResourceType]
public class McpResources
{
    private static readonly DateTime s_startTime = DateTime.UtcNow;
    private static int s_requestCount;

    [McpServerResource(
        UriTemplate = "config://project",
        Name        = "project-config",
        Title       = "Project Configuration",
        MimeType    = "application/json")]
    [Description("Static project metadata")]
    public static string GetProjectConfig() =>
        JsonSerializer.Serialize(new
        {
            name     = "agent-mcp-core",
            version  = "1.0",
            features = new[] { "tools", "resources", "prompts", "sampling", "elicitation" }
        });

    [McpServerResource(
        UriTemplate = "data://stats",
        Name        = "runtime-stats",
        Title       = "Runtime Statistics",
        MimeType    = "application/json")]
    [Description("Dynamic server runtime statistics")]
    public static string GetRuntimeStats()
    {
        Interlocked.Increment(ref s_requestCount);
        return JsonSerializer.Serialize(new
        {
            uptime_seconds = (int)(DateTime.UtcNow - s_startTime).TotalSeconds,
            request_count  = s_requestCount,
            timestamp      = DateTime.UtcNow.ToString("O")
        });
    }
}
