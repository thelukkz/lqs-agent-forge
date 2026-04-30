using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;
using System.Diagnostics;
using System.Text.Json;

namespace AgentMcpUploader.Agent;

static class AgentRunner
{
    private const int MaxSteps = 40;

    private const string SystemPrompt = """
        You are a file upload assistant.

        To upload a file, use the {{file:path}} placeholder in the base64 parameter of vault__vault_store.
        The system resolves it to the actual file content automatically — never read the file yourself before uploading.

        Example: vault__vault_store({ "filename": "hello.md", "base64": "{{file:hello.md}}" })

        Workflow:
        1. files__fs_list  — list files in the source workspace
        2. vault__vault_list — check which files are already in the vault
        3. For each file not yet in the vault: vault__vault_store using {{file:<name>}} placeholder
        4. Report: "Upload complete: X files uploaded, Y skipped."

        Rules:
        - Never call files__fs_read to read file content before uploading
        - Skip files already listed by vault__vault_list
        """;

    public static async Task RunAsync(IConfiguration config)
    {
        var apiKey  = config["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not set");
        var model   = config["OpenAI:Model"]   ?? "openai/gpt-4o-mini";
        var baseUrl = config["OpenAI:BaseUrl"];
        var vaultPort  = config["Vault:Port"] ?? "5001";
        var sourceDir  = Path.GetFullPath(config["Workspace:SourceDir"] ?? "workspace/source");
        var vaultDir   = Path.GetFullPath(config["Workspace:VaultDir"]  ?? "workspace/vault");
        var exePath    = Process.GetCurrentProcess().MainModule!.FileName;

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(vaultDir);

        // ── Connect to MCP servers ────────────────────────────────────────────

        var filesTransport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command   = exePath,
            Arguments = ["--files-server"],
            Name      = "files",
            EnvironmentVariables = new Dictionary<string, string?> { ["FS_ROOT"] = sourceDir }
        });

        var vaultTransport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command   = exePath,
            Arguments = ["--vault-server"],
            Name      = "vault",
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["VAULT_ROOT"] = vaultDir,
                ["VAULT_PORT"] = vaultPort
            }
        });

        Console.WriteLine("[setup] Connecting to MCP servers...");
        await using var filesClient = await McpClient.CreateAsync(filesTransport);
        await using var vaultClient = await McpClient.CreateAsync(vaultTransport);

        // ── Build prefixed tool list ──────────────────────────────────────────
        // Each server's tools are prefixed with the server name (files__ / vault__).
        // The prefix lets the agent route tool calls back to the correct server
        // without knowing their internal names.

        var chatTools = new List<ChatTool>();
        var toolMap   = new Dictionary<string, (McpClient client, string originalName)>();

        await RegisterTools(filesClient, "files", chatTools, toolMap);
        await RegisterTools(vaultClient, "vault",  chatTools, toolMap);

        Console.WriteLine($"[setup] Tools: {string.Join(", ", chatTools.Select(t => t.FunctionName))}");
        Console.WriteLine();

        // ── Build OpenAI client ───────────────────────────────────────────────

        var oaiOptions = baseUrl is not null
            ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
            : new OpenAIClientOptions();

        var chatClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), oaiOptions)
            .GetChatClient(model);

        var options = new ChatCompletionOptions();
        foreach (var tool in chatTools) options.Tools.Add(tool);

        // ── Agent loop ────────────────────────────────────────────────────────

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage("Upload all files from the source workspace to the vault.")
        };

        for (var step = 0; step < MaxSteps; step++)
        {
            var response   = await chatClient.CompleteChatAsync(messages, options);
            var completion = response.Value;

            messages.Add(new AssistantChatMessage(completion));

            if (completion.FinishReason == ChatFinishReason.Stop)
            {
                Console.WriteLine();
                Console.WriteLine($"[done] {completion.Content[0].Text}");
                break;
            }

            if (completion.FinishReason != ChatFinishReason.ToolCalls) break;

            foreach (var call in completion.ToolCalls)
            {
                var rawArgs      = call.FunctionArguments.ToString();
                var resolvedArgs = PlaceholderResolver.Resolve(rawArgs, sourceDir);

                Console.WriteLine($"[tool] {call.FunctionName}({Summarize(call.FunctionName, resolvedArgs)})");

                var result = await DispatchToolAsync(call.FunctionName, resolvedArgs, toolMap);

                Console.WriteLine($"       → {Truncate(result, 120)}");
                messages.Add(new ToolChatMessage(call.Id, result));
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task RegisterTools(
        McpClient client,
        string prefix,
        List<ChatTool> chatTools,
        Dictionary<string, (McpClient, string)> toolMap)
    {
        var tools = await client.ListToolsAsync();
        foreach (var t in tools)
        {
            var prefixed = $"{prefix}__{t.Name}";
            chatTools.Add(ChatTool.CreateFunctionTool(
                prefixed,
                t.Description,
                BinaryData.FromObjectAsJson(t.JsonSchema)));
            toolMap[prefixed] = (client, t.Name);
        }
    }

    private static async Task<string> DispatchToolAsync(
        string prefixedName,
        string resolvedArgsJson,
        Dictionary<string, (McpClient client, string originalName)> toolMap)
    {
        if (!toolMap.TryGetValue(prefixedName, out var entry))
            return JsonSerializer.Serialize(new { error = $"Unknown tool: {prefixedName}" });

        var argsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resolvedArgsJson) ?? [];
        var args = argsDict.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)(kvp.Value.ValueKind switch
            {
                JsonValueKind.String => kvp.Value.GetString(),
                JsonValueKind.True   => (object?)true,
                JsonValueKind.False  => false,
                JsonValueKind.Number => kvp.Value.TryGetInt64(out var l) ? (object?)l : kvp.Value.GetDouble(),
                _                    => kvp.Value.GetRawText()
            }));

        var result = await entry.client.CallToolAsync(entry.originalName, args!);
        return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
    }

    private static string Summarize(string toolName, string args)
    {
        try
        {
            var doc = JsonDocument.Parse(args);
            if (toolName.EndsWith("vault_store") && doc.RootElement.TryGetProperty("filename", out var fn))
                return $"filename={fn.GetString()}";
            if (doc.RootElement.TryGetProperty("path", out var path))
                return path.GetString() ?? args;
        }
        catch { /* fall through */ }
        return Truncate(args, 60);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
