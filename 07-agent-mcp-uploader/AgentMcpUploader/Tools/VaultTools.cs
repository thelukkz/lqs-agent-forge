using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentMcpUploader.Tools;

[McpServerToolType]
class VaultTools
{
    private static string VaultRoot =>
        Environment.GetEnvironmentVariable("VAULT_ROOT")
        ?? Path.GetFullPath("workspace/vault");

    private static int VaultPort =>
        int.TryParse(Environment.GetEnvironmentVariable("VAULT_PORT"), out var p) ? p : 5001;

    [McpServerTool, Description("Store a base64-encoded file in the vault and return its URL.")]
    public static string VaultStore(
        [Description("File name with extension, e.g. 'hello.md'")] string filename,
        [Description("Base64-encoded file content")] string base64)
    {
        var root = VaultRoot;
        Directory.CreateDirectory(root);

        var target = Path.GetFullPath(Path.Combine(root, filename));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "Invalid filename" });

        File.WriteAllBytes(target, Convert.FromBase64String(base64));

        var url = $"http://localhost:{VaultPort}/files/{filename}";
        return JsonSerializer.Serialize(new { ok = true, filename, url });
    }

    [McpServerTool, Description("List all files currently stored in the vault with their URLs.")]
    public static string VaultList()
    {
        var root = VaultRoot;
        if (!Directory.Exists(root))
            return JsonSerializer.Serialize(new { files = Array.Empty<object>() });

        var port = VaultPort;
        var files = Directory.GetFiles(root)
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                return new { filename = name, url = $"http://localhost:{port}/files/{name}" };
            })
            .OrderBy(f => f.filename)
            .ToArray();

        return JsonSerializer.Serialize(new { files });
    }
}
