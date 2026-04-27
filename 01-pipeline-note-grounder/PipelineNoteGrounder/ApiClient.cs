using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace PipelineNoteGrounder;

static class Json
{
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
}

class ApiUsage
{
    int _requests;
    long _inputTokens;
    long _outputTokens;

    public int Requests => _requests;
    public long InputTokens => _inputTokens;
    public long OutputTokens => _outputTokens;
    public long TotalTokens => _inputTokens + _outputTokens;

    internal void Add(ChatTokenUsage usage)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _inputTokens, usage.InputTokenCount);
        Interlocked.Add(ref _outputTokens, usage.OutputTokenCount);
    }
}

class ApiClient : IDisposable
{
    readonly OpenAIClient _openAI;
    readonly AppConfig _config;

    public ApiUsage Usage { get; } = new();

    public ApiClient(AppConfig config)
    {
        _config = config;
        _openAI = new OpenAIClient(
            new ApiKeyCredential(config.Ai.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(config.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(config.Pipeline.TimeoutSeconds)
            });
    }

    public async Task<T> ChatJsonAsync<T>(string model, IEnumerable<ChatMessage> messages, ChatResponseFormat format)
    {
        var client = _openAI.GetChatClient(model);
        var options = new ChatCompletionOptions { ResponseFormat = format };
        var retries = _config.Pipeline.Retries;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                var result = await client.CompleteChatAsync(messages, options);
                Usage.Add(result.Value.Usage);
                var text = result.Value.Content[0].Text;
                return JsonSerializer.Deserialize<T>(text, Json.CaseInsensitive)
                    ?? throw new InvalidOperationException("Failed to deserialize response.");
            }
            catch (ClientResultException ex) when (IsRetryable(ex.Status))
            {
                if (IsDailyLimit(ex.Message))
                    throw new InvalidOperationException(
                        $"Daily free model limit reached. Add credits or try again tomorrow.\n{ex.Message}");

                if (attempt == retries - 1) throw;

                var wait = DelaySeconds(attempt, ex.Status);
                Console.WriteLine($"  Retry {attempt + 1}/{retries} (status {ex.Status}, waiting {wait}s)...");
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
        }

        throw new InvalidOperationException("Unreachable.");
    }

    static bool IsRetryable(int status) => status is 429 or 500 or 502 or 503;

    static bool IsDailyLimit(string message) =>
        message.Contains("per-day", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("per_day", StringComparison.OrdinalIgnoreCase);

    // 429 rate-limit: 10s, 20s, 40s — other errors: 1s, 2s, 4s
    static double DelaySeconds(int attempt, int status) =>
        status == 429 ? 10 * Math.Pow(2, attempt) : Math.Pow(2, attempt);

    public void Dispose() { }
}
