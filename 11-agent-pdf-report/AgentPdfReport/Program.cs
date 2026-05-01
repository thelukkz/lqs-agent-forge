using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Responses;
using PuppeteerSharp;
using PuppeteerSharp.Media;

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

var responsesClient = new OpenAIClient(
    new System.ClientModel.ApiKeyCredential(apiKey), clientOptions)
    .GetResponsesClient();

// ── Workspace ─────────────────────────────────────────────────────────────────

var workspaceRoot = Path.GetFullPath("workspace");
Directory.CreateDirectory(Path.Combine(workspaceRoot, "html"));
Directory.CreateDirectory(Path.Combine(workspaceRoot, "output"));

// ── Tool schemas ──────────────────────────────────────────────────────────────

var tools = new List<ResponseTool>
{
    ResponseTool.CreateFunctionTool(
        functionName: "read_file",
        functionDescription: "Read the full text content of a file inside the workspace.",
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
        functionName: "write_file",
        functionDescription: "Write text content to a file inside the workspace. Creates parent directories as needed. Use this to create the HTML document.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "path":    { "type": "string", "description": "File path relative to workspace root (e.g. 'html/report.html')." },
                "content": { "type": "string", "description": "Full text content to write." }
              },
              "required": ["path", "content"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),

    ResponseTool.CreateFunctionTool(
        functionName: "list_directory",
        functionDescription: "List files and subdirectories at a path inside the workspace.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Directory path relative to workspace root. Use '.' for the workspace root." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: true),

    ResponseTool.CreateFunctionTool(
        functionName: "html_to_pdf",
        functionDescription: "Convert an HTML file to a PDF using headless Chromium. Always set print_background to true for dark-themed documents.",
        functionParameters: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "html_path":        { "type": "string",  "description": "HTML file path relative to workspace root (e.g. 'html/report.html')." },
                "output_name":      { "type": "string",  "description": "Base name for the output PDF, without extension (e.g. 'my_report')." },
                "format":           { "type": "string",  "enum": ["A4", "Letter"], "description": "Paper format. Defaults to A4." },
                "landscape":        { "type": "boolean", "description": "Landscape orientation. Defaults to false." },
                "print_background": { "type": "boolean", "description": "Print background colors and images. Must be true for dark-themed documents." }
              },
              "required": ["html_path", "output_name"],
              "additionalProperties": false
            }
            """),
        strictModeEnabled: false),
};

// ── System instructions ───────────────────────────────────────────────────────

const string Instructions = """
    You are an autonomous PDF report generation agent with tools to read files, write files, and convert HTML to PDF.

    ## GOAL
    Create professional, print-ready PDF documents based on the user's request.

    ## MANDATORY WORKFLOW — execute these steps using your tools, in order
    Step 1. Call read_file("template.html") — contains the complete CSS for documents.
    Step 2. Call read_file("style-guide.md") — documents all available components.
    Step 3. Choose a descriptive document name in snake_case (e.g. solid_principles, team_overview).
    Step 4. Call write_file("html/{name}.html") with:
            - The ENTIRE <head> from template.html copied verbatim (do NOT recreate from memory).
            - A new <body> with page content wrapped in <div class="page"> divs.
            - .page-header and .page-footer on every content page.
    Step 5. Call html_to_pdf(html_path="html/{name}.html", output_name="{name}", print_background=true).
    Step 6. Report the path to the generated PDF.

    ## HTML RULES
    - Never edit template.html — always write to html/{name}.html.
    - Copy <head> verbatim; never guess CSS class names.
    - Cover page: no .page-header, has .page-footer with page "1".
    - Content pages: both .page-header (title + section) and .page-footer (title + page number).
    - Use only: card, table, note, tag, two-col, three-col, figure.

    ## CONTENT PRINCIPLES
    - "If it doesn't clarify, it clutters."
    - Tables over bullet lists when data has structure. Each page: one clear focus.

    ## CRITICAL
    - Start immediately by calling read_file — never output text before the PDF is ready.
    - The files exist in the workspace; call read_file to access them. Never claim they are missing.
    - Always set print_background: true in html_to_pdf.
    """;

// ── REPL ──────────────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════╗");
Console.WriteLine("║   Agent PDF Report           ║");
Console.WriteLine("╚══════════════════════════════╝");
Console.WriteLine($"Model:     {model}");
Console.WriteLine($"Workspace: {workspaceRoot}");
Console.WriteLine("Commands:  clear | exit");
Console.WriteLine();

var conversationInputs  = new List<ResponseItem>();
int totalInputTokens    = 0;
int totalOutputTokens   = 0;
bool puppeteerReady     = false;

while (true)
{
    Console.Write("> ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput)) continue;
    if (userInput.Equals("exit",  StringComparison.OrdinalIgnoreCase)) break;

    if (userInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        conversationInputs.Clear();
        Console.WriteLine("Conversation cleared.");
        continue;
    }

    conversationInputs.Add(ResponseItem.CreateUserMessageItem(userInput));
    Console.WriteLine();

    var reply = await RunAgentAsync();

    Console.WriteLine();
    Console.WriteLine(reply);
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine($"Tokens — input: {totalInputTokens}  output: {totalOutputTokens}");

// ── Agent loop ────────────────────────────────────────────────────────────────

async Task<string> RunAgentAsync()
{
    const int MaxSteps = 50;

    for (int step = 1; step <= MaxSteps; step++)
    {
        var options = new CreateResponseOptions
        {
            Model               = model,
            Instructions        = Instructions,
            ToolChoice          = ResponseToolChoice.CreateAutoChoice(),
            MaxOutputTokenCount = 16384,
            StoredOutputEnabled = false,
        };
        foreach (var item in conversationInputs) options.InputItems.Add(item);
        foreach (var tool in tools)              options.Tools.Add(tool);

        var response = await responsesClient.CreateResponseAsync(options);
        var result   = response.Value;

        if (result.Usage is { } usage)
        {
            totalInputTokens  += usage.InputTokenCount;
            totalOutputTokens += usage.OutputTokenCount;
        }

        conversationInputs.AddRange(result.OutputItems);

        var calls = result.OutputItems.OfType<FunctionCallResponseItem>().ToList();

        if (calls.Count == 0)
        {
            var msg = result.OutputItems.OfType<MessageResponseItem>().LastOrDefault();
            return msg?.Content is { Count: > 0 } c ? c[0].Text ?? "" : "(no response)";
        }

        Console.WriteLine($"[step {step}] {calls.Count} tool call(s)");

        foreach (var call in calls)
        {
            Console.Write($"  → {call.FunctionName}");
            var output = await ExecuteToolAsync(call);
            Console.WriteLine();
            conversationInputs.Add(ResponseItem.CreateFunctionCallOutputItem(call.CallId, output));
        }
    }

    return "Error: agent exceeded maximum step limit.";
}

// ── Tool handlers ─────────────────────────────────────────────────────────────

async Task<string> ExecuteToolAsync(FunctionCallResponseItem call)
{
    var args = JsonNode.Parse(call.FunctionArguments.ToString()) ?? new JsonObject();

    try
    {
        return call.FunctionName switch
        {
            "read_file"      => ReadFile(args["path"]!.GetValue<string>()),
            "write_file"     => WriteFile(args["path"]!.GetValue<string>(), args["content"]!.GetValue<string>()),
            "list_directory" => ListDirectory(args["path"]!.GetValue<string>()),
            "html_to_pdf"    => await HtmlToPdfAsync(args),
            _                => Error($"Unknown tool: {call.FunctionName}"),
        };
    }
    catch (Exception ex)
    {
        return Error(ex.Message);
    }
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

string WriteFile(string relativePath, string content)
{
    var full = SafePath(relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, content);
    Console.Write($"  ({relativePath}: {content.Length} chars written)");
    return JsonSerializer.Serialize(new { success = true, path = relativePath, bytes_written = content.Length });
}

string ListDirectory(string relativePath)
{
    var full = SafePath(relativePath);
    if (!Directory.Exists(full))
        return Error($"Directory not found: {relativePath}");

    var entries = Directory.GetFileSystemEntries(full)
        .Select(e => new { name = Path.GetFileName(e), type = Directory.Exists(e) ? "directory" : "file" })
        .ToArray();

    Console.Write($"  ({relativePath}: {entries.Length} entries)");
    return JsonSerializer.Serialize(entries);
}

async Task<string> HtmlToPdfAsync(JsonNode args)
{
    var htmlPath   = args["html_path"]!.GetValue<string>();
    var outputName = args["output_name"]!.GetValue<string>();
    var format     = args["format"]?.GetValue<string>() ?? "A4";
    var landscape  = args["landscape"]?.GetValue<bool>() ?? false;
    var printBg    = args["print_background"]?.GetValue<bool>() ?? true;

    var htmlFull = SafePath(htmlPath);
    if (!File.Exists(htmlFull))
        return Error($"HTML file not found: {htmlPath}");

    var outputDir = Path.Combine(workspaceRoot, "output");
    Directory.CreateDirectory(outputDir);
    var pdfFull = Path.Combine(outputDir, $"{outputName}.pdf");

    if (!puppeteerReady)
    {
        Console.Write("  (downloading Chromium — first run only...)");
        var fetcher = new BrowserFetcher();
        await fetcher.DownloadAsync();
        puppeteerReady = true;
        Console.WriteLine(" done.");
        Console.Write($"  → html_to_pdf");
    }

    Console.Write($"  ({htmlPath} → output/{outputName}.pdf)");

    await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });
    await using var page    = await browser.NewPageAsync();

    var fileUri = new Uri(htmlFull).AbsoluteUri;
    await page.GoToAsync(fileUri, new NavigationOptions
    {
        WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
        Timeout   = 60_000,
    });

    await page.PdfAsync(pdfFull, new PdfOptions
    {
        Format          = format == "Letter" ? PaperFormat.Letter : PaperFormat.A4,
        Landscape       = landscape,
        PrintBackground = printBg,
        MarginOptions   = new MarginOptions { Top = "0", Right = "0", Bottom = "0", Left = "0" },
    });

    return JsonSerializer.Serialize(new
    {
        success       = true,
        output_path   = $"workspace/output/{outputName}.pdf",
        absolute_path = pdfFull,
    });
}

// ── Utilities ─────────────────────────────────────────────────────────────────

string SafePath(string relativePath)
{
    var full = Path.GetFullPath(
        Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Path traversal blocked");
    return full;
}

static string Error(string message) =>
    JsonSerializer.Serialize(new { error = message });
