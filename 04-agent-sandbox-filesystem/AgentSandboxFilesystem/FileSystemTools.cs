using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace AgentSandboxFilesystem;

static class FileSystemTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<ChatTool> Definitions { get; } =
    [
        ChatTool.CreateFunctionTool(
            "list_files",
            "List files and directories at a path within the sandbox",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Directory path relative to sandbox root; use '.' for root" }
                },
                required = new[] { "path" },
                additionalProperties = false
            })),

        ChatTool.CreateFunctionTool(
            "read_file",
            "Read the text content of a file",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path relative to sandbox root" }
                },
                required = new[] { "path" },
                additionalProperties = false
            })),

        ChatTool.CreateFunctionTool(
            "write_file",
            "Write text content to a file, creating it if it does not exist",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path    = new { type = "string", description = "File path relative to sandbox root" },
                    content = new { type = "string", description = "Text content to write" }
                },
                required = new[] { "path", "content" },
                additionalProperties = false
            })),

        ChatTool.CreateFunctionTool(
            "delete_file",
            "Delete a file from the sandbox",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path relative to sandbox root" }
                },
                required = new[] { "path" },
                additionalProperties = false
            })),

        ChatTool.CreateFunctionTool(
            "create_directory",
            "Create a directory (including any missing parent directories)",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Directory path relative to sandbox root" }
                },
                required = new[] { "path" },
                additionalProperties = false
            })),

        ChatTool.CreateFunctionTool(
            "file_info",
            "Get metadata about a file or directory (type, size, last modified)",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path relative to sandbox root" }
                },
                required = new[] { "path" },
                additionalProperties = false
            }))
    ];

    public static async Task<string> ExecuteAsync(string name, JsonElement args)
    {
        try
        {
            return name switch
            {
                "list_files"       => await ListFilesAsync(args),
                "read_file"        => await ReadFileAsync(args),
                "write_file"       => await WriteFileAsync(args),
                "delete_file"      => DeleteFile(args),
                "create_directory" => CreateDirectory(args),
                "file_info"        => GetFileInfo(args),
                _                  => Serialize(new { error = $"Unknown tool: {name}" })
            };
        }
        catch (Exception ex)
        {
            return Serialize(new { error = ex.Message });
        }
    }

    static async Task<string> ListFilesAsync(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString() ?? ".";
        var resolved = Sandbox.ResolvePath(path);

        if (!Directory.Exists(resolved))
            return Serialize(new { error = $"Directory not found: {path}" });

        var entries = Directory.EnumerateFileSystemEntries(resolved)
            .Select(e => new
            {
                name = Path.GetRelativePath(resolved, e),
                type = Directory.Exists(e) ? "directory" : "file"
            })
            .ToArray();

        return Serialize(new { path, entries });
    }

    static async Task<string> ReadFileAsync(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString()!;
        var resolved = Sandbox.ResolvePath(path);

        if (!File.Exists(resolved))
            return Serialize(new { error = $"File not found: {path}" });

        var content = await File.ReadAllTextAsync(resolved);
        return Serialize(new { path, content });
    }

    static async Task<string> WriteFileAsync(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString()!;
        var content  = args.GetProperty("content").GetString()!;
        var resolved = Sandbox.ResolvePath(path);

        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        await File.WriteAllTextAsync(resolved, content);
        return Serialize(new { success = true, path });
    }

    static string DeleteFile(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString()!;
        var resolved = Sandbox.ResolvePath(path);

        if (!File.Exists(resolved))
            return Serialize(new { error = $"File not found: {path}" });

        File.Delete(resolved);
        return Serialize(new { success = true, path });
    }

    static string CreateDirectory(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString()!;
        var resolved = Sandbox.ResolvePath(path);

        Directory.CreateDirectory(resolved);
        return Serialize(new { success = true, path });
    }

    static string GetFileInfo(JsonElement args)
    {
        var path     = args.GetProperty("path").GetString()!;
        var resolved = Sandbox.ResolvePath(path);

        if (Directory.Exists(resolved))
        {
            var info = new DirectoryInfo(resolved);
            return Serialize(new { path, type = "directory", lastModified = info.LastWriteTimeUtc });
        }

        if (File.Exists(resolved))
        {
            var info = new FileInfo(resolved);
            return Serialize(new { path, type = "file", size = info.Length, lastModified = info.LastWriteTimeUtc });
        }

        return Serialize(new { error = $"Path not found: {path}" });
    }

    static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
