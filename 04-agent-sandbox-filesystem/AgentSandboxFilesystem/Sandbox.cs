namespace AgentSandboxFilesystem;

static class Sandbox
{
    public static string Root { get; private set; } = "";

    public static void Initialize(string root)
    {
        Directory.CreateDirectory(root);
        Root = Path.GetFullPath(root);
    }

    public static string ResolvePath(string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootWithSep = Root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSep) && resolved != Root)
            throw new UnauthorizedAccessException($"Access denied: \"{relativePath}\" is outside the sandbox");

        return resolved;
    }
}
