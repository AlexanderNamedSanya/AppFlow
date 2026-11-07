using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using AppFlow.Core;
using AppFlow.Desktop.Services;

namespace AppFlow.Desktop.ViewModels;

public sealed class ParentViewModel(ParentApp model) : ObservableObject
{
    public Guid Id { get; } = model.Id;
    public string Name { get; } = model.Name;
    public string Executable { get; } = model.Executable;
    public ImageSource? Icon { get; } = AppIcon.Get(model.Executable);
    public ObservableCollection<LinkViewModel> Links { get; } = new(model.Links.Select(l => new LinkViewModel(l, model.Name, model.Executable)));
    private bool running;
    public bool Running { get => running; set { Set(ref running, value); Raise(nameof(State)); } }
    public string State => !File.Exists(Executable) ? "Файл недоступен" : Running ? "Работает" : "Не запущено";
    public ParentApp ToModel() => new() { Id = Id, Name = Name, Executable = Executable, Links = Links.Select(l => l.ToModel()).ToList() };
}

public sealed class LinkViewModel : ObservableObject
{
    private readonly AppLink original;
    private readonly string mainName;
    private readonly string? mainExecutable;
    private bool enabled;
    private IReadOnlyList<TimelineNode>? preview;
    private string status = "Ожидание события";
    public LinkViewModel(AppLink model, string mainName = "Основная", string? mainExecutable = null)
    {
        original = model; enabled = model.Enabled; this.mainName = mainName; this.mainExecutable = mainExecutable;
        Events = new(model.ExecutionRules().Select(rule => CreateEvent(model.Rules is null ? rule with { Id = Guid.NewGuid() } : rule)));
        Events.CollectionChanged += (_, _) => RefreshPreview();
        AddEventCommand = new RelayCommand(_ => Events.Add(CreateEvent(new AppLink { Id = Guid.NewGuid(), Steps = [new() { Action = FlowAction.Launch }] })));
    }
    public Guid Id => original.Id;
    public string Name => original.Name;
    public string Executable => original.Executable;
    public bool CanLearnClose => original.Kind == TargetKind.Application;
    public ImageSource? Icon => AppIcon.Get(Executable);
    public string LaunchLabel => original.Kind == TargetKind.Website ? "Открыть сайт" : original.Kind == TargetKind.Script ? "Выполнить скрипт" : "Запустить";
    public string MainName => mainName;
    public ImageSource? MainIcon => mainExecutable is null ? null : AppIcon.Get(mainExecutable);
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public ObservableCollection<EventViewModel> Events { get; }
    public ICommand AddEventCommand { get; }
    public string Summary => string.Join("  •  ", Events.Select(e => e.Summary));
    public IReadOnlyList<TimelineNode> Preview => preview ??= EventPreview.Build(Events, mainName, Name, Executable, mainExecutable);
    public void Animate(ExecutionActivity activity)
    {
        if (!Enabled) return;
        foreach (var node in Preview.Where(n => activity.RuleId is { } id ? n.RuleId == id : n.ParentTrigger == activity.Trigger))
            node.Animate(activity.Action);
    }
    private void RefreshPreview() { preview = null; Raise(nameof(Summary)); Raise(nameof(Preview)); }
    private EventViewModel CreateEvent(AppLink rule)
    {
        var editor = new EventViewModel(rule with { Name = Name, Executable = Executable }) { RemoveCommand = new RelayCommand(p => { if (p is EventViewModel row) Events.Remove(row); }) };
        editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EventViewModel.Summary)) RefreshPreview(); };
        return editor;
    }
    public AppLink ToModel() => original with
    {
        Enabled = Enabled,
        Rules = Events.Select(e => e.ToModel()).Select(e => new AutomationRule { Id = e.Id, Trigger = e.Trigger, RepeatSeconds = e.RepeatSeconds, Steps = e.Steps }).ToList()
    };
}

public sealed class EventViewModel : ObservableObject
{
    private bool enabled;
    private ParentEvent trigger;
    private FlowAction action;
    private string delay;
    private bool timedClose;
    private string closeAfter;
    private bool repeat;
    private string interval;
    private string status = "Ожидание события";
    private bool delayEnabled;
    private bool optionsOpen;
    private readonly List<FlowStep> originalSteps;
    private bool sequenceEdited;
    public EventViewModel(AppLink model)
    {
        Id = model.Id; Name = model.Name; Executable = model.Executable;
        enabled = model.Enabled; trigger = model.RepeatSeconds is not null && model.Trigger == ParentEvent.Started ? ParentEvent.Running : model.Trigger;
        if (IsWebsite && trigger == ParentEvent.Running) trigger = ParentEvent.Started;
        originalSteps = model.Steps.ToList();
        var first = model.Steps.First();
        action = IsWebsite || trigger == ParentEvent.Running ? FlowAction.Launch : first.Action; delay = Format(first.DelaySeconds); delayEnabled = first.DelaySeconds > 0;
        var timer = model.Steps.Skip(1).FirstOrDefault(s => s.Action == FlowAction.Close);
        timedClose = !IsWebsite && trigger != ParentEvent.Running && action == FlowAction.Launch && timer is not null && model.RepeatSeconds is null;
        closeAfter = Format(timer?.DelaySeconds ?? 180);
        repeat = !IsWebsite && trigger == ParentEvent.Running;
        interval = Format(model.RepeatSeconds ?? 300);
    }
    public Guid Id { get; }
    public string Name { get; }
    public string Executable { get; }
    public ImageSource? Icon => AppIcon.Get(Executable);
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public ParentEvent Trigger
    {
        get => trigger;
        set
        {
            if (IsWebsite && value == ParentEvent.Running) return;
            if (Set(ref trigger, value))
            {
                if (value == ParentEvent.Running) { Action = FlowAction.Launch; TimedClose = false; }
                Repeat = value == ParentEvent.Running;
                Edited();
            }
        }
    }
    public FlowAction Action
    {
        get => action;
        set { if ((IsWebsite || Trigger == ParentEvent.Running) && value != FlowAction.Launch) return; if (Set(ref action, value)) { if (!IsLaunch) { TimedClose = false; Repeat = false; } Edited(); } }
    }
    public bool DelayEnabled { get => delayEnabled; set { if (Set(ref delayEnabled, value)) Edited(); } }
    public bool OptionsOpen { get => optionsOpen; set => Set(ref optionsOpen, value); }
    public ICommand ToggleOptionsCommand => new RelayCommand(_ => OptionsOpen = !OptionsOpen);
    public ICommand RemoveCommand { get; set; } = new RelayCommand(_ => { });
    public string Delay { get => delay; set { if (Set(ref delay, value)) Edited(); } }
    public bool TimedClose { get => timedClose; set { if (Set(ref timedClose, value)) Edited(); } }
    public string CloseAfter { get => closeAfter; set { if (Set(ref closeAfter, value)) Edited(); } }
    public bool Repeat { get => repeat; set { if (Set(ref repeat, !IsWebsite && Trigger == ParentEvent.Running)) Edited(); } }
    public string Interval { get => interval; set { if (Set(ref interval, value)) Refresh(); } }
    public string Status { get => status; set => Set(ref status, value); }
    public bool IsLaunch => Action == FlowAction.Launch;
    public bool IsWebsite => ActionTarget.Kind(Executable) == TargetKind.Website;
    public bool CanClose => !IsWebsite && Trigger != ParentEvent.Running;
    public bool CanRun => !IsWebsite;
    public string OpenActionLabel => Trigger == ParentEvent.Running ? "Циклично" : IsWebsite ? "Открыть сайт" : ActionTarget.Kind(Executable) == TargetKind.Script ? "Выполнить" : "Запустить";
    public string CloseActionLabel => ActionTarget.Kind(Executable) == TargetKind.Script ? "Остановить" : "Закрыть";
    public bool CanRepeat => !IsWebsite && IsLaunch && Trigger == ParentEvent.Running;
    public bool IsCycle => CanRepeat && Repeat;
    public bool SingleActionOptions => !IsCycle;
    public bool SingleLaunchOptions => !IsWebsite && IsLaunch && !IsCycle;
    public bool HasTimer => SingleLaunchOptions && TimedClose;
    public string TimerDescription => "Время отсчитывается после запуска дочерней программы.";
    private string EventLabel => Trigger switch { ParentEvent.Started => "Основная программа запущена", ParentEvent.Exited => "Основная программа закрыта", _ => "Основная программа работает" };
    public string Summary => $"{EventLabel} → {(IsLaunch ? "Запустить" : "Закрыть")}" + (!IsCycle && DelayEnabled ? $" · {Delay} с" : "")
        + (HasTimer ? $" → закрыть через {CloseAfter} с" : "")
        + (IsCycle ? $" → работать {CloseAfter} с → закрыть → пауза {Interval} с ↻" : "");
    private void Edited() { sequenceEdited = true; Refresh(); }
    private void Refresh()
    {
        Raise(nameof(Summary)); Raise(nameof(IsLaunch)); Raise(nameof(CanRepeat)); Raise(nameof(IsCycle)); Raise(nameof(SingleActionOptions)); Raise(nameof(SingleLaunchOptions)); Raise(nameof(HasTimer)); Raise(nameof(TimerDescription)); Raise(nameof(CanClose)); Raise(nameof(OpenActionLabel));
    }
    public AppLink ToModel()
    {
        if (IsWebsite)
            return new() { Id = Id, Name = Name, Executable = Executable, Enabled = Enabled, Trigger = Trigger,
                Steps = [new() { Action = FlowAction.Launch, DelaySeconds = DelayEnabled ? ParseSeconds(Delay) : 0 }] };
        if (IsCycle)
        {
            var work = ParseSeconds(CloseAfter);
            var pause = ParseSeconds(Interval);
            if (work < 1 || pause < 1) throw new InvalidDataException("Время работы и пауза цикла должны быть от 1 до 604800 секунд.");
            return new()
            {
                Id = Id, Name = Name, Executable = Executable, Enabled = Enabled, Trigger = Trigger, RepeatSeconds = pause,
                Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = work }]
            };
        }
        var steps = sequenceEdited ? new List<FlowStep> { new() { Action = Action, DelaySeconds = DelayEnabled ? ParseSeconds(Delay) : 0 } } : originalSteps.ToList();
        if (sequenceEdited && HasTimer) steps.Add(new() { Action = FlowAction.Close, DelaySeconds = ParseSeconds(CloseAfter) });
        return new()
        {
            Id = Id, Name = Name, Executable = Executable, Enabled = Enabled, Trigger = Trigger,
            RepeatSeconds = null, Steps = steps
        };
    }
    private static string Format(double value) => value.ToString(CultureInfo.CurrentCulture);
    private static double ParseSeconds(string text)
    {
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result) || result < 0 || result > 604800)
            throw new InvalidDataException("Введите время от 0 до 604800 секунд.");
        return result;
    }
}

