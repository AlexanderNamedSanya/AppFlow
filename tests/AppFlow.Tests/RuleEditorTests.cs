using AppFlow.Core;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class RuleEditorTests
{
    [Fact]
    public void CycleIsAvailableOnlyWhileMainProgramRunsAndSavesBothPhases()
    {
        var editor = new EventViewModel(new AppLink { Steps = [new() { Action = FlowAction.Launch }] });
        Assert.False(editor.CanRepeat);
        editor.Trigger = ParentEvent.Running;
        Assert.True(editor.CanRepeat); Assert.True(editor.Repeat);
        Assert.Equal("Циклично", editor.OpenActionLabel); Assert.False(editor.CanClose);
        editor.Action = FlowAction.Close; Assert.Equal(FlowAction.Launch, editor.Action);
        editor.Repeat = true; editor.CloseAfter = "10"; editor.Interval = "20";
        var saved = editor.ToModel();
        Assert.Equal(ParentEvent.Running, saved.Trigger); Assert.Equal(20, saved.RepeatSeconds);
        Assert.Equal(FlowAction.Launch, saved.Steps[0].Action);
        Assert.Equal(FlowAction.Close, saved.Steps[1].Action); Assert.Equal(10, saved.Steps[1].DelaySeconds);
        
        editor.Repeat = false; Assert.True(editor.IsCycle);
        editor.Trigger = ParentEvent.Started; saved = editor.ToModel();
        Assert.Single(saved.Steps); Assert.Null(saved.RepeatSeconds);
        editor.Trigger = ParentEvent.Exited; Assert.False(editor.CanRepeat);
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("5", "0")]
    public void CycleRejectsZeroPhaseDurations(string work, string pause)
    {
        var editor = new EventViewModel(new AppLink { Trigger = ParentEvent.Running });
        editor.Repeat = true; editor.CloseAfter = work; editor.Interval = pause;
        Assert.Throws<InvalidDataException>(() => editor.ToModel());
    }
    [Fact]
    public void NewChildHasTwoEventsWithAllOptionsOff()
    {
        var model = new AppLink { Rules = AutomationRule.CreateDefaults() };
        var editor = new LinkViewModel(model);
        Assert.Equal(2, editor.Events.Count);
        Assert.Equal(ParentEvent.Started, editor.Events[0].Trigger);
        Assert.Equal(FlowAction.Launch, editor.Events[0].Action);
        Assert.Equal(ParentEvent.Exited, editor.Events[1].Trigger);
        Assert.Equal(FlowAction.Close, editor.Events[1].Action);
        Assert.All(editor.Events, row => { Assert.False(row.DelayEnabled); Assert.False(row.TimedClose); Assert.False(row.Repeat); Assert.False(row.OptionsOpen); });
        editor.AddEventCommand.Execute(null);
        Assert.Equal(3, editor.Events.Count);
        Assert.False(editor.Events[2].TimedClose);
        editor.Events[2].RemoveCommand.Execute(editor.Events[2]);
        Assert.Equal(2, editor.Events.Count);
    }

    [Fact]
    public void LegacyEventCanBeSavedWithoutDuplicateIdsOrLosingTimer()
    {
        var old = new AppLink { Executable = Path.GetFullPath("child.exe") };
        var saved = new LinkViewModel(old).ToModel();
        Assert.Single(saved.Rules!); Assert.NotEqual(saved.Id, saved.Rules![0].Id);
        Assert.Equal(180, saved.Rules[0].Steps[1].DelaySeconds);
        new AppConfiguration { Parents = [new() { Executable = Path.GetFullPath("parent.exe"), Links = [saved] }] }.Validate();
    }

    [Fact]
    public void DisabledDelayDoesNotUseStoredValue()
    {
        var row = new EventViewModel(new AppLink { Steps = [new() { Action = FlowAction.Launch }] });
        row.Delay = "25";
        Assert.Equal(0, row.ToModel().Steps[0].DelaySeconds);
        row.DelayEnabled = true;
        Assert.Equal(25, row.ToModel().Steps[0].DelaySeconds);
    }
    [Fact]
    public void DisablingTimerRemovesScheduledCloseAction()
    {
        var editor = new EventViewModel(new AppLink());
        editor.TimedClose = false;
        var link = editor.ToModel();
        Assert.Single(link.Steps);
        editor.TimedClose = true; editor.CloseAfter = "120";
        link = editor.ToModel();
        Assert.Equal(2, link.Steps.Count); Assert.Equal(120, link.Steps[1].DelaySeconds);
        
    }

    [Fact]
    public void CloseActionDoesNotRetainLaunchOptions()
    {
        var editor = new EventViewModel(new AppLink { RepeatSeconds = 5 });
        editor.Trigger = ParentEvent.Started;
        editor.Action = FlowAction.Close;
        var link = editor.ToModel();
        Assert.Single(link.Steps); Assert.Equal(FlowAction.Close, link.Steps[0].Action);
        Assert.Null(link.RepeatSeconds);
    }

    [Fact]
    public void SavedRunningEventWithoutCycleIsNormalizedToCycle()
    {
        var link = new AppLink { Trigger = ParentEvent.Running, Steps = [new() { Action = FlowAction.Close }] };
        var effective = Assert.Single(link.ExecutionRules());
        Assert.Equal(300, effective.RepeatSeconds);
        Assert.Equal(FlowAction.Launch, effective.Steps[0].Action);
        Assert.Equal(180, effective.Steps[1].DelaySeconds);
        var editor = new EventViewModel(link);
        Assert.Equal("Циклично", editor.OpenActionLabel); Assert.True(editor.IsCycle);
        Assert.False(editor.SingleActionOptions); Assert.False(editor.SingleLaunchOptions);
    }

    [Fact]
    public void LegacyCloseOnExitFlagIsIgnored()
    {
        var timed = System.Text.Json.JsonSerializer.Deserialize<AppLink>("{\"CloseOnParentExit\":false}")!;
        Assert.Equal(180, timed.Steps[1].DelaySeconds);
        var untimed = System.Text.Json.JsonSerializer.Deserialize<AppLink>("{\"CloseOnParentExit\":true,\"Steps\":[{\"Action\":0}]}")!;
        Assert.Single(untimed.Steps);
    }
}
