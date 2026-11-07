using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppFlow.Desktop.Services;

public enum ExitMethod { MenuCommand, Automation }
public sealed record ExitElement(string Name, string AutomationId, int ControlType);
public sealed record ExitWindow(string ClassName, string Title);
public sealed record ExitControl(string ClassName, string Text, int Id);
public sealed record ExitRecipe
{
    public ExitMethod Method { get; init; }
    public ExitWindow Window { get; init; } = new("", "");
    public uint CommandId { get; init; }
    public string Label { get; init; } = "";
    public List<ExitElement> Elements { get; init; } = [];
    public ExitElement? TrayIcon { get; init; }
    public ExitElement? TrayOpener { get; init; }
    public ExitControl? Control { get; init; }
}
public sealed record CloseProfile
{
    public string Executable { get; init; } = "";
    public string FileHash { get; init; } = "";
    public ExitRecipe Recipe { get; init; } = new();
    public bool BackgroundVerified { get; init; }
    public bool VisibleVerified { get; init; }
    public bool DirectVerified { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
    public int ManualExitCode { get; init; }
}

// Separate from automation rules: learning one executable applies to all its links.
public sealed class CloseProfileStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<CloseProfile>? profiles;
    public string FilePath => path;

    public async Task<CloseProfile?> FindAsync(string executable, CancellationToken ct = default)
        => (await FindAllAsync(executable, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<CloseProfile>> FindAllAsync(string executable, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var found = profiles!.Where(p => string.Equals(p.Executable, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 0) return [];
            var hash = await FingerprintAsync(executable, ct);
            return found.Where(p => p.FileHash == hash).OrderByDescending(p => p.DirectVerified).ThenByDescending(p => p.VerifiedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    public Task SaveAsync(CloseProfile profile, CancellationToken ct = default) => SaveManyAsync([profile], ct);

    public async Task SaveManyAsync(IReadOnlyCollection<CloseProfile> additions, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var incoming = additions.ToArray();
        if (incoming.Length == 0) return;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in incoming)
        {
            Validate(profile);
            if ((!profile.BackgroundVerified && !profile.VisibleVerified) || profile.VerifiedAt == default)
                throw new InvalidDataException("Сначала проверьте записанный способ закрытия.");
            if (!hashes.TryGetValue(profile.Executable, out var hash))
                hashes[profile.Executable] = hash = await FingerprintAsync(profile.Executable, ct);
            if (profile.FileHash != hash)
                throw new InvalidDataException("Программа изменилась после обучения. Повторите обучение.");
        }
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var next = profiles!.ToList();
            foreach (var profile in incoming)
            {
                var recipe = JsonSerializer.Serialize(profile.Recipe, Options);
                bool SameRecipe(CloseProfile existing) => existing.FileHash == profile.FileHash
                    && JsonSerializer.Serialize(existing.Recipe, Options) == recipe;
                var previous = next.Where(p => string.Equals(p.Executable, profile.Executable, StringComparison.OrdinalIgnoreCase) && SameRecipe(p)).ToArray();
                next.RemoveAll(p => string.Equals(p.Executable, profile.Executable, StringComparison.OrdinalIgnoreCase)
                    && (p.FileHash != profile.FileHash || SameRecipe(p)));
                next.Add(profile with
                {
                    BackgroundVerified = profile.BackgroundVerified || previous.Any(p => p.BackgroundVerified),
                    DirectVerified = profile.DirectVerified || previous.Any(p => p.DirectVerified),
                    VisibleVerified = profile.VisibleVerified || previous.Any(p => p.VisibleVerified)
                });
            }
            await WriteAsync(next, ct);
            profiles = next;
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string executable, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            var next = profiles!.Where(p => !string.Equals(p.Executable, executable, StringComparison.OrdinalIgnoreCase)).ToList();
            await WriteAsync(next, ct); profiles = next;
        }
        finally { gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (profiles is not null) return;
        if (!File.Exists(path)) { profiles = []; return; }
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Файл способов закрытия слишком большой.");
        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<List<CloseProfile>>(stream, Options, ct)
            ?? throw new InvalidDataException("Файл способов закрытия пуст.");
        foreach (var profile in loaded) Validate(profile);
        profiles = loaded;
    }

    private async Task WriteAsync(List<CloseProfile> next, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(next, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024)
            throw new InvalidDataException("Файл способов закрытия слишком большой.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, json, ct);
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, path, true);
    }

    internal static void Validate(CloseProfile profile)
    {
        var recipe = profile.Recipe;
        if (recipe is null || recipe.Window is null || recipe.Elements is null || profile.FileHash is null
            || recipe.Window.ClassName is null || recipe.Window.Title is null || profile.Executable is null)
            throw new InvalidDataException("Некорректный способ закрытия программы.");
        if (!Path.IsPathFullyQualified(profile.Executable) || !profile.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || profile.FileHash.Length != 64 || !profile.FileHash.All(Uri.IsHexDigit) || !Enum.IsDefined(recipe.Method)
            || string.IsNullOrWhiteSpace(recipe.Label) || recipe.Label.Length > 512 || string.IsNullOrWhiteSpace(recipe.Window.ClassName)
            || recipe.Window.ClassName.Length > 256 || recipe.Window.Title.Length > 512
            || recipe.Method == ExitMethod.MenuCommand && (recipe.CommandId == 0 || recipe.CommandId > ushort.MaxValue)
            || recipe.Method == ExitMethod.Automation && recipe.Elements.Count == 0 || recipe.Elements.Count > 16
            || recipe.Elements.Any(e => e is null || e.Name is null || e.AutomationId is null || e.Name.Length > 512 || e.AutomationId.Length > 512)
            || new[] { recipe.TrayIcon, recipe.TrayOpener }.Any(e => e is not null && (e.Name is null || e.AutomationId is null || e.Name.Length > 512 || e.AutomationId.Length > 512))
            || recipe.Control is { } control && (string.IsNullOrEmpty(control.ClassName) || control.ClassName.Length > 256 || string.IsNullOrEmpty(control.Text) || control.Text.Length > 512)
            || profile.DirectVerified && !profile.BackgroundVerified || profile.ManualExitCode < 0)
            throw new InvalidDataException("Некорректный способ закрытия программы.");
    }

    public static async Task<string> FingerprintAsync(string executable, CancellationToken ct = default)
    {
        await using var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }
}
