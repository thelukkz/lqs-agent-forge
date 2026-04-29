using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace AgentMcpTranslator;

public static class McpFileServer
{
    public static async Task RunAsync(string[] args, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders(); // stdio must stay clean for JSON-RPC
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<FileSystemTools>();

        await builder.Build().RunAsync(ct);
    }
}

[McpServerToolType]
public class FileSystemTools
{
    private static readonly string FsRoot =
        Path.GetFullPath(Environment.GetEnvironmentVariable("FS_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "workspace"));

    private static string SafePath(string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(FsRoot, relativePath.TrimStart('/', '\\')));
        if (!resolved.StartsWith(FsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path traversal blocked: {relativePath}");
        return resolved;
    }

    [McpServerTool(Name = "fs_read")]
    [Description("Read a file or list a directory. mode='read' returns text content; mode='list' returns entries with size and line count.")]
    public static string FsRead(
        [Description("Path relative to workspace root")] string path,
        [Description("'read' to read file content, 'list' to list directory entries")] string mode = "read")
    {
        var fullPath = SafePath(path);

        if (mode == "list")
        {
            if (!Directory.Exists(fullPath))
                return Err($"Directory not found: {path}");

            var entries = Directory.EnumerateFileSystemEntries(fullPath)
                .OrderBy(e => e)
                .Select(e =>
                {
                    var name = Path.GetRelativePath(fullPath, e);
                    if (Directory.Exists(e))
                        return (object)new { name, type = "directory" };

                    var info = new FileInfo(e);
                    int? lines = null;
                    try { lines = File.ReadAllLines(e).Length; } catch { }
                    return (object)new { name, type = "file", sizeBytes = info.Length, lines };
                }).ToList();

            return JsonSerializer.Serialize(new { path, entries });
        }

        if (!File.Exists(fullPath))
            return Err($"File not found: {path}");

        var content = File.ReadAllText(fullPath);
        return JsonSerializer.Serialize(new { path, content, lineCount = content.Split('\n').Length });
    }

    [McpServerTool(Name = "fs_write")]
    [Description("Write text content to a file. Creates parent directories if needed. operation='create' overwrites; operation='append' adds to end.")]
    public static string FsWrite(
        [Description("Path relative to workspace root")] string path,
        [Description("Text content to write")] string content,
        [Description("'create' to overwrite, 'append' to add to existing file")] string operation = "create")
    {
        var fullPath = SafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (operation == "append")
            File.AppendAllText(fullPath, content);
        else
            File.WriteAllText(fullPath, content);

        return JsonSerializer.Serialize(new { success = true, path, operation });
    }

    [McpServerTool(Name = "fs_manage")]
    [Description("File system management operations: mkdir, delete.")]
    public static string FsManage(
        [Description("'mkdir' to create directory, 'delete' to remove file or directory")] string operation,
        [Description("Path relative to workspace root")] string path)
    {
        var fullPath = SafePath(path);

        return operation switch
        {
            "mkdir" => Manage(() => { Directory.CreateDirectory(fullPath); }, operation, path),
            "delete" when File.Exists(fullPath) => Manage(() => File.Delete(fullPath), operation, path),
            "delete" when Directory.Exists(fullPath) => Manage(() => Directory.Delete(fullPath, true), operation, path),
            "delete" => Err($"Path not found: {path}"),
            _ => Err($"Unknown operation: {operation}")
        };
    }

    private static string Manage(Action action, string operation, string path)
    {
        action();
        return JsonSerializer.Serialize(new { success = true, operation, path });
    }

    private static string Err(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
