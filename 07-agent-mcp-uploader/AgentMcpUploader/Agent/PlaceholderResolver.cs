using System.Text.RegularExpressions;

namespace AgentMcpUploader.Agent;

static partial class PlaceholderResolver
{
    [GeneratedRegex(@"\{\{file:([^}]+)\}\}")]
    private static partial Regex FileRef();

    // Replaces {{file:relative/path}} with the base64-encoded content of that file.
    // The model uses this syntax instead of reading files explicitly, keeping the
    // conversation history free of large base64 blobs.
    public static string Resolve(string json, string sourceRoot) =>
        FileRef().Replace(json, match =>
        {
            var relative = match.Groups[1].Value;
            var full = Path.GetFullPath(Path.Combine(sourceRoot, relative));

            if (!full.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return match.Value;

            return Convert.ToBase64String(File.ReadAllBytes(full));
        });
}
