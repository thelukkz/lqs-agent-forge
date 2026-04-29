using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;
using System.Diagnostics;
using System.Text.Json;

namespace AgentMcpCore.Client;

public static class ClientRunner
{
    public static async Task RunAsync(IConfiguration config)
    {
        var apiKey  = config["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("OpenAI:ApiKey not set. Add it via user secrets.");
        var model   = config["OpenAI:Model"] ?? "openai/gpt-4o-mini";
        var baseUrl = config["OpenAI:BaseUrl"];

        var oaiOptions = baseUrl is not null
            ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
            : new OpenAIClientOptions();

        var chatClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), oaiOptions)
            .GetChatClient(model);

        var exePath = Process.GetCurrentProcess().MainModule!.FileName;

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command   = exePath,
            Arguments = ["--server"],
            Name      = "AgentMcpCore"
        });

        var mcpOptions = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "AgentMcpCore-Client", Version = "1.0.0" },
            Capabilities = new ClientCapabilities
            {
                Sampling    = new SamplingCapability(),
                Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() }
            },
            Handlers = new McpClientHandlers
            {
                SamplingHandler    = BuildSamplingHandler(chatClient, model),
                ElicitationHandler = BuildElicitationHandler()
            }
        };

        Console.WriteLine("=== MCP Client Demo ===\n");

        await using var client = await McpClient.CreateAsync(transport, mcpOptions);

        await DemoTools(client);
        await DemoResources(client);
        await DemoPrompts(client);

        Console.WriteLine("\n=== Done ===");
    }

    // ── Tools ───────────────────────────────────────────────────────────────

    private static async Task DemoTools(McpClient client)
    {
        Console.WriteLine("--- Tools ---");
        var tools = await client.ListToolsAsync();
        foreach (var t in tools)
            Console.WriteLine($"  [{t.Name}] {t.Description}");

        Console.WriteLine("\n→ calculate(add, 10, 25)");
        var calcResult = await client.CallToolAsync("calculate", new Dictionary<string, object?>
        {
            ["operation"] = "add",
            ["a"]         = 10.0,
            ["b"]         = 25.0
        });
        Console.WriteLine($"  Result: {GetToolText(calcResult)}");

        Console.WriteLine("\n→ summarize_with_confirmation");
        const string SampleText =
            "The Model Context Protocol (MCP) is an open protocol that standardizes how applications " +
            "provide context to large language models. It separates context provision from model interaction, " +
            "enabling servers to expose tools, resources, and prompts to any compliant client.";

        var summarizeResult = await client.CallToolAsync("summarize_with_confirmation", new Dictionary<string, object?>
        {
            ["text"]      = SampleText,
            ["maxLength"] = 30
        });
        Console.WriteLine($"  Result: {GetToolText(summarizeResult)}");
    }

    // ── Resources ───────────────────────────────────────────────────────────

    private static async Task DemoResources(McpClient client)
    {
        Console.WriteLine("\n--- Resources ---");
        var resources = await client.ListResourcesAsync();
        foreach (var r in resources)
            Console.WriteLine($"  [{r.Name}] {r.Uri}");

        Console.WriteLine("\n→ read config://project");
        var configResult = await client.ReadResourceAsync("config://project");
        Console.WriteLine($"  {GetResourceText(configResult)}");

        Console.WriteLine("\n→ read data://stats");
        var statsResult = await client.ReadResourceAsync("data://stats");
        Console.WriteLine($"  {GetResourceText(statsResult)}");
    }

    // ── Prompts ─────────────────────────────────────────────────────────────

    private static async Task DemoPrompts(McpClient client)
    {
        Console.WriteLine("\n--- Prompts ---");
        var prompts = await client.ListPromptsAsync();
        foreach (var p in prompts)
            Console.WriteLine($"  [{p.Name}] {p.Description}");

        Console.WriteLine("\n→ get code-review(csharp, security)");
        var prompt = await client.GetPromptAsync("code-review", new Dictionary<string, object?>
        {
            ["code"]     = "public void Save(string path) => File.WriteAllText(path, userInput);",
            ["language"] = "csharp",
            ["focus"]    = "security"
        });

        foreach (var msg in prompt.Messages)
        {
            Console.WriteLine($"  [{msg.Role}]");
            Console.WriteLine($"  {((TextContentBlock)msg.Content).Text}");
        }
    }

    // ── Sampling handler ────────────────────────────────────────────────────

    private static Func<CreateMessageRequestParams?, IProgress<ProgressNotificationValue>, CancellationToken, ValueTask<CreateMessageResult>> BuildSamplingHandler(
        ChatClient chatClient, string model)
    {
        return async (request, _, ct) =>
        {
            Console.WriteLine("  [sampling] LLM call via client…");

            var messages = new List<ChatMessage>();

            if (request?.SystemPrompt is { } sys)
                messages.Add(new SystemChatMessage(sys));

            foreach (var msg in request?.Messages ?? [])
            {
                var msgText = msg.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
                messages.Add(msg.Role == Role.User
                    ? new UserChatMessage(msgText)
                    : (ChatMessage)new AssistantChatMessage(msgText));
            }

            var options  = new ChatCompletionOptions { MaxOutputTokenCount = request?.MaxTokens };
            var response = await chatClient.CompleteChatAsync(messages, options, ct);
            var text     = response.Value.Content[0].Text;

            return new CreateMessageResult
            {
                Role    = Role.Assistant,
                Content = [new TextContentBlock { Text = text }],
                Model   = model
            };
        };
    }

    // ── Elicitation handler ─────────────────────────────────────────────────

    private static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> BuildElicitationHandler()
    {
        return (request, _) =>
        {
            Console.WriteLine($"  [elicitation] Auto-accepting: \"{request?.Message}\"");

            var content = new Dictionary<string, JsonElement>();

            if (request?.RequestedSchema?.Properties is { } props)
            {
                foreach (var (key, schema) in props)
                {
                    content[key] = schema switch
                    {
                        ElicitRequestParams.BooleanSchema b =>
                            JsonSerializer.SerializeToElement(b.Default ?? true),
                        ElicitRequestParams.StringSchema s =>
                            JsonSerializer.SerializeToElement(s.Default ?? ""),
                        ElicitRequestParams.NumberSchema n =>
                            JsonSerializer.SerializeToElement(n.Default ?? 0),
                        ElicitRequestParams.UntitledSingleSelectEnumSchema e =>
                            JsonSerializer.SerializeToElement(e.Enum?.FirstOrDefault() ?? ""),
                        ElicitRequestParams.TitledSingleSelectEnumSchema t =>
                            JsonSerializer.SerializeToElement(t.OneOf?.FirstOrDefault()?.Const ?? ""),
                        _ =>
                            JsonSerializer.SerializeToElement("")
                    };
                }
            }

            return ValueTask.FromResult(new ElicitResult
            {
                Action  = "accept",
                Content = content
            });
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string GetToolText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no text)";

    private static string GetResourceText(ReadResourceResult result) =>
        result.Contents.OfType<TextResourceContents>().FirstOrDefault()?.Text ?? "(no text)";
}
