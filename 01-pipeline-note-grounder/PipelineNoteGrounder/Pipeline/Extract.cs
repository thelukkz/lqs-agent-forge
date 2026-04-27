using OpenAI.Chat;
using PipelineNoteGrounder.Utils;

namespace PipelineNoteGrounder.Pipeline;

static class Extract
{
    const int MaxHeaderConcepts = 1;
    const int MaxBodyConcepts   = 5;

    static readonly ChatResponseFormat ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "concept_extraction",
        jsonSchema: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "concepts": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "label":        { "type": "string" },
                      "category":     { "type": "string", "enum": ["claim","result","method","metric","resource","definition","term","entity","reference"] },
                      "needsSearch":  { "type": "boolean" },
                      "searchQuery":  { "type": ["string","null"] },
                      "reason":       { "type": "string" },
                      "surfaceForms": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["label","category","needsSearch","searchQuery","reason","surfaceForms"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["concepts"],
              "additionalProperties": false
            }
            """),
        jsonSchemaIsStrict: true);

    public static async Task<ExtractResult> RunAsync(string markdown, ApiClient api, AppConfig config)
    {
        var paragraphs = Markdown.SplitParagraphs(markdown);
        var sourceHash = Cache.HashText(markdown);
        var outputPath = OutputPath(config);

        var existing = await Cache.ReadAsync<ExtractResult>(outputPath);
        if (!config.Force && IsCacheValid(existing, config.Ai.ExtractModel, sourceHash))
        {
            Console.WriteLine($"  [extract] cache hit — skipping ({existing!.ConceptCount} concepts)");
            return existing!;
        }

        Console.WriteLine($"  [extract] processing {paragraphs.Count} paragraphs...");

        var results = await Batch.RunAsync(
            Enumerable.Range(0, paragraphs.Count),
            config.Pipeline.BatchSize,
            async i =>
            {
                var p = paragraphs[i];
                var isHeader = Markdown.IsHeader(p);
                Console.WriteLine($"    [{i + 1}/{paragraphs.Count}] extracting...");
                var concepts = await ExtractParagraphAsync(p, isHeader, api, config.Ai.ExtractModel);
                Console.WriteLine($"    [{i + 1}/{paragraphs.Count}] done — {concepts.Count} concept(s)");
                return new ParagraphResult
                {
                    Index    = i,
                    Hash     = Cache.HashText(p),
                    Text     = p,
                    Type     = isHeader ? "header" : "body",
                    Concepts = concepts
                };
            },
            config.Pipeline.RequestDelayMs);

        var ordered = results.OrderBy(r => r.Index).ToList();
        var allConcepts = ordered.SelectMany(r => r.Concepts).ToList();

        var result = new ExtractResult
        {
            SourceFile     = config.InputFile,
            Model          = config.Ai.ExtractModel,
            SourceHash     = sourceHash,
            ConceptsHash   = Cache.HashObject(allConcepts),
            ParagraphCount = paragraphs.Count,
            ConceptCount   = allConcepts.Count,
            Paragraphs     = ordered
        };

        await Cache.WriteAsync(outputPath, result);
        Console.WriteLine($"  [extract] done — {result.ConceptCount} concepts extracted");
        return result;
    }

    static async Task<List<Concept>> ExtractParagraphAsync(string paragraph, bool isHeader, ApiClient api, string model)
    {
        var targetCount = isHeader ? MaxHeaderConcepts : MaxBodyConcepts;
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(BuildUserPrompt(paragraph, targetCount))
        };

        var output = await api.ChatJsonAsync<ExtractOutput>(model, messages, ResponseFormat);
        return FilterConcepts(output.Concepts, paragraph, isHeader);
    }

    static List<Concept> FilterConcepts(List<Concept> concepts, string paragraph, bool isHeader)
    {
        var max = isHeader ? MaxHeaderConcepts : MaxBodyConcepts;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Concept>();

        foreach (var concept in concepts.OrderByDescending(c => c.Label.Length))
        {
            if (result.Count >= max) break;
            if (!seen.Add(concept.Label)) continue;

            var validForms = concept.SurfaceForms
                .Where(f => f.Length <= 100 && paragraph.Contains(f, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (validForms.Count == 0) continue;

            result.Add(concept with { SurfaceForms = validForms });
        }

        return result;
    }

    static bool IsCacheValid(ExtractResult? cached, string model, string sourceHash) =>
        cached is not null &&
        cached.Model == model &&
        cached.SourceHash == sourceHash;

    static string OutputPath(AppConfig config) =>
        Path.Combine(Path.GetDirectoryName(config.InputFile)!, "output", "concepts.json");

    static string BuildUserPrompt(string paragraph, int targetCount) =>
        $"Extract up to {targetCount} concept(s) from this paragraph:\n\n{paragraph}";

    const string SystemPrompt = """
        You are a concept extraction assistant. Your job is to identify key concepts in text paragraphs
        that are worth verifying or explaining with web sources.

        For each concept, provide:
        - label: a concise, descriptive name
        - category: one of claim, definition, term, entity, reference, result, method, metric, resource
        - needsSearch: true if the concept contains a verifiable fact, date, or statistic
        - searchQuery: a web search query (only when needsSearch is true, otherwise null)
        - reason: brief explanation of why this concept is worth extracting
        - surfaceForms: 1-3 exact phrases from the paragraph (3-12 words each, no markdown syntax)

        Guidelines:
        - Prioritize claims with specific facts (dates, numbers, names) for needsSearch=true
        - surfaceForms must appear verbatim in the paragraph
        - Avoid extracting vague or overly generic concepts
        """;

    record ExtractOutput(List<Concept> Concepts);
}

static class ConceptCategories
{
    public static readonly string[] All =
    [
        "claim", "result", "method", "metric", "resource",
        "definition", "term", "entity", "reference"
    ];
}
