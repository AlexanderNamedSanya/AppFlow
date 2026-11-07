using AppFlow.Core;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class EventPreviewTests
{
    [Fact]
    public void ActivityAnimatesOnlyMatchingNodeAndDoesNotChangeConfiguration()
    {
        var rules = AutomationRule.CreateDefaults();
        var editor = new LinkViewModel(new() { Executable = @"C:\child.exe", Rules = rules });
        var preview = editor.Preview;
        var configurationChanges = 0;
        editor.PropertyChanged += (_, _) => configurationChanges++;
        editor.Animate(new(Guid.NewGuid(), rules[0].Id, ParentEvent.Started, FlowAction.Launch));
        Assert.Equal(new long[] { 0, 1, 0, 0 }, preview.Select(n => n.ActivityVersion));
        editor.Animate(new(Guid.NewGuid(), null, ParentEvent.Exited, FlowAction.Close));
        Assert.Equal(new long[] { 0, 1, 1, 0 }, preview.Select(n => n.ActivityVersion));
        Assert.Equal(FlowAction.Close, preview[2].LastAction);
        Assert.Same(preview, editor.Preview);
        Assert.Equal(0, configurationChanges);
        editor.Enabled = false;
        editor.Animate(new(Guid.NewGuid(), rules[0].Id, ParentEvent.Started, FlowAction.Launch));
        Assert.Equal(1, preview[1].ActivityVersion);
    }

    [Fact]
    public void SingleLineContainsOnlyExistingEventsAndUsesChildIcons()
    {
        var editor = new LinkViewModel(new() { Name = "TTC", Executable = @"C:\TTC.exe", Rules = AutomationRule.CreateDefaults() }, "TESO", @"C:\TESO.exe");
        Assert.Equal(new[] { "TESO", "TTC", "TESO", "TTC" }, editor.Preview.Select(n => n.Title));
        Assert.All(editor.Preview, node => Assert.True(node.IsApplication));
        Assert.Equal(new[] { @"C:\TESO.exe", @"C:\TTC.exe", @"C:\TESO.exe", @"C:\TTC.exe" }, editor.Preview.Select(n => n.Executable));
        Assert.False(editor.Preview[0].HasPrevious);
        Assert.All(editor.Preview.Skip(1), node => Assert.True(node.HasPrevious));
        editor.Events.RemoveAt(1);
        Assert.Equal(2, editor.Preview.Count);
        Assert.DoesNotContain(editor.Preview, n => n.Description.Contains("закрытие"));
        editor.Events.Clear(); Assert.Empty(editor.Preview);
    }

    [Fact]
    public void TimerIsDetailOfActionAndDoesNotInventExitOrCloseNodes()
    {
        var editor = new LinkViewModel(new() { Name = "TTC", Executable = @"C:\TTC.exe", Rules = [new()] }, "TESO");
        var rule = editor.Events[0]; rule.Delay = "30"; rule.CloseAfter = "180";
        Assert.All(editor.Preview, n => Assert.False(n.HasDetail));
        rule.TimedClose = true;
        Assert.Equal(2, editor.Preview.Count);
        Assert.Equal("3 минуты", editor.Preview[1].Detail);
        Assert.DoesNotContain(editor.Preview, n => n.Description.Contains("или выход"));
        rule.TimedClose = false; Assert.False(editor.Preview[1].HasDetail);
    }

    [Fact]
    public void PhasesAreChronologicalAndAllActionsWithinPhaseAreRetained()
    {
        var rules = new List<AutomationRule>
        {
            new() { Trigger = ParentEvent.Exited, Steps = [new() { Action = FlowAction.Close }] },
            new() { Trigger = ParentEvent.Running, RepeatSeconds = 300, Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 60 }] },
            new(), new() { Steps = [new() { Action = FlowAction.Close }] }
        };
        var editor = new LinkViewModel(new() { Name = "TTC", Rules = rules }, "TESO");
        Assert.Equal(6, editor.Preview.Count);
        Assert.Equal("TESO", editor.Preview[0].Title);
        Assert.Contains("запуск", editor.Preview[1].Description);
        Assert.Contains("закрытие", editor.Preview[2].Description);
        Assert.True(editor.Preview[3].IsCycle);
        Assert.Equal("1 мин / 5 мин", editor.Preview[3].Detail);
        Assert.Contains("закрытие", editor.Preview[4].Description);
    }

    [Theory]
    [InlineData("60", "1 минута")]
    [InlineData("180", "3 минуты")]
    [InlineData("300", "5 минут")]
    [InlineData("660", "11 минут")]
    [InlineData("1260", "21 минута")]
    public void TimerDurationsUseNaturalRussianLabels(string seconds, string expected)
    {
        var editor = new LinkViewModel(new AppLink(), "TESO");
        editor.Events[0].CloseAfter = seconds;
        Assert.Equal(expected, editor.Preview[1].Detail);
    }
}
