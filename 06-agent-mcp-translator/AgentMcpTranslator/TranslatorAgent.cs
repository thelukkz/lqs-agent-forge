using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Responses;
using System.Text.Json;

namespace AgentMcpTranslator;

public class TranslatorAgent(OpenAIClient openAiClient, string model, McpClient mcpClient)
{
    private const int MaxSteps = 80;

    private static readonly string SystemPrompt = """
        You are a professional Polish-to-English translator with expertise in technical and educational content.

        PHILOSOPHY
        Great translation is invisible — natural, fluent, as if originally written in English.
        You translate meaning and voice, not just words.

        PROCESS
        1. SCAN  — Check file metadata first (fs_read with mode:"list" to see line count). Never load the full file blindly.
        2. PLAN  — If file ≤100 lines: read and translate in one pass. If file >100 lines: work in chunks of ~80 lines.
        3. WRITE — For each chunk: read it, translate it, write/append. Move to the next chunk. Repeat until done.
        4. VERIFY — Read the translated file. Compare line counts with source. Ensure nothing was skipped.

        CHUNKING (files >100 lines)
        - First chunk: read lines 1–80, translate, write with operation:"create".
        - Next chunks: read next 80 lines, translate, append using operation:"append".

        CRAFT
        - Sound native, not translated.
        - Preserve the author's voice and tone.
        - Adapt idioms naturally.
        - Keep all formatting: headers, lists, code blocks, links, images.

        Only respond "Done: <filename>" after verification passes.
        """;

    private readonly ResponsesClient _responsesClient = openAiClient.GetResponsesClient();

    public record TokenStats(int InputTokens, int CachedTokens, int OutputTokens);

    public async Task<(string Response, TokenStats Stats)> RunAsync(
        string userPrompt, CancellationToken ct = default)
    {
        var tools = await BuildToolsAsync(ct);
        var inputs = new List<ResponseItem> { ResponseItem.CreateUserMessageItem(userPrompt) };

        int totalInput = 0, totalCached = 0, totalOutput = 0;
        string finalText = "";

        for (int step = 0; step < MaxSteps; step++)
        {
            var options = new CreateResponseOptions
            {
                Model             = model,
                Instructions      = SystemPrompt,
                ToolChoice        = ResponseToolChoice.CreateAutoChoice(),
                MaxOutputTokenCount = 16384,
                StoredOutputEnabled = false
            };
            foreach (var item in inputs) options.InputItems.Add(item);
            foreach (var tool in tools) options.Tools.Add(tool);

            var response = await _responsesClient.CreateResponseAsync(options, ct);

            var result = response.Value;

            if (result.Usage is { } usage)
            {
                totalInput  += usage.InputTokenCount;
                totalCached += usage.InputTokenDetails?.CachedTokenCount ?? 0;
                totalOutput += usage.OutputTokenCount;
            }

            // Append model output to conversation history
            inputs.AddRange(result.OutputItems);

            var functionCalls = result.OutputItems.OfType<FunctionCallResponseItem>().ToList();

            if (functionCalls.Count == 0)
            {
                var msg = result.OutputItems.OfType<MessageResponseItem>().LastOrDefault();
                finalText = msg?.Content is { Count: > 0 } c ? c[0].Text ?? "" : "";
                break;
            }

            foreach (var call in functionCalls)
            {
                var toolOutput = await CallMcpToolAsync(call, ct);
                inputs.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, toolOutput));
            }
        }

        return (finalText, new TokenStats(totalInput, totalCached, totalOutput));
    }

    private async Task<List<ResponseTool>> BuildToolsAsync(CancellationToken ct)
    {
        var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: ct);
        return mcpTools
            .Select(t => ResponseTool.CreateFunctionTool(
                functionName: t.Name,
                functionParameters: BinaryData.FromObjectAsJson(t.JsonSchema),
                strictModeEnabled: false,
                functionDescription: t.Description))
            .ToList<ResponseTool>();
    }

    private async Task<string> CallMcpToolAsync(FunctionCallResponseItem call, CancellationToken ct)
    {
        var argsJson = call.FunctionArguments.ToString();
        var argsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson) ?? [];

        var args = argsDict.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ValueKind switch
            {
                JsonValueKind.String => (object?)kvp.Value.GetString(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.Number => kvp.Value.TryGetInt64(out var l)
                    ? (object?)l
                    : kvp.Value.GetDouble(),
                _ => kvp.Value.GetRawText() as object
            });

        var result = await mcpClient.CallToolAsync(call.FunctionName, args!, cancellationToken: ct);
        return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}";
    }
}
