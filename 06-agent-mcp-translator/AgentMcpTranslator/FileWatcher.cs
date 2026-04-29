namespace AgentMcpTranslator;

public class FileWatcher(TranslatorAgent agent, string sourceDir, string targetDir, int pollIntervalMs = 5_000)
{
    private static readonly string[] SupportedExtensions = [".md", ".txt", ".html", ".json"];

    private readonly HashSet<string> _inProgress = [];
    private int _translatedCount;

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        Console.WriteLine($"[watcher] Watching {sourceDir} — polling every {pollIntervalMs / 1000}s");

        while (!ct.IsCancellationRequested)
        {
            var pending = FindPending();

            foreach (var sourceFile in pending)
            {
                if (ct.IsCancellationRequested) break;

                var fileName = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(targetDir, fileName);

                lock (_inProgress) { _inProgress.Add(sourceFile); }

                _ = TranslateAsync(sourceFile, targetFile, fileName, ct);
            }

            try { await Task.Delay(pollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private List<string> FindPending()
    {
        var sourceFiles = Directory
            .EnumerateFiles(sourceDir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToHashSet();

        var targetFiles = Directory
            .EnumerateFiles(targetDir)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_inProgress)
        {
            return sourceFiles
                .Where(f => !targetFiles.Contains(Path.GetFileName(f)) && !_inProgress.Contains(f))
                .ToList();
        }
    }

    private async Task TranslateAsync(string sourceFile, string targetFile, string fileName, CancellationToken ct)
    {
        Console.WriteLine($"[translate] Starting: {fileName}");

        try
        {
            // Paths passed to agent are relative to workspace root (two levels up from sourceDir)
            var workspaceRoot = Path.GetFullPath(Path.Combine(sourceDir, ".."));
            var relSource = Path.GetRelativePath(workspaceRoot, sourceFile).Replace('\\', '/');
            var relTarget = Path.GetRelativePath(workspaceRoot, targetFile).Replace('\\', '/');

            var prompt = $"Translate \"{relSource}\" to English and save to \"{relTarget}\".";

            var (response, stats) = await agent.RunAsync(prompt, ct);

            Console.WriteLine($"[translate] Done: {fileName} — {stats.InputTokens} in / {stats.OutputTokens} out" +
                              (stats.CachedTokens > 0 ? $" / {stats.CachedTokens} cached" : ""));

            _translatedCount++;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[translate] Cancelled: {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[translate] Error: {fileName} — {ex.Message}");
        }
        finally
        {
            lock (_inProgress) { _inProgress.Remove(sourceFile); }
        }
    }
}
