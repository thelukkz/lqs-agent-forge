using System.Text.Json;
using System.Web;
using PipelineNoteGrounder.Utils;

namespace PipelineNoteGrounder.Pipeline;

static class Ground
{
    static readonly object ResponseFormat = new
    {
        type = "json_schema",
        json_schema = new
        {
            name   = "grounded_paragraph",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    html = new { type = "string" }
                },
                required             = new[] { "html" },
                additionalProperties = false
            }
        }
    };

    public static async Task RunAsync(
        string markdown,
        ExtractResult extracted,
        DedupeResult deduped,
        SearchResult searched,
        ApiClient api,
        AppConfig config)
    {
        var outputPath = OutputPath(config);

        var groundingItems = BuildGroundingItems(extracted, deduped, searched);

        Console.WriteLine($"  [ground] processing {extracted.Paragraphs.Count} paragraphs...");

        var htmlParts = await Batch.RunAsync(
            extracted.Paragraphs,
            config.Pipeline.BatchSize,
            async paragraph =>
            {
                var relevant = groundingItems
                    .Where(g => g.SurfaceForms.Any(f =>
                        paragraph.Text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (relevant.Count == 0)
                    return (paragraph.Index, EscapeHtml(paragraph.Text));

                Console.WriteLine($"    [{paragraph.Index + 1}/{extracted.Paragraphs.Count}] {relevant.Count} grounding item(s)");
                var html = await GroundParagraphAsync(paragraph.Text, relevant, api, config.Ai.GroundModel);
                return (paragraph.Index, html);
            });

        var body = string.Join("\n\n", htmlParts.OrderBy(p => p.Index).Select(p => p.Item2));
        var finalHtml = ApplyTemplate(body, config);

        await Cache.WriteAsync(outputPath, new { html = finalHtml });
        await File.WriteAllTextAsync(outputPath.Replace(".json", ".html"), finalHtml);

        Console.WriteLine($"  [ground] done — {outputPath.Replace(".json", ".html")}");
    }

    static async Task<string> GroundParagraphAsync(string paragraph, List<GroundingItem> items, ApiClient api, string model)
    {
        var annotations = items.Select(item =>
        {
            var dataAttr = HttpUtility.HtmlAttributeEncode(JsonSerializer.Serialize(new
            {
                summary = item.Summary,
                sources = item.Sources
            }));
            return $"- Canonical: {item.Canonical}\n  SurfaceForms: {string.Join(", ", item.SurfaceForms)}\n  data-grounding: \"{dataAttr}\"";
        });

        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
            new
            {
                role    = "user",
                content = $"Paragraph:\n{paragraph}\n\nGrounding items:\n{string.Join("\n", annotations)}"
            }
        };

        var output = await api.ChatJsonAsync<GroundOutput>(model, messages, ResponseFormat);
        return output.Html;
    }

    static List<GroundingItem> BuildGroundingItems(ExtractResult extracted, DedupeResult deduped, SearchResult searched)
    {
        var conceptsByLabel = extracted.Paragraphs
            .SelectMany(p => p.Concepts)
            .GroupBy(c => c.Label)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<GroundingItem>();

        foreach (var group in deduped.Groups)
        {
            if (!searched.ResultsByCanonical.TryGetValue(group.Canonical, out var entry))
                continue;

            var allForms = group.Ids
                .SelectMany(id =>
                {
                    var label = conceptsByLabel.Values.ElementAtOrDefault(id - 1)?.Label;
                    return label is not null && conceptsByLabel.TryGetValue(label, out var c)
                        ? c.SurfaceForms
                        : [];
                })
                .Concat(group.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            items.Add(new GroundingItem
            {
                Canonical    = group.Canonical,
                SurfaceForms = allForms,
                Summary      = entry.Summary,
                Sources      = entry.Sources
            });
        }

        return items;
    }

    static string EscapeHtml(string text) =>
        $"<p>{HttpUtility.HtmlEncode(text)}</p>";

    static string ApplyTemplate(string body, AppConfig config)
    {
        var templatePath = Path.Combine(
            Path.GetDirectoryName(config.InputFile)!,
            "..",
            "template.html");

        if (!File.Exists(templatePath))
            return $"<html><body>{body}</body></html>";

        return File.ReadAllText(templatePath).Replace("<!--CONTENT-->", body);
    }

    static string OutputPath(AppConfig config) =>
        Path.Combine(Path.GetDirectoryName(config.InputFile)!, "output", "grounded.json");

    const string SystemPrompt = """
        You are an HTML annotation assistant. Convert the given paragraph into semantic HTML,
        wrapping exact surface form phrases with grounded spans.

        For each grounding item, wrap the matching phrase using:
        <span class="grounded" data-grounding="ATTR">phrase</span>

        Use the exact data-grounding value provided for each item (already HTML-encoded).

        Rules:
        - Only wrap phrases that appear verbatim in the paragraph
        - Prefer the longest matching surface form when multiple overlap
        - Wrap the paragraph in a <p> tag
        - Do not add any other HTML or styling
        - Do not modify the text content outside of the span wrapping
        """;

    record GroundOutput(string Html);
}
