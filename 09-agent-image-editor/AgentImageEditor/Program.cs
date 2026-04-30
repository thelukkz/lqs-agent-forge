using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var apiKey = config["OpenAI:ApiKey"];
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OpenAI:ApiKey not set — use 'dotnet user-secrets set OpenAI:ApiKey <key>'");

var model      = config["OpenAI:Model"]      ?? "google/gemini-2.5-flash";
var imageModel = config["OpenAI:ImageModel"] ?? "google/gemini-3.1-flash-image-preview";
var baseUrl    = config["OpenAI:BaseUrl"]    ?? "https://openrouter.ai/api/v1";

var workspaceRoot = Path.Combine(Directory.GetCurrentDirectory(), "workspace");
var inputDir      = Path.Combine(workspaceRoot, "input");
var outputDir     = Path.Combine(workspaceRoot, "output");
Directory.CreateDirectory(inputDir);
Directory.CreateDirectory(outputDir);

var styleGuidePath = Path.Combine(workspaceRoot, "style-guide.md");
var styleGuide = File.Exists(styleGuidePath) ? await File.ReadAllTextAsync(styleGuidePath) : null;

// Chat client (agent loop + vision analysis) via OpenAI SDK → OpenRouter
var chatClient = new OpenAIClient(
        new System.ClientModel.ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
    .GetChatClient(model);

// HTTP client for image generation (OpenRouter /chat/completions with modalities)
var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Authorization =
    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

// ── Tool schemas ──────────────────────────────────────────────────────────────

var tools = new List<ChatTool>
{
    ChatTool.CreateFunctionTool(
        "create_image",
        "Generate a new image or edit an existing one. For edits, supply reference_image as a workspace-relative path.",
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "prompt":          { "type": "string", "description": "Detailed image generation prompt" },
            "output_name":     { "type": "string", "description": "Output filename without extension, e.g. 'robot_sketch'" },
            "reference_image": { "type": "string", "description": "Optional relative path to a source image for editing, e.g. 'workspace/input/photo.jpg'" }
          },
          "required": ["prompt", "output_name"],
          "additionalProperties": false
        }
        """)
    ),
    ChatTool.CreateFunctionTool(
        "analyze_image",
        "Analyze a generated image for quality. Returns verdict (accept/retry), a score 1–10, and any blocking issues.",
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "image_path":      { "type": "string", "description": "Workspace-relative path to the image, e.g. 'workspace/output/robot_sketch_123.png'" },
            "original_prompt": { "type": "string", "description": "The prompt that was used to generate the image" }
          },
          "required": ["image_path", "original_prompt"],
          "additionalProperties": false
        }
        """)
    )
};

var systemPrompt = $"""
You are an image generation and editing assistant.
{(styleGuide is not null ? $"\n<style_guide>\n{styleGuide}\n</style_guide>\n" : "")}
<workflow>
1. For edit requests, identify the exact filename in workspace/input — ask the user if unclear.
2. Call create_image to generate or edit the image.
3. Call analyze_image on the result.
4. If the verdict is 'retry', address the blocking issues and retry with an improved prompt — maximum two retries.
5. Stop when the verdict is 'accept' or retries are exhausted. Report the final output path to the user.
</workflow>

<quality_bar>
Retry only for blocking problems: wrong subject, broken composition, severe artifacts, unreadable required text, or clear style violations. Minor polish does not warrant a retry.
</quality_bar>
""";

var history = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
var completionOptions = new ChatCompletionOptions();
foreach (var t in tools) completionOptions.Tools.Add(t);

Console.WriteLine("=== Image Editor Agent ===");
Console.WriteLine($"  chat/vision : {model}");
Console.WriteLine($"  image gen   : {imageModel}");
Console.WriteLine("Commands: 'clear' to reset conversation, 'exit' to quit.");
Console.WriteLine();

// ── REPL ──────────────────────────────────────────────────────────────────────

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();

    if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        history = [new SystemChatMessage(systemPrompt)];
        Console.WriteLine("[history cleared]");
        continue;
    }

    if (string.IsNullOrWhiteSpace(input))
        continue;

    history.Add(new UserChatMessage(input));
    await RunAgentLoopAsync();
    Console.WriteLine();
}

// ── Agent loop ────────────────────────────────────────────────────────────────

async Task RunAgentLoopAsync()
{
    for (int step = 0; step < 20; step++)
    {
        var response = await chatClient.CompleteChatAsync(history, completionOptions);
        var completion = response.Value;
        history.Add(new AssistantChatMessage(completion));

        if (completion.FinishReason == ChatFinishReason.Stop)
        {
            if (completion.Content.Count > 0)
                Console.WriteLine("\n" + completion.Content[0].Text);
            return;
        }

        if (completion.FinishReason != ChatFinishReason.ToolCalls)
            return;

        foreach (var call in completion.ToolCalls)
        {
            var result = await DispatchToolAsync(call.FunctionName, call.FunctionArguments.ToString());
            history.Add(new ToolChatMessage(call.Id, result));
        }
    }
}

// ── Tool dispatch ─────────────────────────────────────────────────────────────

async Task<string> DispatchToolAsync(string toolName, string argsJson)
{
    var args = JsonNode.Parse(argsJson)!;

    return toolName switch
    {
        "create_image" => await CreateImageAsync(
            args["prompt"]!.GetValue<string>(),
            args["output_name"]!.GetValue<string>(),
            args["reference_image"]?.GetValue<string>()),

        "analyze_image" => await AnalyzeImageAsync(
            args["image_path"]!.GetValue<string>(),
            args["original_prompt"]!.GetValue<string>()),

        _ => Err($"Unknown tool: {toolName}")
    };
}

// ── Tool: create_image ────────────────────────────────────────────────────────
//
// Uses OpenRouter /chat/completions with modalities:["image","text"].
// The response carries the generated image as a data URL in either:
//   choices[0].message.images[0].image_url.url  (Gemini native format)
//   choices[0].message.content[n].image_url.url (standard content-array format)

async Task<string> CreateImageAsync(string prompt, string outputName, string? referenceImageRelPath)
{
    Console.WriteLine($"  ⚡ create_image: {prompt[..Math.Min(70, prompt.Length)]}…");

    try
    {
        // Build content parts: text prompt + optional reference image as data URL
        var contentParts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = prompt }
        };

        if (referenceImageRelPath is not null)
        {
            var absRef = ToAbsPath(referenceImageRelPath);
            if (File.Exists(absRef))
            {
                var refBytes = await File.ReadAllBytesAsync(absRef);
                var dataUrl  = $"data:{MimeType(absRef)};base64,{Convert.ToBase64String(refBytes)}";
                contentParts.Add(new JsonObject
                {
                    ["type"]      = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl }
                });
                Console.WriteLine($"  ⚡ reference: {Path.GetFileName(absRef)}");
            }
        }

        var requestBody = new JsonObject
        {
            ["model"]    = imageModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = contentParts }
            },
            ["modalities"] = new JsonArray { "image", "text" }
        };

        using var httpResponse = await httpClient.PostAsync(
            $"{baseUrl}/chat/completions",
            new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        var raw = await httpResponse.Content.ReadAsStringAsync();

        if (!httpResponse.IsSuccessStatusCode)
            return Err($"OpenRouter {(int)httpResponse.StatusCode}: {raw[..Math.Min(400, raw.Length)]}");

        var message = JsonNode.Parse(raw)?["choices"]?[0]?["message"];

        // Try Gemini native format: message.images[0].image_url.url
        var imageDataUrl = message?["images"]?[0]?["image_url"]?["url"]?.GetValue<string>();

        // Fall back to standard content-array format
        if (imageDataUrl is null)
        {
            var contentArr = message?["content"]?.AsArray();
            imageDataUrl = contentArr?
                .FirstOrDefault(p => p?["type"]?.GetValue<string>() is "image_url")
                ?["image_url"]?["url"]?.GetValue<string>();
        }

        if (imageDataUrl is null)
            return Err($"No image in response: {raw[..Math.Min(400, raw.Length)]}");

        // Parse "data:<mimeType>;base64,<data>"
        var comma    = imageDataUrl.IndexOf(',');
        var mimeType = imageDataUrl[5..imageDataUrl.IndexOf(';')]; // strip "data:" prefix
        var bytes    = Convert.FromBase64String(imageDataUrl[(comma + 1)..]);

        var ext      = mimeType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
        var filename = $"{outputName}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(outputDir, filename), bytes);

        var relPath = $"workspace/output/{filename}";
        Console.WriteLine($"  ✓ saved → {relPath}");

        return JsonSerializer.Serialize(new
        {
            success    = true,
            output_path = relPath,
            absolute_path = Path.Combine(outputDir, filename),
            mime_type  = mimeType,
            mode       = referenceImageRelPath is not null ? "edit" : "generate"
        });
    }
    catch (Exception ex)
    {
        return Err(ex.Message);
    }
}

// ── Tool: analyze_image ───────────────────────────────────────────────────────
//
// Standalone vision call — not part of the main conversation history.
// The same chat model (gemini-2.5-flash) handles both text and image input.

async Task<string> AnalyzeImageAsync(string imageRelPath, string originalPrompt)
{
    Console.WriteLine($"  👁 analyze_image: {imageRelPath}");

    try
    {
        var absPath = ToAbsPath(imageRelPath);
        if (!File.Exists(absPath))
            return Err($"File not found: {imageRelPath}");

        var imageBytes = await File.ReadAllBytesAsync(absPath);

        var analysisPrompt = $"""
            Analyze this image against the prompt: "{originalPrompt}"

            Evaluate:
            - prompt_adherence : Does the image match the requested subject and composition?
            - visual_artifacts : Glitches, distortions, or unnatural elements?
            - anatomy          : Correct human/animal figures if present?
            - style_consistency: Coherent visual style?
            - composition      : Balanced and intentional layout?

            Respond in this exact format:
            VERDICT: [ACCEPT or RETRY]
            SCORE: [1-10]
            BLOCKING_ISSUES:
            - [issue or "none"]
            MINOR_ISSUES:
            - [issue or "none"]
            NEXT_PROMPT_HINT:
            - [hint or "none" if ACCEPT]

            Use ACCEPT when the main subject and style essentials are satisfied (minor polish is OK).
            Use RETRY only for: wrong subject, broken composition, severe artifacts, unreadable required text.
            """;

        var visionResponse = await chatClient.CompleteChatAsync(
        [
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(analysisPrompt),
                ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(imageBytes), MimeType(absPath)))
        ]);

        var analysisText = visionResponse.Value.Content[0].Text;

        var verdictMatch = Regex.Match(analysisText, @"VERDICT:\s*(ACCEPT|RETRY)", RegexOptions.IgnoreCase);
        var verdict = verdictMatch.Success ? verdictMatch.Groups[1].Value.ToLower() : "accept";

        var scoreMatch = Regex.Match(analysisText, @"SCORE:\s*(\d+)");
        var score = scoreMatch.Success ? int.Parse(scoreMatch.Groups[1].Value) : 7;

        Console.WriteLine($"  ✓ verdict={verdict.ToUpper()} score={score}/10");

        return JsonSerializer.Serialize(new
        {
            success    = true,
            image_path = imageRelPath,
            verdict,
            score,
            analysis   = analysisText
        });
    }
    catch (Exception ex)
    {
        return Err(ex.Message);
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

string ToAbsPath(string path) =>
    Path.IsPathRooted(path)
        ? path
        : Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar));

static string MimeType(string filePath) => Path.GetExtension(filePath).ToLower() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png"            => "image/png",
    ".gif"            => "image/gif",
    ".webp"           => "image/webp",
    _                 => "image/jpeg"
};

static string Err(string message) => JsonSerializer.Serialize(new { error = message });
