using PipelineNoteGrounder;
using PipelineNoteGrounder.Pipeline;

try
{
    var config = AppConfig.Load(args);

    Console.WriteLine($"note-grounder");
    Console.WriteLine($"  input:    {config.InputFile}");
    Console.WriteLine($"  provider: {config.Provider} ({config.BaseUrl})");
    Console.WriteLine($"  batch:    {config.Pipeline.BatchSize}");
    Console.WriteLine();

    using var api = new ApiClient(config);
    var markdown = await File.ReadAllTextAsync(config.InputFile);

    Console.WriteLine("[1/4] Extracting concepts...");
    var concepts = await Extract.RunAsync(markdown, api, config);

    Console.WriteLine("[2/4] Deduplicating concepts...");
    var deduped = await Dedupe.RunAsync(concepts, api, config);

    Console.WriteLine("[3/4] Searching concepts...");
    var searched = await Search.RunAsync(concepts, deduped, api, config);

    Console.WriteLine("[4/4] Grounding HTML...");
    await Ground.RunAsync(markdown, concepts, deduped, searched, api, config);

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine();
    Console.WriteLine($"  requests:       {api.Usage.Requests}");
    Console.WriteLine($"  tokens in:      {api.Usage.InputTokens:N0}");
    Console.WriteLine($"  tokens out:     {api.Usage.OutputTokens:N0}");
    Console.WriteLine($"  tokens total:   {api.Usage.TotalTokens:N0}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}
