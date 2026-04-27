using PipelineNoteGrounder.Utils;

namespace PipelineNoteGrounder.Pipeline;

static class Dedupe
{
    static readonly object ResponseFormat = new
    {
        type = "json_schema",
        json_schema = new
        {
            name   = "concept_deduplication",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    groups = new
                    {
                        type  = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                canonical  = new { type = "string" },
                                ids        = new { type = "array", items = new { type = "integer" } },
                                aliases    = new { type = "array", items = new { type = "string" } },
                                rationale  = new { type = "string" }
                            },
                            required             = new[] { "canonical", "ids", "aliases", "rationale" },
                            additionalProperties = false
                        }
                    }
                },
                required             = new[] { "groups" },
                additionalProperties = false
            }
        }
    };

    public static async Task<DedupeResult> RunAsync(ExtractResult extracted, ApiClient api, AppConfig config)
    {
        var outputPath  = OutputPath(config);
        var conceptsHash = extracted.ConceptsHash;

        var existing = await Cache.ReadAsync<DedupeResult>(outputPath);
        if (!config.Force && IsCacheValid(existing, conceptsHash))
        {
            Console.WriteLine($"  [dedupe] cache hit — skipping ({existing!.Groups.Count} groups)");
            return existing!;
        }

        var searchable = extracted.Paragraphs
            .SelectMany(p => p.Concepts)
            .Where(c => c.NeedsSearch)
            .Select((c, i) => new { Id = i + 1, c.Label, c.Category, c.SearchQuery })
            .ToList();

        if (searchable.Count == 0)
        {
            Console.WriteLine("  [dedupe] no searchable concepts — skipping");
            var empty = new DedupeResult
            {
                SourceFile  = config.InputFile,
                DedupeHash  = Cache.HashText(conceptsHash),
                Groups      = []
            };
            await Cache.WriteAsync(outputPath, empty);
            return empty;
        }

        Console.WriteLine($"  [dedupe] grouping {searchable.Count} concepts...");

        var conceptList = string.Join("\n", searchable.Select(c =>
            $"[{c.Id}] ({c.Category}) {c.Label}"));

        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user",   content = $"Group these concepts:\n\n{conceptList}" }
        };

        var output = await api.ChatJsonAsync<DedupeOutput>(config.Ai.ExtractModel, messages, ResponseFormat);

        var result = new DedupeResult
        {
            SourceFile = config.InputFile,
            DedupeHash = Cache.HashText(conceptsHash),
            Groups     = output.Groups
        };

        await Cache.WriteAsync(outputPath, result);
        Console.WriteLine($"  [dedupe] done — {result.Groups.Count} groups");
        return result;
    }

    static bool IsCacheValid(DedupeResult? cached, string conceptsHash) =>
        cached is not null &&
        cached.DedupeHash == Cache.HashText(conceptsHash);

    static string OutputPath(AppConfig config) =>
        Path.Combine(Path.GetDirectoryName(config.InputFile)!, "output", "dedupe.json");

    const string SystemPrompt = """
        You are a concept deduplication assistant. Group concepts only when they are strict
        paraphrases of the same claim or term.

        Rules:
        - Only group items with the same category
        - Do NOT group related-but-distinct ideas (cause/effect, part/whole, example vs category)
        - Each group needs a canonical name (the clearest, most complete label)
        - Every concept must appear in exactly one group
        - Single-item groups are fine when a concept is unique
        """;

    record DedupeOutput(List<ConceptGroup> Groups);
}
