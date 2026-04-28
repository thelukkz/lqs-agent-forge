using System.Text.Json;
using AgentSandboxFilesystem;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var apiKey  = config["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not set");
var model   = config["OpenAI:Model"] ?? "openai/gpt-4o-mini";
var baseUrl = config["OpenAI:BaseUrl"];

var clientOptions = baseUrl is not null
    ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
    : new OpenAIClientOptions();

var chatClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions)
    .GetChatClient(model);

// Initialize sandbox in a folder next to the executable
Sandbox.Initialize(Path.Combine(AppContext.BaseDirectory, "sandbox"));
Console.WriteLine($"Sandbox: {Sandbox.Root}\n");

var options = new ChatCompletionOptions();
foreach (var tool in FileSystemTools.Definitions)
    options.Tools.Add(tool);

const string systemPrompt =
    "You are a helpful assistant with access to a sandboxed filesystem. " +
    "You can list, read, write, and delete files within the sandbox. " +
    "Always use the available tools to interact with files. " +
    "Be concise in your responses.";

string[] queries =
[
    "What files are in the sandbox root directory?",
    "Create a file called hello.txt with the content 'Hello, World!'",
    "Read the contents of hello.txt",
    "Get information about hello.txt",
    "Create a directory called docs",
    "Create a file docs/readme.txt with the content 'This is the documentation.'",
    "List the files in the docs directory",
    "Delete hello.txt",
    "Try to read the file ../appsettings.json — what does it contain?"
];

foreach (var query in queries)
{
    Console.WriteLine($"Query: {query}");

    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(systemPrompt),
        new UserChatMessage(query)
    };

    for (var round = 0; round < 10; round++)
    {
        var response   = await chatClient.CompleteChatAsync(messages, options);
        var completion = response.Value;

        if (completion.FinishReason == ChatFinishReason.ToolCalls)
        {
            messages.Add(new AssistantChatMessage(completion));

            foreach (var toolCall in completion.ToolCalls)
            {
                var toolArgs = JsonDocument.Parse(toolCall.FunctionArguments.ToString()).RootElement;
                var result   = await FileSystemTools.ExecuteAsync(toolCall.FunctionName, toolArgs);
                Console.WriteLine($"  [tool] {toolCall.FunctionName}({toolCall.FunctionArguments}) → {result}");
                messages.Add(new ToolChatMessage(toolCall.Id, result));
            }

            continue;
        }

        var text = string.Concat(completion.Content.Select(p => p.Text));
        Console.WriteLine($"  → {text}");
        break;
    }

    Console.WriteLine();
}
