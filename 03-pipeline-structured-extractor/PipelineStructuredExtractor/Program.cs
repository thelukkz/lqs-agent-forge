using System.Text.Json;
using System.Text.Json.Serialization;
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

var chatClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions)
    .GetChatClient(model);

var text = "John is 30 years old and works as a software engineer. He is skilled in JavaScript, Python, and React.";

var personSchema = BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "name":       { "type": ["string", "null"], "description": "Full name of the person" },
            "age":        { "type": ["number", "null"], "description": "Age in years" },
            "occupation": { "type": ["string", "null"], "description": "Job title or profession" },
            "skills":     { "type": "array", "items": { "type": "string" }, "description": "List of skills or technologies" }
        },
        "required": ["name", "age", "occupation", "skills"],
        "additionalProperties": false
    }
    """);

var responseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
    jsonSchemaFormatName: "person",
    jsonSchema: personSchema,
    jsonSchemaIsStrict: true);

var messages = new List<ChatMessage>
{
    new UserChatMessage($"Extract person information from: \"{text}\"")
};

var completion = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
{
    ResponseFormat = responseFormat
});

var json = completion.Value.Content[0].Text;
var person = JsonSerializer.Deserialize<PersonInfo>(json)!;

Console.WriteLine($"Name:       {person.Name ?? "unknown"}");
Console.WriteLine($"Age:        {person.Age?.ToString() ?? "unknown"}");
Console.WriteLine($"Occupation: {person.Occupation ?? "unknown"}");
Console.WriteLine($"Skills:     {string.Join(", ", person.Skills)}");

record PersonInfo(
    [property: JsonPropertyName("name")]       string?   Name,
    [property: JsonPropertyName("age")]        double?   Age,
    [property: JsonPropertyName("occupation")] string?   Occupation,
    [property: JsonPropertyName("skills")]     string[]  Skills
);
