using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using OpenAI.Chat;
using PipelineNoteGrounder.Utils;

namespace PipelineNoteGrounder.Pipeline;

static class Ground
{
    static readonly ChatResponseFormat ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "grounded_paragraph",
        jsonSchema: BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "html": { "type": "string" }
              },
              "required": ["html"],
              "additionalProperties": false
            }
            """),
        jsonSchemaIsStrict: true);

    // LLM outputs data-grounding-id="N"; we replace it with properly encoded JSON after the fact.
    static readonly Regex GroundingIdRegex = new(@"data-grounding-id=""(\d+)""", RegexOptions.Compiled);

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

                Console.WriteLine($"    [{paragraph.Index + 1}/{extracted.Paragraphs.Count}] grounding {relevant.Count} item(s)...");
                var html = await GroundParagraphAsync(paragraph.Text, relevant, api, config.Ai.GroundModel);
                Console.WriteLine($"    [{paragraph.Index + 1}/{extracted.Paragraphs.Count}] done");
                return (paragraph.Index, html);
            },
            config.Pipeline.RequestDelayMs);

        var body = string.Join("\n\n", htmlParts.OrderBy(p => p.Index).Select(p => p.Item2));
        var finalHtml = ApplyTemplate(body, config);

        await Cache.WriteAsync(outputPath, new { html = finalHtml });
        await File.WriteAllTextAsync(outputPath.Replace(".json", ".html"), finalHtml);

        Console.WriteLine($"  [ground] done — {outputPath.Replace(".json", ".html")}");
    }

    static async Task<string> GroundParagraphAsync(string paragraph, List<GroundingItem> items, ApiClient api, string model)
    {
        var annotations = items.Select((item, i) =>
            $"[{i}] Canonical: {item.Canonical}\n    SurfaceForms: {string.Join(", ", item.SurfaceForms)}");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"Paragraph:\n{paragraph}\n\nGrounding items:\n{string.Join("\n", annotations)}")
        };

        var output = await api.ChatJsonAsync<GroundOutput>(model, messages, ResponseFormat);
        return InjectGrounding(output.Html, items);
    }

    // Replace data-grounding-id="N" placeholders with properly HTML-encoded JSON.
    static string InjectGrounding(string html, List<GroundingItem> items)
    {
        return GroundingIdRegex.Replace(html, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out var id) || id >= items.Count)
                return string.Empty;

            var item = items[id];
            var json = JsonSerializer.Serialize(new { summary = item.Summary, sources = item.Sources });
            return $"data-grounding=\"{HttpUtility.HtmlAttributeEncode(json)}\"";
        });
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
            "template.html");

        var template = File.Exists(templatePath)
            ? File.ReadAllText(templatePath)
            : DefaultTemplate;

        return template.Replace("<!--CONTENT-->", body);
    }

    static string OutputPath(AppConfig config) =>
        Path.Combine(Path.GetDirectoryName(config.InputFile)!, "output", "grounded.json");

    const string SystemPrompt = """
        You are an HTML annotation assistant. Convert the given paragraph into semantic HTML,
        wrapping exact surface form phrases with annotated spans.

        Each grounding item is listed as [N] with its canonical name and surface forms.
        For each item whose surface form appears in the paragraph, wrap the matching phrase using:
        <span class="grounded" data-grounding-id="N">phrase</span>

        Rules:
        - Only wrap phrases that appear verbatim in the paragraph
        - Prefer the longest matching surface form when multiple overlap
        - Wrap the paragraph in a <p> tag
        - Do not add any other HTML, attributes, or styling
        - Do not modify the text content outside of the span wrapping
        - Use only data-grounding-id with the integer N — never invent other attribute values
        """;

    const string DefaultTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Grounded Document</title>
            <style>
                *, *::before, *::after { box-sizing: border-box; }

                body {
                    font-family: Georgia, 'Times New Roman', serif;
                    font-size: 1.05rem;
                    line-height: 1.75;
                    color: #1a1a1a;
                    max-width: 760px;
                    margin: 3rem auto;
                    padding: 0 1.5rem 4rem;
                    background: #fafaf8;
                }

                p { margin: 0 0 1.2em; }

                .grounded {
                    border-bottom: 1.5px dotted #3b6fa8;
                    color: inherit;
                    cursor: help;
                    transition: background 0.1s;
                }

                .grounded:hover {
                    background: #e8f0fb;
                    border-radius: 2px;
                }

                #grounding-tooltip {
                    display: none;
                    position: fixed;
                    z-index: 9999;
                    background: #fff;
                    border: 1px solid #d4d4d4;
                    border-radius: 8px;
                    padding: 14px 16px;
                    max-width: 380px;
                    box-shadow: 0 6px 24px rgba(0, 0, 0, 0.12);
                    font-family: system-ui, -apple-system, sans-serif;
                    font-size: 0.85rem;
                    line-height: 1.55;
                    color: #1a1a1a;
                    pointer-events: none;
                }

                #grounding-tooltip.visible {
                    display: block;
                }

                .gt-summary {
                    margin-bottom: 10px;
                }

                .gt-sources {
                    border-top: 1px solid #ebebeb;
                    padding-top: 8px;
                    display: flex;
                    flex-direction: column;
                    gap: 3px;
                }

                .gt-sources a {
                    color: #2563eb;
                    text-decoration: none;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    font-size: 0.8rem;
                    pointer-events: all;
                }

                .gt-sources a:hover { text-decoration: underline; }
            </style>
        </head>
        <body>

        <!--CONTENT-->

        <div id="grounding-tooltip"></div>

        <script>
            (function () {
                const tooltip = document.getElementById('grounding-tooltip');
                let hideTimer;

                document.querySelectorAll('.grounded[data-grounding]').forEach(el => {
                    let data;
                    try { data = JSON.parse(el.getAttribute('data-grounding')); } catch { return; }
                    if (!data?.summary) return;

                    el.addEventListener('mouseenter', e => {
                        clearTimeout(hideTimer);
                        tooltip.innerHTML = build(data);
                        tooltip.classList.add('visible');
                        move(e);
                    });

                    el.addEventListener('mousemove', move);

                    el.addEventListener('mouseleave', () => {
                        hideTimer = setTimeout(() => tooltip.classList.remove('visible'), 120);
                    });
                });

                tooltip.addEventListener('mouseenter', () => clearTimeout(hideTimer));
                tooltip.addEventListener('mouseleave', () => tooltip.classList.remove('visible'));

                function build(data) {
                    let html = `<div class="gt-summary">${esc(data.summary)}</div>`;
                    if (data.sources?.length) {
                        html += '<div class="gt-sources">';
                        for (const s of data.sources) {
                            const label = s.title || s.url;
                            html += `<a href="${escAttr(s.url)}" target="_blank" rel="noopener">${esc(label)}</a>`;
                        }
                        html += '</div>';
                    }
                    return html;
                }

                function move(e) {
                    const pad = 14, tw = tooltip.offsetWidth, th = tooltip.offsetHeight;
                    let x = e.clientX + pad, y = e.clientY + pad;
                    if (x + tw > window.innerWidth  - pad) x = e.clientX - tw - pad;
                    if (y + th > window.innerHeight - pad) y = e.clientY - th - pad;
                    tooltip.style.left = x + 'px';
                    tooltip.style.top  = y + 'px';
                }

                function esc(s) {
                    return String(s)
                        .replace(/&/g, '&amp;')
                        .replace(/</g, '&lt;')
                        .replace(/>/g, '&gt;');
                }

                function escAttr(s) {
                    return String(s).replace(/"/g, '&quot;');
                }
            })();
        </script>
        </body>
        </html>
        """;

    record GroundOutput(string Html);
}
