using System.Text.Json.Serialization;

namespace AppFlow.Core;

public enum ParentEvent { Started, Exited, Running }
public enum FlowAction { Launch, Close }
public enum ThemeChoice { System, Light, Dark }

public sealed record FlowStep
{
    public FlowAction Action { get; init; }
    public double DelaySeconds { get; init; }
}

public sealed record AutomationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ParentEvent Trigger { get; init; }
    public double? RepeatSeconds { get; init; }
    public List<FlowStep> Steps { get; init; } = [new() { Action = FlowAction.Launch }];
    public static List<AutomationRule> CreateDefaults() =>
    [
        new() { Trigger = ParentEvent.Started },
        new() { Trigger = ParentEvent.Exited, Steps = [new() { Action = FlowAction.Close }] }
    ];
}

public sealed record AppLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Executable { get; init; } = "";
    public bool Enabled { get; init; } = true;
    [JsonIgnore] public TargetKind Kind => ActionTarget.Kind(Executable);
    // Null means an existing single-event configuration, retained on load.
    public List<AutomationRule>? Rules { get; init; }
    [JsonIgnore] public Guid OwnershipId { get; init; }
    [JsonIgnore] public Guid ClaimId => OwnershipId == Guid.Empty ? Id : OwnershipId;
    public ParentEvent Trigger { get; init; }
    public double? RepeatSeconds { get; init; }
    public List<FlowStep> Steps { get; init; } = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 180 }];

    public IEnumerable<AppLink> ExecutionRules() => (Rules is null ? new[] { this } : Rules.Select(rule => this with
    {
        Id = rule.Id, OwnershipId = Id, Rules = null,
        Trigger = rule.Trigger, RepeatSeconds = rule.RepeatSeconds, Steps = rule.Steps
    })).Select(rule => rule.ForExecution());

    private AppLink ForExecution()
    {
        if (Trigger != ParentEvent.Running) return this;
        if (Kind == TargetKind.Website) return this with { Trigger = ParentEvent.Started };
        return this with { RepeatSeconds = RepeatSeconds ?? 300, Steps = [new() { Action = FlowAction.Launch },
            new() { Action = FlowAction.Close, DelaySeconds = Steps.Skip(1).FirstOrDefault(s => s.Action == FlowAction.Close)?.DelaySeconds ?? 180 }] };
    }
}

public sealed record ParentApp
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Executable { get; init; } = "";
    public List<AppLink> Links { get; init; } = [];
}

public sealed record AppSettings
{
    public bool AutoStart { get; init; }
    public ThemeChoice Theme { get; init; }
    public bool MinimizeToTray { get; init; } = true;
}

public sealed record AppConfiguration
{
    public int Version { get; init; } = 1;
    public AppSettings Settings { get; init; } = new();
    public List<ParentApp> Parents { get; init; } = [];

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия конфигурации.");
        var ids = new HashSet<Guid>();
        foreach (var parent in Parents)
        {
            if (!ids.Add(parent.Id)) throw new InvalidDataException("Повторяющийся идентификатор программы.");
            ValidateExe(parent.Executable);
            foreach (var link in parent.Links)
            {
                if (!ids.Add(link.Id)) throw new InvalidDataException("Повторяющийся идентификатор связи.");
                if (link.Kind == TargetKind.Website) _ = ActionTarget.Website(link.Executable);
                else if (link.Kind == TargetKind.Script)
                {
                    if (!Path.IsPathFullyQualified(link.Executable)) throw new InvalidDataException("Выберите полный путь к скрипту.");
                }
                else ValidateExe(link.Executable);
                if (string.Equals(parent.Executable, link.Executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Программа не может управлять сама собой.");
                if (link.Rules is { Count: > 32 }) throw new InvalidDataException("Допускается не более 32 событий одной программы.");
                foreach (var rule in link.ExecutionRules())
                {
                if (link.Rules is not null && !ids.Add(rule.Id)) throw new InvalidDataException("Повторяющийся идентификатор события.");
                if (rule.Steps.Count == 0 || rule.Steps.Count > 32) throw new InvalidDataException("Добавьте от 1 до 32 действий.");
                if (link.Kind == TargetKind.Website && (rule.Steps.Any(s => s.Action != FlowAction.Launch) || rule.RepeatSeconds is not null))
                    throw new InvalidDataException("Для сайта доступно только открытие. Закрытие браузера и циклы не поддерживаются.");
                if (!Enum.IsDefined(rule.Trigger) || rule.Steps.Any(s => !Enum.IsDefined(s.Action) || !double.IsFinite(s.DelaySeconds) || s.DelaySeconds < 0 || s.DelaySeconds > 604800))
                    throw new InvalidDataException("Задержка должна быть от 0 до 604800 секунд.");
                if (rule.RepeatSeconds is double repeat && (!double.IsFinite(repeat) || repeat < 1 || repeat > 604800 || rule.Trigger == ParentEvent.Exited))
                    throw new InvalidDataException("Цикл доступен, пока основная программа работает; пауза — от 1 до 604800 секунд.");
                if (rule.Trigger == ParentEvent.Running && rule.Steps[1].DelaySeconds < 1)
                    throw new InvalidDataException("Время работы цикла должно быть от 1 до 604800 секунд.");
                }
            }
        }
    }

    private static void ValidateExe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите полный путь к исполняемому файлу .exe.");
    }
}

// Start time prevents PID reuse from granting ownership over another process.
public sealed record ProcessIdentity(int Pid, long StartTimeUtcTicks, string Executable);
public enum ProcessCloseResult { Closed, StillRunning, ConfirmationRequired, AccessDenied, CloseUnavailable, Forced, CloseNotLearned, Deferred }
public sealed record ProcessSnapshot(IReadOnlyList<ProcessIdentity> Processes, IReadOnlySet<string>? RunningTargets = null)
{
    public bool Contains(string path) => RunningTargets?.Contains(path) == true || Processes.Any(p => string.Equals(p.Executable, path, StringComparison.OrdinalIgnoreCase));
}

public interface IProcessService
{
    Task OpenWebsiteAsync(string address, CancellationToken cancellationToken) => throw new NotSupportedException("Открытие сайтов недоступно.");
    Task<ProcessIdentity?> LaunchScriptIfAbsentAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException("Запуск скриптов недоступен.");
    Task<ProcessSnapshot> SnapshotAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
    Task<ProcessIdentity?> LaunchIfAbsentAsync(string executable, CancellationToken cancellationToken);
    Task<bool> IsSameProcessAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    // Return false if the process remains alive. Never kill an unverified instance.
    Task<bool> CloseAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    async Task<ProcessCloseResult> CloseWithResultAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        await CloseAsync(identity, cancellationToken) ? ProcessCloseResult.Closed : ProcessCloseResult.StillRunning;
}

public sealed record EngineStatus(IReadOnlySet<Guid> RunningParents, IReadOnlyDictionary<Guid, string> LinkMessages, string? Error);
public sealed record ExecutionActivity(Guid ParentId, Guid? RuleId, ParentEvent Trigger, FlowAction Action);
