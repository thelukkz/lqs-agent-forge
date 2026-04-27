using System.Text.RegularExpressions;

namespace PipelineNoteGrounder.Utils;

static class Markdown
{
    static readonly Regex ParagraphSplit = new(@"\n\s*\n+", RegexOptions.Compiled);
    static readonly Regex HeaderPattern  = new(@"^#{1,6}\s+", RegexOptions.Compiled);

    public static List<string> SplitParagraphs(string markdown) =>
        ParagraphSplit
            .Split(markdown.Trim())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    public static bool IsHeader(string paragraph) =>
        HeaderPattern.IsMatch(paragraph);

    public static int TargetConceptCount(string paragraph) =>
        IsHeader(paragraph) ? 1 : 5;
}
