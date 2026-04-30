using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using System.ClientModel;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>()
    .Build();

var chatApiKey = config["Chat:ApiKey"];
if (string.IsNullOrEmpty(chatApiKey))
    throw new InvalidOperationException("Chat:ApiKey not configured. Run: dotnet user-secrets set \"Chat:ApiKey\" \"<your-key>\"");

var chatClient = CreateOpenAIClient(config["Chat:BaseUrl"], chatApiKey)
    .GetChatClient(config["Chat:Model"] ?? "google/gemini-2.5-flash");

var transcribeApiKey = string.IsNullOrEmpty(config["Transcribe:ApiKey"]) ? chatApiKey : config["Transcribe:ApiKey"]!;
var transcribeClient = CreateOpenAIClient(config["Transcribe:BaseUrl"], transcribeApiKey)
    .GetChatClient(config["Transcribe:Model"] ?? "google/gemini-2.5-flash");

var ttsApiKey = string.IsNullOrEmpty(config["Tts:ApiKey"]) ? chatApiKey : config["Tts:ApiKey"]!;
var ttsClient = CreateOpenAIClient(config["Tts:BaseUrl"], ttsApiKey)
    .GetAudioClient(config["Tts:Model"] ?? "google/gemini-3.1-flash-tts-preview");
var ttsVoice = new GeneratedSpeechVoice(config["Tts:Voice"] ?? "Kore");

var silenceThresholdDb = config.GetValue<double>("Recording:SilenceThresholdDb", -40.0);
var silenceDurationMs = config.GetValue<int>("Recording:SilenceDurationMs", 1500);
var sampleRate = config.GetValue<int>("Recording:SampleRate", 16000);

var messages = new List<ChatMessage>
{
    new SystemChatMessage(
        "You are a helpful voice assistant. Keep your answers concise and conversational — they will be spoken aloud. " +
        "Respond in the same language the user speaks.")
};

Console.WriteLine("=== Agent Voice Chat ===");
Console.WriteLine($"Chat: {config["Chat:Model"]}  |  TTS: {config["Tts:Voice"]}  |  Silence: {silenceDurationMs} ms");
Console.WriteLine("Speak into the microphone. Silence for 1.5 s ends the recording. Ctrl+C to exit.");
Console.WriteLine();

while (true)
{
    Console.WriteLine("[●] Recording...");

    var tempFile = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid():N}.wav");
    try
    {
        await RecordUntilSilenceAsync(tempFile, sampleRate, silenceThresholdDb, silenceDurationMs);

        var audioBytes = await File.ReadAllBytesAsync(tempFile);
        var audioPart = ChatMessageContentPart.CreateInputAudioPart(
            BinaryData.FromBytes(audioBytes), ChatInputAudioFormat.Wav);

        string userText;
        try
        {
            var transcribeResponse = await transcribeClient.CompleteChatAsync(
            [
                new SystemChatMessage("Transcribe the audio exactly as spoken. Return only the transcribed text, nothing else."),
                new UserChatMessage(audioPart)
            ]);
            userText = transcribeResponse.Value.Content[0].Text.Trim();
        }
        catch (ClientResultException ex)
        {
            var body = ex.GetRawResponse()?.Content?.ToString();
            Console.WriteLine($"[error] Transcription failed ({ex.Status}): {body ?? ex.Message}");
            continue;
        }

        if (string.IsNullOrWhiteSpace(userText))
        {
            Console.WriteLine("[✓] You: (silence — try again)\n");
            continue;
        }

        Console.WriteLine($"[✓] You: {userText}");
        messages.Add(new UserChatMessage(userText));

        Console.Write("[…] Thinking...");
        var completion = await chatClient.CompleteChatAsync(messages);
        var aiText = completion.Value.Content[0].Text;
        messages.Add(new AssistantChatMessage(aiText));

        Console.WriteLine($"\r[✓] AI: {aiText}");
        Console.WriteLine("[♪] Playing...");

        BinaryData speechData;
        try
        {
            var speech = await ttsClient.GenerateSpeechAsync(
                aiText, ttsVoice,
                new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Pcm });
            speechData = speech.Value;
        }
        catch (ClientResultException ex)
        {
            var body = ex.GetRawResponse()?.Content?.ToString();
            Console.WriteLine($"[error] TTS failed ({ex.Status}): {body ?? ex.Message}");
            continue;
        }

        await PlayAudioAsync(speechData.ToStream());
        Console.WriteLine();
    }
    finally
    {
        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }
}

static OpenAIClient CreateOpenAIClient(string? baseUrl, string apiKey)
{
    var options = baseUrl is not null
        ? new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
        : new OpenAIClientOptions();
    return new OpenAIClient(new ApiKeyCredential(apiKey), options);
}

static async Task RecordUntilSilenceAsync(string outputPath, int sampleRate, double silenceThresholdDb, int silenceDurationMs)
{
    var format = new WaveFormat(sampleRate, 16, 1);
    using var waveIn = new WaveInEvent { WaveFormat = format };
    using var writer = new WaveFileWriter(outputPath, format);

    var silenceStart = DateTime.UtcNow;
    var hasSpeech = false;
    var tcs = new TaskCompletionSource();

    waveIn.DataAvailable += (_, e) =>
    {
        writer.Write(e.Buffer, 0, e.BytesRecorded);
        writer.Flush();

        var rms = CalculateRms(e.Buffer, e.BytesRecorded);
        var db = rms > 0 ? 20 * Math.Log10(rms) : -100.0;

        if (db > silenceThresholdDb)
        {
            hasSpeech = true;
            silenceStart = DateTime.UtcNow;
        }
        else if (hasSpeech && (DateTime.UtcNow - silenceStart).TotalMilliseconds >= silenceDurationMs)
        {
            tcs.TrySetResult();
        }
    };

    waveIn.StartRecording();
    await tcs.Task;
    waveIn.StopRecording();
}

static double CalculateRms(byte[] buffer, int bytesRecorded)
{
    double sum = 0;
    int samples = bytesRecorded / 2;
    for (int i = 0; i < bytesRecorded - 1; i += 2)
    {
        var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
        sum += sample * sample;
    }
    return samples > 0 ? Math.Sqrt(sum / samples) : 0;
}

static async Task PlayAudioAsync(Stream audioStream)
{
    // Gemini TTS returns raw PCM: 24 kHz, 16-bit, mono
    var pcmFormat = new WaveFormat(24000, 16, 1);
    using var reader = new RawSourceWaveStream(audioStream, pcmFormat);
    using var output = new WaveOutEvent();
    var tcs = new TaskCompletionSource();

    output.PlaybackStopped += (_, _) => tcs.TrySetResult();
    output.Init(reader);
    output.Play();

    await tcs.Task;
}
