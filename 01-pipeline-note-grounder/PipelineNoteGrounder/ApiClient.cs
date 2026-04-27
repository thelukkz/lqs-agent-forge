using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PipelineNoteGrounder;

class ApiClient : IDisposable
{
    readonly HttpClient _http;
    readonly AppConfig _config;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ApiClient(AppConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.Pipeline.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.Ai.ApiKey);
    }

    // Sends a Chat Completions request and returns the parsed response node.
    public async Task<JsonNode> ChatAsync(string model, List<object> messages, object? responseFormat = null)
    {
        var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
        if (responseFormat is not null)
            body["response_format"] = responseFormat;

        return await PostWithRetryAsync($"{_config.BaseUrl}/chat/completions", body);
    }

    // Sends a Chat Completions request and returns the assistant message content as string.
    public async Task<string> ChatTextAsync(string model, List<object> messages, object? responseFormat = null)
    {
        var response = await ChatAsync(model, messages, responseFormat);
        return response["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No content in response.");
    }

    // Sends a Chat Completions request and deserializes the JSON content to T.
    public async Task<T> ChatJsonAsync<T>(string model, List<object> messages, object responseFormat)
    {
        var text = await ChatTextAsync(model, messages, responseFormat);
        return JsonSerializer.Deserialize<T>(text)
            ?? throw new InvalidOperationException("Failed to deserialize response.");
    }

    async Task<JsonNode> PostWithRetryAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var retries = _config.Pipeline.Retries;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (TaskCanceledException)
            {
                if (attempt < retries - 1) { await DelayAsync(attempt); continue; }
                throw new TimeoutException($"Request timed out after {_config.Pipeline.TimeoutSeconds}s.");
            }

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return JsonNode.Parse(content)
                    ?? throw new InvalidOperationException("Empty response body.");
            }

            var errorBody = await response.Content.ReadAsStringAsync();

            if (!IsRetryable((int)response.StatusCode) || attempt == retries - 1)
                throw new HttpRequestException($"API error ({(int)response.StatusCode}): {errorBody}");

            Console.WriteLine($"  Retry {attempt + 1}/{retries} (status {(int)response.StatusCode})...");
            await DelayAsync(attempt);
        }

        throw new InvalidOperationException("Unreachable.");
    }

    static bool IsRetryable(int status) => status is 429 or 500 or 502 or 503;

    static Task DelayAsync(int attempt) => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));

    public void Dispose() => _http.Dispose();
}
