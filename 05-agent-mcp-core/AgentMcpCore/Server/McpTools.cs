using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace AgentMcpCore.Server;

[McpServerToolType]
public class McpTools
{
    [McpServerTool(Name = "calculate")]
    [Description("Performs basic arithmetic: add, subtract, multiply, divide")]
    public static string Calculate(
        [Description("Operation: add, subtract, multiply, divide")] string operation,
        [Description("First number")] double a,
        [Description("Second number")] double b)
    {
        return operation.ToLowerInvariant() switch
        {
            "add"      => JsonSerializer.Serialize(new { result = a + b }),
            "subtract" => JsonSerializer.Serialize(new { result = a - b }),
            "multiply" => JsonSerializer.Serialize(new { result = a * b }),
            "divide" when b == 0 => JsonSerializer.Serialize(new { error = "Division by zero" }),
            "divide"   => JsonSerializer.Serialize(new { result = a / b }),
            _ => JsonSerializer.Serialize(new { error = $"Unknown operation: {operation}" })
        };
    }

    [McpServerTool(Name = "summarize_with_confirmation")]
    [Description("Summarizes text using LLM sampling, with user confirmation via elicitation")]
    public static async Task<string> SummarizeWithConfirmation(
        McpServer server,
        [Description("Text to summarize")] string text,
        [Description("Maximum summary length in words")] int maxLength = 100,
        CancellationToken cancellationToken = default)
    {
        // Step 1: ask user for confirmation via elicitation
        var elicitResult = await server.ElicitAsync(new ElicitRequestParams
        {
            Mode = "form",
            Message = $"Summarize the following text in at most {maxLength} words?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema
                    {
                        Title   = "Confirm summarization",
                        Default = true
                    }
                },
                Required = ["confirm"]
            }
        }, cancellationToken);

        if (!elicitResult.IsAccepted)
            return "Summarization cancelled.";

        // Step 2: request LLM summary from the client via sampling
        var sampleResult = await server.SampleAsync(new CreateMessageRequestParams
        {
            Messages =
            [
                new SamplingMessage
                {
                    Role    = Role.User,
                    Content = [new TextContentBlock { Text = $"Summarize in at most {maxLength} words:\n\n{text}" }]
                }
            ],
            MaxTokens = 512
        }, cancellationToken);

        return sampleResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? "Summary unavailable.";
    }
}
