using Microsoft.Extensions.Configuration;

namespace PipelineNoteGrounder;

enum AiProvider { OpenAi, OpenRouter, LmStudio }

record AiSettings
{
    public required string Provider { get; init; }
    public required string ExtractModel { get; init; }
    public required string SearchModel { get; init; }
    public required string GroundModel { get; init; }
    public required string ApiKey { get; init; }
}

record PipelineSettings
{
    public int BatchSize { get; init; } = 5;
    public int TimeoutSeconds { get; init; } = 180;
    public int Retries { get; init; } = 3;
    public int RequestDelayMs { get; init; } = 0;
}

record AppConfig
{
    public required AiSettings Ai { get; init; }
    public required PipelineSettings Pipeline { get; init; }
    public required string InputFile { get; init; }
    public bool Force { get; init; }

    public AiProvider Provider => Ai.Provider.ToLowerInvariant() switch
    {
        "openrouter" => AiProvider.OpenRouter,
        "lmstudio"   => AiProvider.LmStudio,
        _            => AiProvider.OpenAi
    };

    public string BaseUrl => Provider switch
    {
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1",
        AiProvider.LmStudio  => "http://localhost:1234/v1",
        _                    => "https://api.openai.com/v1"
    };

    public static AppConfig Load(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var ai = configuration.GetSection("AI").Get<AiSettings>()
            ?? throw new InvalidOperationException("Missing AI configuration section.");

        var pipeline = configuration.GetSection("Pipeline").Get<PipelineSettings>()
            ?? new PipelineSettings();

        var inputFile = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new InvalidOperationException("Usage: note-grounder <path-to-file.md> [--force] [--batch=N]");

        if (!File.Exists(inputFile))
            throw new FileNotFoundException($"Input file not found: {inputFile}");

        var batchArg = args.FirstOrDefault(a => a.StartsWith("--batch="));
        if (batchArg is not null && int.TryParse(batchArg["--batch=".Length..], out var batchSize))
            pipeline = pipeline with { BatchSize = Math.Clamp(batchSize, 1, 10) };

        return new AppConfig
        {
            Ai = ai,
            Pipeline = pipeline,
            InputFile = Path.GetFullPath(inputFile),
            Force = args.Contains("--force")
        };
    }
}
