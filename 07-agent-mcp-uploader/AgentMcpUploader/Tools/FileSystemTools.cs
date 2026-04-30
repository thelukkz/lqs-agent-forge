using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentMcpUploader.Tools;

[McpServerToolType]
class FileSystemTools
{
    private static string FsRoot =>
        Environment.GetEnvironmentVariable("FS_ROOT")
        ?? Path.GetFullPath("workspace/source");

    [McpServerTool, Description("List all files in the source workspace.")]
    public static string FsList()
    {
        var root = FsRoot;
        if (!Directory.Exists(root))
            return JsonSerializer.Serialize(new { files = Array.Empty<string>() });

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToArray();

        return JsonSerializer.Serialize(new { files });
    }

    [McpServerTool, Description("Read a file from the source workspace.")]
    public static string FsRead([Description("Relative path to the file")] string path)
    {
        var root = FsRoot;
        var full = Path.GetFullPath(Path.Combine(root, path));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "Path traversal not allowed" });

        if (!File.Exists(full))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

        return JsonSerializer.Serialize(new { path, content = File.ReadAllText(full) });
    }
}
