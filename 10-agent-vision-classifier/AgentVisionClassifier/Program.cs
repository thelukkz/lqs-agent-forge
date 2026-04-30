using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var apiKey = config["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey not set");
var model  = config["OpenAI:Model"] ?? "openai/gpt-4o-mini";
var baseUrl = config["OpenAI:BaseUrl"];

var clientOptions = baseUrl is not null
    ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
    : new OpenAIClientOptions();

var openAiClient    = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions);
var chatClient      = openAiClient.GetChatClient(model);
var responsesClient = openAiClient.GetResponsesClient();

// ── Workspace ─────────────────────────────────────────────────────────────────

var workspaceRoot = Path.GetFullPath("workspace");
Directory.CreateDirectory(Path.Combine(workspaceRoot, "knowledge"));
Directory.CreateDirectory(Path.Combine(workspaceRoot, "images", "organized"));

// ── Tool schemas ──────────────────────────────────────────────────────────────

var tools = new List<ResponseTool>
{
    ResponseTool.CreateFunctionTool(
        functionName: "list_directory",
        functionDescription: "List files and subdirectories at a path inside the workspace.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path relative to workspace root." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),

    ResponseTool.CreateFunctionTool(
        functionName: "read_file",
        functionDescription: "Read the text content of a file inside the workspace.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "File path relative to workspace root." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),

    ResponseTool.CreateFunctionTool(
        functionName: "copy_file",
        functionDescription: "Copy a file to a new destination inside the workspace. Creates parent directories as needed.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "source":      { "type": "string", "description": "Source file path relative to workspace root." },
                "destination": { "type": "string", "description": "Destination file path relative to workspace root." }
              },
              "required": ["source", "destination"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),

    ResponseTool.CreateFunctionTool(
        functionName: "understand_image",
        functionDescription: "Analyze an image and answer a specific question about its visible content. Use targeted yes/no or descriptive questions.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "image_path": { "type": "string", "description": "Image file path relative to workspace root." },
                "question":   { "type": "string", "description": "Question to answer about the image." }
              },
              "required": ["image_path", "question"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),
};

// ── System prompt ─────────────────────────────────────────────────────────────

const string SystemPrompt = """
    You are an autonomous image classification agent.

    ## GOAL
    Classify all images in images/ into character folders based on profiles in knowledge/.
    Output to images/organized/<character>/ folders.

    ## PROCESS
    1. Use list_directory on knowledge/ to get profile filenames, then read_file each one.
    2. Use list_directory on images/ to get image filenames (ignore subdirectories).
    3. For each image, gather evidence by calling understand_image for EACH profile criterion.
    4. Decide based on the evidence — see MATCHING and ELIMINATION below.
    5. Use copy_file to place the image into images/organized/<character>/.
    6. Report a summary when every image has been processed.

    ## GATHERING EVIDENCE
    Before deciding on any image:
    - First ask: "Is there a clearly visible human face in this image?"
      If no face is visible → unclassified immediately, skip remaining questions.
    - Then ask one targeted question per criterion from every profile. Examples:
        "Does the person wear glasses?"
        "Does the person have a beard or facial hair?"
        "Does the person have short hair (roughly ear-length or shorter)?"
        "Does the person have long hair (shoulder-length or longer)?"
    - For negative criteria ("does not wear X"), ask directly ("Do they wear glasses?")
      and treat a clear NO as confirmation. Only confirm absence when the face and
      the relevant feature area are clearly visible.
    - You may call understand_image multiple times on the same image.

    ## MATCHING
    - ALL stated criteria in a profile must be satisfied — nothing more is required.
    - Positive criteria: feature must be clearly present.
    - Negative criteria: feature must be clearly absent (visible face, feature area clear).
    - Extra traits not in a profile are irrelevant.
    - If multiple profiles match → copy to ALL matching folders.

    ## ELIMINATION
    Use when direct matching fails:
    1. List all profiles.
    2. Rule out any profile whose positive criteria are clearly violated by the evidence.
    3. If exactly one profile remains and its criteria are satisfied → that is the match.
    4. If zero or more than one profile remains ambiguously → unclassified.

    ## UNCLASSIFIED
    Place in images/organized/unclassified/ when:
    - No clearly visible human face.
    - Evidence contradicts all profiles.
    - Required feature areas are too obscured to evaluate.
    - Image contains multiple distinct subjects that cannot be separated.

    ## CRITICAL
    Do NOT output any text response until every image has been copied to its folder.
    Intermediate status updates, progress reports, or "I will now..." messages are forbidden.
    The only permitted text output is the final summary after all copy_file calls are done.

    Run autonomously without asking for confirmation.
    """;

const string ClassificationQuery =
    "Classify all images in the images/ folder using the character profiles from knowledge/.";

// ── Agent loop ────────────────────────────────────────────────────────────────

Console.WriteLine("Starting classification agent...");
Console.WriteLine($"Workspace: {workspaceRoot}");
Console.WriteLine();

var inputs = new List<ResponseItem> { ResponseItem.CreateUserMessageItem(ClassificationQuery) };
int totalInput = 0, totalOutput = 0;
const int MaxSteps = 100;

for (int step = 1; step <= MaxSteps; step++)
{
    var options = new CreateResponseOptions
    {
        Model               = model,
        Instructions        = SystemPrompt,
        ToolChoice          = ResponseToolChoice.CreateAutoChoice(),
        MaxOutputTokenCount = 16384,
        StoredOutputEnabled = false,
    };
    foreach (var item in inputs)  options.InputItems.Add(item);
    foreach (var tool in tools)   options.Tools.Add(tool);

    var response = await responsesClient.CreateResponseAsync(options);
    var result   = response.Value;

    if (result.Usage is { } usage)
    {
        totalInput  += usage.InputTokenCount;
        totalOutput += usage.OutputTokenCount;
    }

    inputs.AddRange(result.OutputItems);

    var calls = result.OutputItems.OfType<FunctionCallResponseItem>().ToList();

    if (calls.Count == 0)
    {
        var msg = result.OutputItems.OfType<MessageResponseItem>().LastOrDefault();
        var text = msg?.Content is { Count: > 0 } c ? c[0].Text ?? "" : "";
        Console.WriteLine();
        Console.WriteLine("── Agent summary ────────────────────────────────────────");
        Console.WriteLine(text);
        break;
    }

    Console.WriteLine($"[step {step}] {calls.Count} tool call(s)");

    foreach (var call in calls)
    {
        Console.Write($"  → {call.FunctionName}");
        var output = await ExecuteToolAsync(call);
        Console.WriteLine();
        inputs.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, output));
    }
}

Console.WriteLine();
Console.WriteLine($"Tokens — input: {totalInput}  output: {totalOutput}");

// ── Tool handlers ─────────────────────────────────────────────────────────────

async Task<string> ExecuteToolAsync(FunctionCallResponseItem call)
{
    var args = JsonNode.Parse(call.FunctionArguments.ToString()) ?? new JsonObject();

    try
    {
        return call.FunctionName switch
        {
            "list_directory"   => ListDirectory(args["path"]!.GetValue<string>()),
            "read_file"        => ReadFile(args["path"]!.GetValue<string>()),
            "copy_file"        => CopyFile(
                                     args["source"]!.GetValue<string>(),
                                     args["destination"]!.GetValue<string>()),
            "understand_image" => await UnderstandImageAsync(
                                     args["image_path"]!.GetValue<string>(),
                                     args["question"]!.GetValue<string>()),
            _                  => Error($"Unknown tool: {call.FunctionName}"),
        };
    }
    catch (Exception ex)
    {
        return Error(ex.Message);
    }
}

string ListDirectory(string relativePath)
{
    var full = SafePath(relativePath);
    if (!Directory.Exists(full))
        return Error($"Directory not found: {relativePath}");

    var entries = Directory.GetFileSystemEntries(full)
        .Select(e => new
        {
            name = Path.GetFileName(e),
            type = Directory.Exists(e) ? "directory" : "file",
        })
        .ToArray();

    Console.Write($"  ({relativePath}: {entries.Length} entries)");
    return JsonSerializer.Serialize(entries);
}

string ReadFile(string relativePath)
{
    var full = SafePath(relativePath);
    if (!File.Exists(full))
        return Error($"File not found: {relativePath}");

    var text = File.ReadAllText(full);
    Console.Write($"  ({relativePath}: {text.Length} chars)");
    return JsonSerializer.Serialize(new { path = relativePath, content = text });
}

string CopyFile(string source, string destination)
{
    var srcFull  = SafePath(source);
    var dstFull  = SafePath(destination);

    if (!File.Exists(srcFull))
        return Error($"Source not found: {source}");

    Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
    File.Copy(srcFull, dstFull, overwrite: true);

    Console.Write($"  ({source} → {destination})");
    return JsonSerializer.Serialize(new { success = true, source, destination });
}

async Task<string> UnderstandImageAsync(string relativePath, string question)
{
    var full = SafePath(relativePath);
    if (!File.Exists(full))
        return Error($"Image not found: {relativePath}");

    var imageBytes = await File.ReadAllBytesAsync(full);
    var mimeType   = Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".png"  => "image/png",
        ".gif"  => "image/gif",
        ".webp" => "image/webp",
        _       => "image/jpeg",
    };

    var visionResponse = await chatClient.CompleteChatAsync(
    [
        new UserChatMessage(
            ChatMessageContentPart.CreateTextPart(question),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), mimeType))
    ]);

    var answer = visionResponse.Value.Content[0].Text;
    Console.Write($"  ({Path.GetFileName(relativePath)}: \"{question[..Math.Min(40, question.Length)]}...\")");
    return JsonSerializer.Serialize(new { image = relativePath, question, answer });
}

// ── Utilities ─────────────────────────────────────────────────────────────────

string SafePath(string relativePath)
{
    var full = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Path traversal blocked");
    return full;
}

static string Error(string message) =>
    JsonSerializer.Serialize(new { error = message });
