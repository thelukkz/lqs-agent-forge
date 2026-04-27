using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PipelineNoteGrounder.Utils;

static class Cache
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    public static string HashObject(object obj) =>
        HashText(JsonSerializer.Serialize(obj));

    public static async Task<T?> ReadAsync<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    public static async Task WriteAsync<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
