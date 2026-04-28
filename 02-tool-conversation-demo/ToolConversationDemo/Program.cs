using System.Text;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var apiKey = config["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey not set");
var model = config["OpenAI:Model"] ?? "openai/gpt-4o-mini";
var baseUrl = config["OpenAI:BaseUrl"];

var clientOptions = baseUrl is not null
    ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
    : new OpenAIClientOptions();

var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions).GetChatClient(model);
var history = new List<ChatMessage>
{
    ChatMessage.CreateSystemMessage("Respond in plain text only. No markdown, no LaTeX, no formatting symbols.")
};

try
{
    await Ask("What is 32 * 50?");
    await Ask("Divide that by 4.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}

async Task Ask(string question)
{
    history.Add(ChatMessage.CreateUserMessage(question));
    Console.WriteLine($"Q: {question}");
    Console.Write("A: ");

    var sb = new StringBuilder();
    var reasoningTokens = 0;
    await foreach (var update in client.CompleteChatStreamingAsync(history))
    {
        foreach (var part in update.ContentUpdate)
        {
            Console.Write(part.Text);
            sb.Append(part.Text);
        }
        if (update.Usage is not null)
            reasoningTokens = update.Usage.OutputTokenDetails?.ReasoningTokenCount ?? 0;
    }

    Console.WriteLine($"  ({reasoningTokens} reasoning tokens)");
    Console.WriteLine();
    history.Add(ChatMessage.CreateAssistantMessage(sb.ToString()));
}
