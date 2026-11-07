using AppFlow.Core;
using Xunit;

namespace AppFlow.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task ActivitiesIdentifyParentTransitionsAndExactExecutedRule()
    {
        var processes = new FakeProcesses(); var clock = new ManualClock();
        var rules = AutomationRule.CreateDefaults();
        rules[0] = rules[0] with { Steps = [new() { Action = FlowAction.Launch, DelaySeconds = 2 }] };
        var parent = Parent(Link() with { Rules = rules });
        await using var engine = new AutomationEngine(processes, clock);
        var activities = new List<ExecutionActivity>(); engine.ActivityOccurred += activities.Add;
        await engine.ConfigureAsync(Config(parent));
        await engine.TickAsync(); Assert.Empty(activities);
        processes.External(ParentPath); await engine.TickAsync();
        var started = Assert.Single(activities);
        Assert.Null(started.RuleId); Assert.Equal(parent.Id, started.ParentId); Assert.Equal(ParentEvent.Started, started.Trigger);
        clock.Advance(2); await engine.TickAsync();
        Assert.Equal(rules[0].Id, activities[1].RuleId); Assert.Equal(FlowAction.Launch, activities[1].Action);
        await engine.TickAsync(); Assert.Equal(2, activities.Count);
        processes.Remove(ParentPath); await engine.TickAsync();
        Assert.Null(activities[2].RuleId); Assert.Equal(ParentEvent.Exited, activities[2].Trigger);
        Assert.Equal(rules[1].Id, activities[3].RuleId); Assert.Equal(FlowAction.Close, activities[3].Action);
    }

    [Fact]
    public async Task SkippedLaunchDoesNotReportExecutionAndCyclesReportBothPhases()
    {
        var processes = new FakeProcesses(); var clock = new ManualClock();
        processes.External(ParentPath); processes.External(TargetPath);
        var cycle = Link() with { Trigger = ParentEvent.Running, RepeatSeconds = 3,
            Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 2 }] };
        await using var engine = new AutomationEngine(processes, clock);
        var activities = new List<ExecutionActivity>(); engine.ActivityOccurred += activities.Add;
        await engine.ConfigureAsync(Config(Parent(cycle))); await engine.TickAsync();
        Assert.Single(activities); Assert.Null(activities[0].RuleId);
        clock.Advance(2); await engine.TickAsync(); Assert.Single(activities);
        processes.Remove(TargetPath);
        clock.Advance(3); await engine.TickAsync();
        clock.Advance(2); await engine.TickAsync();
        Assert.Equal(new[] { FlowAction.Launch, FlowAction.Close }, activities.Where(a => a.RuleId is not null).Select(a => a.Action));
        Assert.All(activities.Where(a => a.RuleId is not null), a => Assert.Equal(cycle.Id, a.RuleId));
    }

    private static readonly string ParentPath = Path.GetFullPath("TESO.exe");
    [Fact]
    public async Task UnchangedTicksDoNotRepublishStatus()
    {
        var processes = new FakeProcesses();
        await using var engine = new AutomationEngine(processes);
        var statuses = new List<EngineStatus>();
        engine.StatusChanged += statuses.Add;
        await engine.ConfigureAsync(Config(Parent(Link())));
        await engine.TickAsync();
        for (var i = 0; i < 20; i++) await engine.TickAsync();
        Assert.Single(statuses);
        processes.External(ParentPath);
        await engine.TickAsync();
        Assert.Equal(2, statuses.Count);
        await engine.TickAsync();
        Assert.Equal(2, statuses.Count);
    }

    [Fact]
    public async Task EmptyMonitorSleepsAndConfigurationWakesItImmediately()
    {
        var processes = new FakeProcesses();
        await using var engine = new AutomationEngine(processes);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StatusChanged += status =>
        {
            if (status.RunningParents.Count == 0) idle.TrySetResult(); else running.TrySetResult();
        };
        engine.Start();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(1100);
        Assert.Equal(0, processes.Snapshots);
        processes.External(ParentPath);
        await engine.ConfigureAsync(Config(Parent(Link())));
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, processes.Launches);
    }

    private static readonly string TargetPath = Path.GetFullPath("TTC.exe");
    private static AppLink Link(params FlowStep[] steps) => new() { Executable = TargetPath, Steps = steps.Length == 0 ? [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 180 }] : steps.ToList() };
    private static ParentApp Parent(params AppLink[] links) => new() { Executable = ParentPath, Links = links.ToList() };
    private static AppConfiguration Config(params ParentApp[] parents) => new() { Parents = parents.ToList() };

    [Fact]
    public async Task WebsiteOpensOnceWithoutCreatingOrOwningBrowserProcess()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(new AppLink { Executable = "https://example.com/", Rules = [new()] })));
        await engine.TickAsync(); await engine.TickAsync();
        Assert.Single(process.WebsitesOpened); Assert.Equal(0, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        await engine.LaunchManuallyAsync("https://example.com/"); Assert.Equal(2, process.WebsitesOpened.Count);
    }

    [Fact]
    public async Task ScriptRulesUseScriptLauncherAndCloseOnlyOwnedInstance()
    {
        var path = Path.GetFullPath("example.ps1"); var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(new AppLink { Executable = path, Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); Assert.Equal(1, process.ScriptLaunches);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task ExistingScriptIsNotAdoptedOrClosed()
    {
        var path = Path.GetFullPath("example.cmd"); var process = new FakeProcesses(); process.External(ParentPath); process.External(path);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(new AppLink { Executable = path, Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(0, process.Launches); Assert.Equal(0, process.Closes);
    }

    [Fact]
    public async Task ManuallyLaunchedChildIsNotAdoptedOrClosedByRules()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() })));
        Assert.True(await engine.LaunchManuallyAsync(TargetPath));
        Assert.False(await engine.LaunchManuallyAsync(TargetPath));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(1, process.Launches); Assert.Equal(0, process.Closes);
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
    }

    [Fact]
    public async Task ManualMainLaunchActivatesExistingAutomation()
    {
        var process = new FakeProcesses();
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() })));
        await engine.LaunchManuallyAsync(ParentPath); await engine.TickAsync();
        Assert.Equal(2, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task ConcurrentManualAndAutomaticLaunchCannotDuplicateProcess()
    {
        var process = new FakeProcesses { DelayLaunch = true }; process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link())));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? Task.Run(() => engine.LaunchManuallyAsync(TargetPath)) : Task.Run(() => engine.TickAsync())));
        Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task FailedManualLaunchDoesNotBlockFollowingLaunch()
    {
        var process = new FakeProcesses { FailPath = TargetPath };
        await using var engine = new AutomationEngine(process);
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LaunchManuallyAsync(TargetPath));
        process.FailPath = null;
        Assert.True(await engine.LaunchManuallyAsync(TargetPath));
    }

    [Fact]
    public async Task OldTimerAfterMainExitCannotCloseNewExitLaunchedInstance()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var rules = AutomationRule.CreateDefaults(); rules[0] = rules[0] with { Steps = Link().Steps };
        rules.Add(new() { Trigger = ParentEvent.Exited, Steps = [new() { Action = FlowAction.Launch, DelaySeconds = 5 }] });
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = rules }))); await engine.TickAsync();
        clock.Advance(100); process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(5); await engine.TickAsync(); Assert.Equal(2, process.Launches);
        clock.Advance(75); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
    }

    [Fact]
    public async Task CyclePauseStartsAfterProcessHasFinishedClosing()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        process.AfterClose = () => clock.Advance(3);
        var cycle = Link(new FlowStep { Action = FlowAction.Launch }, new FlowStep { Action = FlowAction.Close, DelaySeconds = 2 })
            with { Trigger = ParentEvent.Running, RepeatSeconds = 5 };
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(cycle))); await engine.TickAsync();
        clock.Advance(2); await engine.TickAsync();
        clock.Advance(4); await engine.TickAsync(); Assert.Equal(1, process.Launches);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(2, process.Launches);
    }

    [Fact]
    public async Task RunningStateWithoutCycleFiresOnceForAlreadyRunningMain()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link(new FlowStep { Action = FlowAction.Launch }) with { Trigger = ParentEvent.Running })));
        await engine.TickAsync(); await engine.TickAsync();
        process.Remove(TargetPath); await engine.TickAsync(); Assert.Equal(1, process.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningCycleHonorsWorkPauseAndStopsInEitherPhase(bool exitDuringPause)
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var cycle = Link(new FlowStep { Action = FlowAction.Launch }, new FlowStep { Action = FlowAction.Close, DelaySeconds = 2 })
            with { Trigger = ParentEvent.Running, RepeatSeconds = 5 };
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(cycle))); await engine.TickAsync(); Assert.Equal(1, process.Launches);
        clock.Advance(2); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(4); await engine.TickAsync(); Assert.Equal(1, process.Launches);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(2, process.Launches);
        if (exitDuringPause) { clock.Advance(2); await engine.TickAsync(); }
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(exitDuringPause ? 2 : 1, process.Closes);
        clock.Advance(100); await engine.TickAsync(); Assert.Equal(2, process.Launches);
        Assert.Equal(2, process.Closes);
        process.External(ParentPath); await engine.TickAsync(); Assert.Equal(3, process.Launches);
    }

    [Fact]
    public async Task RunningCycleNeverClosesPreexistingUserProcess()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath); var external = process.External(TargetPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link() with { Trigger = ParentEvent.Running, RepeatSeconds = 5 })));
        await engine.TickAsync(); clock.Advance(180); await engine.TickAsync();
        clock.Advance(5); await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(0, process.Closes); Assert.Equal(0, process.Launches); Assert.Contains(external, process.Running);
    }

    [Fact]
    public async Task EditingCloseEventDoesNotTerminateAlreadyRunningChild()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        var link = new AppLink { Executable = TargetPath, Rules = AutomationRule.CreateDefaults() };
        var parent = Parent(link);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(parent)); await engine.TickAsync();
        var rules = link.Rules!.ToList();
        rules[1] = rules[1] with { Steps = [new() { Action = FlowAction.Close, DelaySeconds = 5 }] };
        await engine.ConfigureAsync(Config(parent with { Links = [link with { Rules = rules }] }));
        await engine.TickAsync(); Assert.Equal(0, process.Closes); Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task DefaultStartAndExitEventsShareOwnedProcess()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(new AppLink() { Executable = TargetPath, Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); Assert.Equal(1, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task ExitEventNeverClosesUserProcess()
    {
        var process = new FakeProcesses(); process.External(ParentPath); var external = process.External(TargetPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(new AppLink() { Executable = TargetPath, Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(0, process.Launches); Assert.Equal(0, process.Closes); Assert.Contains(external, process.Running);
    }

    [Fact]
    public async Task MultipleActionsOfSameChildRunIndependently()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var rules = AutomationRule.CreateDefaults();
        rules.Add(new() { Trigger = ParentEvent.Started, Steps = [new() { Action = FlowAction.Close, DelaySeconds = 5 }] });
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(new AppLink() { Executable = TargetPath, Rules = rules })));
        await engine.TickAsync(); Assert.Equal(1, process.Launches);
        clock.Advance(5); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task DelayedExitCannotCloseChildOfNextParentSession()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var rules = AutomationRule.CreateDefaults(); rules[1] = rules[1] with { Steps = [new() { Action = FlowAction.Close, DelaySeconds = 5 }] };
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(new AppLink() { Executable = TargetPath, Rules = rules })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        clock.Advance(2); process.External(ParentPath); await engine.TickAsync();
        clock.Advance(5); await engine.TickAsync(); Assert.Equal(0, process.Closes);
    }

    [Fact]
    public async Task SynchronizesAlreadyRunningParentAndClosesAfterThreeMinutes()
    {
        var process = new FakeProcesses(); var clock = new ManualClock();
        process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link())));
        await engine.TickAsync();
        Assert.Equal(1, process.Launches);
        clock.Advance(179); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        await engine.TickAsync(); Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task WallClockChangesDoNotCompleteTimerEarly()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        clock.JumpWallClock(86400); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        clock.Advance(180); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task ParentExitsEarlyButTimerKeepsOriginalDeadline()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        clock.Advance(12); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(0, process.Closes);
        clock.Advance(167); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task UserProcessIsNeitherLaunchedAgainNorClosed()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath); var external = process.External(TargetPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        clock.Advance(180); await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(0, process.Launches); Assert.Equal(0, process.Closes); Assert.Contains(external, process.Running);
    }

    [Fact]
    public async Task ReusedPidWithDifferentStartTimeIsNeverClosed()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        var previous = process.Running.Single(p => p.Executable == TargetPath);
        process.Remove(TargetPath); process.Running.Add(previous with { StartTimeUtcTicks = previous.StartTimeUtcTicks + 1 });
        clock.Advance(180); await engine.TickAsync(); Assert.Equal(0, process.Closes);
    }

    [Fact]
    public async Task SelfExitDoesNotRestartWithoutAnotherEvent()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        process.Remove(TargetPath); await engine.TickAsync(); await engine.TickAsync();
        Assert.Equal(1, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); process.External(ParentPath); await engine.TickAsync();
        Assert.Equal(2, process.Launches);
    }

    [Fact]
    public async Task SharedTargetIsClosedOnlyWhenLastParentReleasesIt()
    {
        var process = new FakeProcesses(); var secondPath = Path.GetFullPath("Second.exe");
        var first = Parent(Link() with { Rules = AutomationRule.CreateDefaults() });
        var second = Parent(Link() with { Rules = AutomationRule.CreateDefaults() }) with { Executable = secondPath };
        process.External(ParentPath); process.External(secondPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(first, second)); await engine.TickAsync();
        Assert.Equal(1, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        process.Remove(secondPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task ConcurrentTicksCannotDuplicateLaunch()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link())));
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => engine.TickAsync()));
        Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task DelayedLaunchIsCancelledWhenParentCloses()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link(new FlowStep() { Action = FlowAction.Launch, DelaySeconds = 30 }))));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        clock.Advance(40); await engine.TickAsync(); Assert.Equal(0, process.Launches);
    }

    [Fact]
    public async Task RepeatsWholeSequenceOnlyWhileParentRuns()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var link = Link(new FlowStep() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 2 }) with { RepeatSeconds = 5 };
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync();
        clock.Advance(2); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(5); await engine.TickAsync(); Assert.Equal(2, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); clock.Advance(20); await engine.TickAsync(); Assert.Equal(2, process.Launches);
    }

    [Fact]
    public async Task ExitTriggerRunsOnceOnActualExitAndNotOnIdleStartup()
    {
        var process = new FakeProcesses(); var link = Link(new FlowStep() { Action = FlowAction.Launch }) with { Trigger = ParentEvent.Exited };
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync(); Assert.Equal(0, process.Launches);
        process.External(ParentPath); await engine.TickAsync(); Assert.Equal(0, process.Launches);
        process.Remove(ParentPath); await engine.TickAsync(); await engine.TickAsync(); Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task DisabledRuleDoesNothing()
    {
        var process = new FakeProcesses(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link() with { Enabled = false }))); await engine.TickAsync(); Assert.Equal(0, process.Launches);
    }

    [Fact]
    public async Task DisablingCompletedLaunchOnlyRuleReleasesOwnership()
    {
        var process = new FakeProcesses(); process.External(ParentPath); var link = Link(new FlowStep() { Action = FlowAction.Launch }); var parent = Parent(link);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(parent)); await engine.TickAsync();
        await engine.ConfigureAsync(Config(parent with { Links = [link with { Enabled = false }] }));
        Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task SavingUnchangedRulesPreservesOriginalDeadline()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath); var configuration = Config(Parent(Link()));
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(configuration); await engine.TickAsync(); clock.Advance(100);
        await engine.ConfigureAsync(configuration); await engine.TickAsync();
        Assert.Equal(0, process.Closes); clock.Advance(80); await engine.TickAsync(); Assert.Equal(1, process.Closes); Assert.Equal(1, process.Launches);
    }

    [Fact]
    public async Task ExplicitExitRuleClosesImmediatelyEvenWithPendingTimer()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        var rules = AutomationRule.CreateDefaults(); rules[0] = rules[0] with { Steps = Link().Steps };
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = rules }))); await engine.TickAsync();
        clock.Advance(100); process.Remove(ParentPath); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(80); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutTimerParentExitLeavesChildRunningAndStopsRepeats(bool repeat)
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var link = Link(new FlowStep { Action = FlowAction.Launch }) with { RepeatSeconds = repeat ? 5 : null };
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync();
        process.Remove(ParentPath); await engine.TickAsync();
        clock.Advance(200); await engine.TickAsync();
        Assert.Equal(0, process.Closes); Assert.Equal(1, process.Launches);
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
    }

    [Fact]
    public async Task FailedRuleDoesNotPreventAnotherRuleFromRunning()
    {
        var process = new FakeProcesses(); process.External(ParentPath); process.FailPath = TargetPath;
        var other = Path.GetFullPath("Other.exe");
        await using var engine = new AutomationEngine(process);
        EngineStatus? status = null; engine.StatusChanged += s => status = s;
        var broken = Link();
        await engine.ConfigureAsync(Config(Parent(broken, Link() with { Executable = other }))); await engine.TickAsync();
        Assert.Equal(1, process.Launches); Assert.Contains("недоступен", status!.LinkMessages[broken.Id]);
    }

    [Fact]
    public async Task MultipleParentInstancesRequireLastInstanceToExit()
    {
        var process = new FakeProcesses(); var one = process.External(ParentPath); var two = process.External(ParentPath);
        await using var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() }))); await engine.TickAsync();
        process.Running.Remove(one); await engine.TickAsync(); Assert.Equal(0, process.Closes);
        process.Running.Remove(two); await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task CloseLearningSuspendsOnlySelectedTargetAndPreservesRemainingTimer()
    {
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        var otherPath = Path.GetFullPath("other.exe");
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link(), Link() with { Executable = otherPath })));
        await engine.TickAsync(); Assert.Equal(2, process.Launches);
        clock.Advance(100);
        var suspension = await engine.SuspendTargetAsync(TargetPath);
        clock.Advance(100); await engine.TickAsync();
        Assert.Equal(1, process.Closes); Assert.Contains(process.Running, p => p.Executable == TargetPath);
        await suspension.DisposeAsync(); await suspension.DisposeAsync();
        await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(79); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(2, process.Closes);
    }

    [Fact]
    public async Task DisposeDoesNotTerminateApplications()
    {
        var process = new FakeProcesses(); process.External(ParentPath); var engine = new AutomationEngine(process);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync(); await engine.DisposeAsync(); Assert.Equal(0, process.Closes);
    }

    [Theory]
    [InlineData(ProcessCloseResult.ConfirmationRequired, "подтверждение")]
    [InlineData(ProcessCloseResult.CloseNotLearned, "Обучить закрытию")]
    [InlineData(ProcessCloseResult.AccessDenied, "Нет доступа")]
    [InlineData(ProcessCloseResult.CloseUnavailable, "недоступен")]
    [InlineData(ProcessCloseResult.StillRunning, "продолжает работать")]
    public async Task RefusedClosePausesCycleAndPreservesOwnershipForNextExitEvent(ProcessCloseResult result, string reason)
    {
        var process = new FakeProcesses { CloseResult = result }; var clock = new ManualClock(); process.External(ParentPath);
        var link = Link() with { Rules = [
            new() { Trigger = ParentEvent.Running, RepeatSeconds = 1, Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 1 }] },
            new() { Trigger = ParentEvent.Exited, Steps = [new() { Action = FlowAction.Close }] }
        ] };
        await using var engine = new AutomationEngine(process, clock);
        EngineStatus? status = null; engine.StatusChanged += s => status = s;
        var activities = new List<ExecutionActivity>(); engine.ActivityOccurred += activities.Add;
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync();
        clock.Advance(1); await engine.TickAsync();
        Assert.Contains(reason, status!.LinkMessages[link.Id]);
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
        Assert.DoesNotContain(activities, a => a.RuleId is not null && a.Action == FlowAction.Close);
        clock.Advance(100); await engine.TickAsync(); clock.Advance(100); await engine.TickAsync();
        Assert.Equal(1, process.CloseRequests); Assert.Equal(1, process.Launches);
        process.CloseResult = ProcessCloseResult.Closed; process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(2, process.CloseRequests); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task CancelledCloseRetainsOwnershipForLaterRequest()
    {
        using var cancel = new CancellationTokenSource();
        var process = new FakeProcesses { CancelOnClose = cancel }; var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link()))); await engine.TickAsync();
        clock.Advance(180);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TickAsync(cancel.Token));
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
        process.CancelOnClose = null;
        await engine.TickAsync(); Assert.Equal(1, process.Closes);
    }

    [Fact]
    public async Task DeferredExitRetriesBothChildrenWithoutBlockingTheOtherTarget()
    {
        var other = Path.GetFullPath("Other.exe");
        var process = new FakeProcesses(); var clock = new ManualClock(); process.External(ParentPath);
        process.CloseResults[TargetPath] = ProcessCloseResult.Deferred;
        await using var engine = new AutomationEngine(process, clock);
        var parent = Parent(Link() with { Rules = AutomationRule.CreateDefaults() },
            Link() with { Executable = other, Rules = AutomationRule.CreateDefaults() });
        EngineStatus? status = null; engine.StatusChanged += value => status = value;
        await engine.ConfigureAsync(Config(parent)); await engine.TickAsync();
        process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(2, process.CloseRequests); Assert.Equal(1, process.Closes);
        Assert.Contains("Повторю автоматически", status!.LinkMessages[parent.Links[0].Id]);
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(2, process.CloseRequests);
        process.CloseResults[TargetPath] = ProcessCloseResult.Closed;
        clock.Advance(1); await engine.TickAsync();
        Assert.Equal(3, process.CloseRequests); Assert.Equal(2, process.Closes);
        Assert.Empty(process.Running); Assert.Equal(2, process.Launches);
    }

    [Fact]
    public async Task DeferredCycleKeepsTheCloseStepAndBacksOffUntilItSucceeds()
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.Deferred };
        var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        var link = Link() with { Trigger = ParentEvent.Running, RepeatSeconds = 2,
            Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 1 }] };
        var activities = new List<ExecutionActivity>(); engine.ActivityOccurred += activities.Add;
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync();
        clock.Advance(1); await engine.TickAsync(); Assert.Equal(1, process.CloseRequests);
        foreach (var delay in new[] { 2, 4, 8, 15, 15 })
        {
            var before = process.CloseRequests;
            clock.Advance(delay - 1); await engine.TickAsync(); Assert.Equal(before, process.CloseRequests);
            clock.Advance(1); await engine.TickAsync(); Assert.Equal(before + 1, process.CloseRequests);
            Assert.Equal(1, process.Launches);
        }
        Assert.DoesNotContain(activities, a => a.RuleId is not null && a.Action == FlowAction.Close);
        process.CloseResult = ProcessCloseResult.Closed;
        clock.Advance(15); await engine.TickAsync(); Assert.Equal(1, process.Closes);
        Assert.Single(activities, a => a.RuleId == link.Id && a.Action == FlowAction.Close);
        clock.Advance(2); await engine.TickAsync(); Assert.Equal(2, process.Launches);
    }

    [Fact]
    public async Task ParentRestartCancelsThePostponedExitRequest()
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.Deferred };
        var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(1, process.CloseRequests);
        process.External(ParentPath); await engine.TickAsync();
        process.CloseResult = ProcessCloseResult.Closed;
        clock.Advance(20); await engine.TickAsync();
        Assert.Equal(1, process.CloseRequests); Assert.Equal(0, process.Closes);
        Assert.Contains(process.Running, p => p.Executable == TargetPath);
    }

    [Fact]
    public async Task PostponedCloseNeverTouchesAUserReplacementOfTheOriginalInstance()
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.Deferred };
        var clock = new ManualClock(); process.External(ParentPath);
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        process.Remove(TargetPath); var replacement = process.External(TargetPath);
        process.CloseResult = ProcessCloseResult.Closed;
        clock.Advance(20); await engine.TickAsync();
        Assert.Equal(1, process.CloseRequests); Assert.Equal(0, process.Closes);
        Assert.Equal(replacement, Assert.Single(process.Running));
    }

    [Fact]
    public async Task CloseRulesForTheSameLaunchShareOneRetryCooldown()
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.Deferred };
        var clock = new ManualClock(); process.External(ParentPath);
        var rules = AutomationRule.CreateDefaults();
        rules.Add(new() { Trigger = ParentEvent.Exited, Steps = [new() { Action = FlowAction.Close }] });
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(Config(Parent(Link() with { Rules = rules })));
        await engine.TickAsync(); process.Remove(ParentPath); await engine.TickAsync();
        Assert.Equal(1, process.CloseRequests);
        process.CloseResult = ProcessCloseResult.Closed;
        clock.Advance(2); await engine.TickAsync();
        Assert.Equal(2, process.CloseRequests); Assert.Equal(1, process.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedRulesFinishDeferredReleaseUnlessTheSameLinkIsReenabled(bool reenable)
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.Deferred };
        var clock = new ManualClock(); process.External(ParentPath);
        var config = Config(Parent(Link() with { Rules = AutomationRule.CreateDefaults() }));
        await using var engine = new AutomationEngine(process, clock);
        await engine.ConfigureAsync(config); await engine.TickAsync();
        await engine.ConfigureAsync(Config());
        Assert.Equal(1, process.CloseRequests);
        if (reenable) { await engine.ConfigureAsync(config); await engine.TickAsync(); }
        process.CloseResult = ProcessCloseResult.Closed;
        clock.Advance(20); await engine.TickAsync();
        Assert.Equal(reenable ? 0 : 1, process.Closes);
        Assert.Equal(reenable ? 1 : 2, process.CloseRequests);
    }

    [Fact]
    public async Task RefusedCloseStopsFollowingStepsAndReportsItWhenRuleIsDisabled()
    {
        var process = new FakeProcesses { CloseResult = ProcessCloseResult.ConfirmationRequired }; var clock = new ManualClock(); process.External(ParentPath);
        var link = Link(new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 1 },
            new() { Action = FlowAction.Launch });
        var parent = Parent(link);
        await using var engine = new AutomationEngine(process, clock);
        EngineStatus? status = null; engine.StatusChanged += s => status = s;
        await engine.ConfigureAsync(Config(parent)); await engine.TickAsync();
        clock.Advance(1); await engine.TickAsync();
        Assert.Equal(1, process.Launches);
        await engine.ConfigureAsync(Config(parent with { Links = [link with { Enabled = false }] }));
        Assert.Contains("подтверждение", status!.LinkMessages[link.Id]);
        Assert.Equal(0, process.Closes);
    }

    [Theory]
    [InlineData(ProcessCloseResult.Closed)]
    [InlineData(ProcessCloseResult.Forced)]
    public async Task CompletedCloseReleasesOwnershipReportsResultAndLetsCycleContinue(ProcessCloseResult result)
    {
        var process = new FakeProcesses { CloseResult = result }; var clock = new ManualClock(); process.External(ParentPath);
        var link = Link() with { Trigger = ParentEvent.Running, RepeatSeconds = 2,
            Steps = [new() { Action = FlowAction.Launch }, new() { Action = FlowAction.Close, DelaySeconds = 1 }] };
        await using var engine = new AutomationEngine(process, clock);
        EngineStatus? status = null; engine.StatusChanged += s => status = s;
        var activities = new List<ExecutionActivity>(); engine.ActivityOccurred += activities.Add;
        await engine.ConfigureAsync(Config(Parent(link))); await engine.TickAsync();
        clock.Advance(1); await engine.TickAsync();
        Assert.DoesNotContain(process.Running, p => p.Executable == TargetPath);
        Assert.Equal(result == ProcessCloseResult.Forced, status!.LinkMessages[link.Id].Contains("принудительно"));
        Assert.Single(activities, a => a.RuleId == link.Id && a.Action == FlowAction.Close);
        clock.Advance(2); await engine.TickAsync(); Assert.Equal(2, process.Launches);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void JumpWallClock(double seconds) => now = now.AddSeconds(seconds);
        public void Advance(double seconds) { now = now.AddSeconds(seconds); timestamp += (long)(seconds * TimeSpan.TicksPerSecond); }
    }
    private sealed class FakeProcesses : IProcessService
    {
        public List<ProcessIdentity> Running { get; } = [];
        private int next = 10;
        public int Launches { get; private set; }
        public int Closes { get; private set; }
        public int CloseRequests { get; private set; }
        public ProcessCloseResult CloseResult { get; set; } = ProcessCloseResult.Closed;
        public Dictionary<string, ProcessCloseResult> CloseResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource? CancelOnClose { get; set; }
        public Action? AfterClose { get; set; }
        public string? FailPath { get; set; }
        public bool DelayLaunch { get; set; }
        public List<string> WebsitesOpened { get; } = [];
        public int ScriptLaunches { get; private set; }
        public int Snapshots { get; private set; }
        public Task OpenWebsiteAsync(string address, CancellationToken cancellationToken) { WebsitesOpened.Add(address); return Task.CompletedTask; }
        public Task<ProcessIdentity?> LaunchScriptIfAbsentAsync(string path, CancellationToken cancellationToken) { ScriptLaunches++; return LaunchIfAbsentAsync(path, cancellationToken); }
        public ProcessIdentity External(string path) { var identity = new ProcessIdentity(next++, next * 100L, path); Running.Add(identity); return identity; }
        public void Remove(string path) => Running.RemoveAll(p => p.Executable == path);
        public Task<ProcessSnapshot> SnapshotAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
        { Snapshots++; return Task.FromResult(new ProcessSnapshot(Running.ToArray())); }
        public async Task<ProcessIdentity?> LaunchIfAbsentAsync(string executable, CancellationToken cancellationToken)
        {
            if (executable == FailPath) throw new FileNotFoundException("Файл недоступен");
            if (Running.Any(p => p.Executable == executable)) return null;
            if (DelayLaunch) await Task.Yield();
            Launches++; return External(executable);
        }
        public Task<bool> IsSameProcessAsync(ProcessIdentity identity, CancellationToken cancellationToken) => Task.FromResult(Running.Contains(identity));
        public Task<bool> CloseAsync(ProcessIdentity identity, CancellationToken cancellationToken)
        {
            if (Running.Remove(identity)) { Closes++; AfterClose?.Invoke(); }
            return Task.FromResult(true);
        }
        public async Task<ProcessCloseResult> CloseWithResultAsync(ProcessIdentity identity, CancellationToken cancellationToken)
        {
            CloseRequests++;
            CancelOnClose?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
            var result = CloseResults.GetValueOrDefault(identity.Executable, CloseResult);
            if (result is not (ProcessCloseResult.Closed or ProcessCloseResult.Forced)) return result;
            return await CloseAsync(identity, cancellationToken) ? result : ProcessCloseResult.StillRunning;
        }
    }
}

