using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPal.Core;

// Writes only app-owned files in the app data folder. Corrupt files are renamed, never deleted.
public static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path, TimeProvider time) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, Options) ?? throw new JsonException("null document");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            QuarantineCorrupt(path, time);
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    private static void QuarantineCorrupt(string path, TimeProvider time)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss");
        for (var i = 0; i < 100; i++)
        {
            var target = Path.Combine(dir, $"{name}.corrupt-{stamp}{(i == 0 ? "" : $"-{i}")}.json");
            if (File.Exists(target)) continue;
            File.Move(path, target);
            return;
        }
    }
}
