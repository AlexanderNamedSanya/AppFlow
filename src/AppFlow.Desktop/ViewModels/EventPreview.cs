using System.Globalization;
using System.ComponentModel;
using System.Windows.Media;
using AppFlow.Core;
using AppFlow.Desktop.Services;

namespace AppFlow.Desktop.ViewModels;

public sealed record TimelineNode(string Title, string Description, string Geometry, string Color,
    string Detail = "", string Delay = "", bool IsCycle = false, bool HasPrevious = true, string? Executable = null,
    Guid? RuleId = null, ParentEvent? ParentTrigger = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public long ActivityVersion { get; private set; }
    public FlowAction LastAction { get; private set; }
    public void Animate(FlowAction action)
    {
        LastAction = action;
        ActivityVersion++;
        PropertyChanged?.Invoke(this, new(nameof(ActivityVersion)));
    }
    public bool IsApplication => Executable is not null;
    public bool HasDetail => Detail.Length != 0;
    public bool HasDelay => Delay.Length != 0;
    public bool ShowTimer => HasDetail && !IsCycle;
    public ImageSource? Icon => Executable is null ? null : AppIcon.Get(Executable);
}

public static class EventPreview
{
    private const string Play = "M 12,2 A 10,10 0 1 1 11.99,2 M 10,7 L 17,12 L 10,17 Z";
    private const string Stop = "M 5,5 L 19,5 L 19,19 L 5,19 Z";
    private const string Repeat = "M 19,7 A 8,8 0 0 0 4,11 M 19,2 L 19,7 L 14,7 M 5,17 A 8,8 0 0 0 20,13 M 5,22 L 5,17 L 10,17";

    public static IReadOnlyList<TimelineNode> Build(IEnumerable<EventViewModel> rules, string mainName, string childName, string executable, string? mainExecutable = null)
    {
        var nodes = new List<TimelineNode>();
        // Group once per lifecycle phase; retain user order inside each phase.
        foreach (var phase in new[] { ParentEvent.Started, ParentEvent.Running, ParentEvent.Exited })
        {
            var events = rules.Where(e => e.Trigger == phase).ToArray();
            if (events.Length == 0) continue;
            if (phase != ParentEvent.Running)
                nodes.Add(new(mainName, $"{mainName}: {(phase == ParentEvent.Started ? "запуск" : "закрытие")}",
                    phase == ParentEvent.Started ? Play : Stop, phase == ParentEvent.Started ? "#32AC82" : "#E36D78", Executable: mainExecutable, ParentTrigger: phase));
            foreach (var rule in events)
            {
                var detail = rule.IsCycle ? $"{Duration(rule.CloseAfter, true)} / {Duration(rule.Interval, true)}"
                    : rule.HasTimer ? Duration(rule.CloseAfter) : "";
                var delay = !rule.IsCycle && rule.DelayEnabled ? Duration(rule.Delay) : "";
                var description = rule.IsCycle
                    ? $"{mainName} работает → {childName}: работа {Duration(rule.CloseAfter)}, пауза {Duration(rule.Interval)}"
                    : $"{childName}: {(rule.IsLaunch ? "запуск" : "закрытие")}";
                if (rule.HasTimer) description += $"; таймер закрытия {Duration(rule.CloseAfter)}";
                if (delay.Length > 0) description += $"; задержка {delay}";
                nodes.Add(new(childName, description, rule.IsCycle ? Repeat : rule.IsLaunch ? Play : Stop,
                    rule.IsCycle ? "#699EE8" : rule.IsLaunch ? "#32AC82" : "#E36D78",
                    Detail: detail, Delay: delay, IsCycle: rule.IsCycle, Executable: executable, RuleId: rule.Id));
            }
        }
        return nodes.Select((node, index) => node with { HasPrevious = index != 0 }).ToArray();
    }

    private static string Duration(string value, bool compact = false)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0)
            return value + " с";
        var number = seconds;
        var unit = "с";
        var words = new[] { "секунда", "секунды", "секунд" };
        if (seconds >= 3600 && seconds % 3600 == 0) { number = seconds / 3600; unit = "ч"; words = ["час", "часа", "часов"]; }
        else if (seconds >= 60 && seconds % 60 == 0) { number = seconds / 60; unit = "мин"; words = ["минута", "минуты", "минут"]; }
        var text = number.ToString("0.###", CultureInfo.CurrentCulture);
        if (compact || number % 1 != 0) return text + " " + unit;
        var ending = number % 100 is >= 11 and <= 14 ? 2 : number % 10 == 1 ? 0 : number % 10 is >= 2 and <= 4 ? 1 : 2;
        return text + " " + words[ending];
    }
}
