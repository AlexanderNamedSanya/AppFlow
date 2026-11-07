using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppFlow.Core;

public sealed class ConfigurationStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim gate = new(1, 1);
    public string FilePath => path;

    public async Task<AppConfiguration> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<AppConfiguration>(file, Options)
            ?? throw new InvalidDataException("Файл настроек пуст.");
        configuration.Validate();
        return configuration;
    }

    public async Task SaveAsync(AppConfiguration configuration)
    {
        configuration.Validate();
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(configuration, Options));
            File.Move(temporary, path, true);
        }
        finally { gate.Release(); }
    }
}
