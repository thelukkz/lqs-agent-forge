using PipelineNoteGrounder.Utils;

namespace PipelineNoteGrounder.Pipeline;

static class Search
{
    static readonly object ResponseFormat = new
    {
        type = "json_schema",
        json_schema = new
        {
            name   = "search_result",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    summary   = new { type = "string" },
                    keyPoints = new { type = "array", items = new { type = "string" } },
                    sources   = new
                    {
                        type  = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = new[] { "string", "null" } },
                                url   = new { type = "string" }
                            },
                            required             = new[] { "title", "url" },
                            additionalProperties = false
                        }
                    }
                },
                required             = new[] { "summary", "keyPoints", "sources" },
                additionalProperties = false
            }
        }
    };

    public static async Task<SearchResult> RunAsync(
        ExtractResult extracted,
        DedupeResult deduped,
        ApiClient api,
        AppConfig config)
    {
        var outputPath = OutputPath(config);

        var existing = await Cache.ReadAsync<SearchResult>(outputPath);
        if (!config.Force && IsCacheValid(existing, config.Ai.SearchModel, config.InputFile))
        {
            Console.WriteLine($"  [search] cache hit — skipping ({existing!.ResultsByCanonical.Count} results)");
            return existing!;
        }

        var canonicals = BuildCanonicals(extracted, deduped);

        if (canonicals.Count == 0)
        {
            Console.WriteLine("  [search] no concepts to search — skipping");
            var empty = new SearchResult
            {
                SourceFile        = config.InputFile,
                Model             = config.Ai.SearchModel,
                ResultsByCanonical = []
            };
            await Cache.WriteAsync(outputPath, empty);
            return empty;
        }

        var alreadySearched = existing?.ResultsByCanonical ?? [];
        var pending = canonicals
            .Where(c => !alreadySearched.ContainsKey(c.Canonical))
            .ToList();

        Console.WriteLine($"  [search] searching {pending.Count}/{canonicals.Count} concepts...");

        var newResults = await Batch.RunAsync(
            pending,
            config.Pipeline.BatchSize,
            async item =>
            {
                Console.WriteLine($"    searching: {item.Canonical}");
                return await SearchOneAsync(item, api, config.Ai.SearchModel);
            });

        var combined = new Dictionary<string, SearchEntry>(alreadySearched);
        foreach (var entry in newResults)
            combined[entry.Canonical] = entry;

        var result = new SearchResult
        {
            SourceFile         = config.InputFile,
            Model              = config.Ai.SearchModel,
            ResultsByCanonical = combined
        };

        await Cache.WriteAsync(outputPath, result);
        Console.WriteLine($"  [search] done — {result.ResultsByCanonical.Count} results");
        return result;
    }

    static async Task<SearchEntry> SearchOneAsync(CanonicalItem item, ApiClient api, string model)
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user",   content = BuildUserPrompt(item) }
        };

        var output = await api.ChatJsonAsync<SearchOutput>(model, messages, ResponseFormat);

        return new SearchEntry
        {
            Canonical = item.Canonical,
            Summary   = output.Summary,
            KeyPoints = output.KeyPoints,
            Sources   = output.Sources
        };
    }

    static List<CanonicalItem> BuildCanonicals(ExtractResult extracted, DedupeResult deduped)
    {
        var conceptsByLabel = extracted.Paragraphs
            .SelectMany(p => p.Concepts)
            .Where(c => c.NeedsSearch)
            .GroupBy(c => c.Label)
            .ToDictionary(g => g.Key, g => g.First());

        return deduped.Groups.Select(group =>
        {
            var allForms = group.Aliases
                .Concat(group.Ids
                    .Select(id => conceptsByLabel.Values.ElementAtOrDefault(id - 1)?.SurfaceForms ?? [])
                    .SelectMany(f => f))
                .Distinct()
                .ToList();

            var searchQuery = group.Ids
                .Select(id => conceptsByLabel.Values.ElementAtOrDefault(id - 1)?.SearchQuery)
                .FirstOrDefault(q => q is not null) ?? group.Canonical;

            return new CanonicalItem(group.Canonical, allForms, searchQuery);
        }).ToList();
    }

    static bool IsCacheValid(SearchResult? cached, string model, string sourceFile) =>
        cached is not null &&
        cached.Model == model &&
        cached.SourceFile == sourceFile;

    static string OutputPath(AppConfig config) =>
        Path.Combine(Path.GetDirectoryName(config.InputFile)!, "output", "search_results.json");

    static string BuildUserPrompt(CanonicalItem item) =>
        $"Research this concept and provide accurate, sourced information:\n\n**{item.Canonical}**\n\nSearch query: {item.SearchQuery}";

    const string SystemPrompt = """
        You are a research assistant. Use your knowledge to verify and expand on the given concept.
        Provide accurate, factual information with specific details.

        Requirements:
        - Write a concise summary (2-4 sentences) grounded in facts
        - Include 2-4 key points with specific details
        - List 1-3 credible sources with titles and URLs

        Focus on accuracy. If you are uncertain, say so in the summary.
        """;

    record CanonicalItem(string Canonical, List<string> SurfaceForms, string SearchQuery);

    record SearchOutput(string Summary, List<string> KeyPoints, List<SearchSource> Sources);
}
