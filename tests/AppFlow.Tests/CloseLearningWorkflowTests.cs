using System.Threading.Channels;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class CloseLearningWorkflowTests
{
    private static LearnedExit Exit(uint command, bool tray = false) => new(new()
    {
        Window = new("TestWindow", "Test"), Label = $"Exit {command}", CommandId = command,
        TrayIcon = tray ? new("Test icon", "", 50000) : null
    }, 0, new string('A', 64));

    [Fact]
    public async Task EveryRecordedMethodIsTestedAndSavedTogether()
    {
        var backend = new Backend();
        var progress = new List<LearningTestProgress>();
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1), Exit(2)], new InlineProgress<LearningTestProgress>(progress.Add), default);
        Assert.Equal(2, result.Saved); Assert.Empty(result.Errors);
        Assert.Equal(2, backend.Launches);
        Assert.Equal(new[] { (1u, ExitRoute.Direct), (2u, ExitRoute.Direct) }, backend.Attempts);
        Assert.Equal(2, backend.Saved.Count); Assert.Equal(1, backend.Commits);
        Assert.Equal(new[] { 1, 2 }, progress.Select(p => p.Current));
    }

    [Fact]
    public async Task AutomaticTestsChooseTrayOrVisibleWindowWhenBackgroundCommandIsUnavailable()
    {
        var backend = new Backend();
        backend.Failures[(1, ExitRoute.Direct)] = (false, true);
        backend.Failures[(2, ExitRoute.Direct)] = (false, true);
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1, true), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), default);
        Assert.Equal(2, result.Saved);
        Assert.Equal(new[] { (1u, ExitRoute.Direct), (1u, ExitRoute.Tray), (2u, ExitRoute.Direct), (2u, ExitRoute.VisibleWindow) }, backend.Attempts);
        Assert.True(backend.Saved[0].BackgroundVerified); Assert.False(backend.Saved[0].DirectVerified);
        Assert.True(backend.Saved[1].VisibleVerified);
    }

    [Fact]
    public async Task AnIssuedButUnfinishedExitCannotBeCreditedToAnotherMethod()
    {
        var backend = new Backend();
        backend.Failures[(1, ExitRoute.Direct)] = (true, true);
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1, true), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), default);
        Assert.Single(backend.Attempts); Assert.Equal(1, backend.Launches);
        Assert.Empty(backend.Saved); Assert.Equal(2, result.Errors.Count);
        Assert.Single(await backend.RunningAsync(default));
    }

    [Fact]
    public async Task AMethodThatSendsNothingDoesNotPreventTestingTheNextMethod()
    {
        var backend = new Backend();
        backend.Failures[(1, ExitRoute.Direct)] = (false, true);
        backend.Failures[(1, ExitRoute.VisibleWindow)] = (false, true);
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), default);
        Assert.Equal(1, result.Saved); Assert.Single(result.Errors);
        Assert.Equal(2u, Assert.Single(backend.Saved).Recipe.CommandId);
        Assert.Equal(1, backend.Launches);
    }

    [Fact]
    public async Task ARemainingOwnedProcessPreventsSavingAFalseSuccess()
    {
        var backend = new Backend { RemainAlive = true };
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), default);
        Assert.Equal(0, result.Saved); Assert.Empty(backend.Saved);
        Assert.Single(backend.Attempts); Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task ExistingUserInstancesAreNeverUsedByAutomaticTests()
    {
        var backend = new Backend(); var external = backend.External();
        var result = await new CloseLearningSession(backend).TestAndSaveAsync([Exit(1), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), default);
        Assert.Equal(0, backend.Launches); Assert.Empty(backend.Attempts); Assert.Empty(backend.Saved);
        Assert.Equal(external, Assert.Single(await backend.RunningAsync(default)).Identity);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task CancellationBeforeCommitDoesNotSaveAPartialSession()
    {
        using var cancel = new CancellationTokenSource();
        var backend = new Backend { Verified = _ => cancel.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CloseLearningSession(backend)
            .TestAndSaveAsync([Exit(1), Exit(2)], new InlineProgress<LearningTestProgress>(_ => { }), cancel.Token));
        Assert.Equal(0, backend.Commits); Assert.Empty(backend.Saved);
    }

    [Fact]
    public async Task ObservationAcceptsAnyDemonstratedInstanceAndStopsTheOtherObservers()
    {
        var backend = new Backend(); backend.External(); backend.External();
        var observing = new CloseLearningSession(backend).ObserveAsync(new InlineProgress<bool>(_ => { }), default);
        var first = await backend.Records.Reader.ReadAsync();
        var second = await backend.Records.Reader.ReadAsync();
        second.Result.SetResult(Exit(2));
        Assert.Equal(2u, (await observing.WaitAsync(TimeSpan.FromSeconds(3))).Recipe.CommandId);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Single(await backend.RunningAsync(default));
    }

    [Fact]
    public Task WindowStartsRecordingRetainsEarlierMethodsAndFinishesWithoutManualTestButtons() => AutoSaveTests.OnDispatcher(async () =>
    {
        var backend = new Backend(); backend.External();
        using var viewModel = new CloseLearningViewModel(new(backend), "Test app");
        var firstRecording = viewModel.InitializeAsync();
        var first = await backend.Records.Reader.ReadAsync();
        Assert.True(viewModel.IsLearning); Assert.True(viewModel.ShowProgress);
        first.Result.SetResult(Exit(1)); await firstRecording;
        Assert.True(viewModel.IsRecorded); Assert.Equal(1, viewModel.LearnedCount);
        backend.External();
        viewModel.AddMethodCommand.Execute(null);
        var second = await backend.Records.Reader.ReadAsync();
        Assert.True(viewModel.IsLearning); Assert.Equal(1, viewModel.LearnedCount);
        second.Result.SetResult(Exit(2));
        await UntilAsync(() => viewModel.IsRecorded);
        Assert.Equal(2, viewModel.LearnedCount);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Completed += () => completed.SetResult();
        viewModel.FinishCommand.Execute(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, backend.Saved.Count); Assert.Contains("2", viewModel.ResultMessage);
        Assert.False(viewModel.ShowProgress); Assert.False(viewModel.CanLaunch);
        await viewModel.StopAsync();
    });

    [Fact]
    public Task LaunchButtonWorksWhileWaitingForAManualDemonstration() => AutoSaveTests.OnDispatcher(async () =>
    {
        var backend = new Backend();
        using var viewModel = new CloseLearningViewModel(new(backend), "Test app");
        var observing = viewModel.InitializeAsync();
        Assert.True(viewModel.CanLaunch); Assert.Equal("Запустить Test app", viewModel.LaunchLabel);
        viewModel.LaunchCommand.Execute(null);
        var recording = await backend.Records.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        recording.Result.SetResult(Exit(1)); await observing;
        Assert.Equal(1, backend.Launches); Assert.True(viewModel.IsRecorded);
        await viewModel.StopAsync();
    });

    [Fact]
    public Task CancellingTheWindowStopsRecordingWithoutStartingTests() => AutoSaveTests.OnDispatcher(async () =>
    {
        var backend = new Backend(); backend.External();
        using var viewModel = new CloseLearningViewModel(new(backend), "Test app");
        var observing = viewModel.InitializeAsync();
        var recording = await backend.Records.Reader.ReadAsync();
        await viewModel.StopAsync(); await observing;
        Assert.True(recording.Token.IsCancellationRequested);
        Assert.False(viewModel.ShowProgress); Assert.Empty(backend.Attempts); Assert.Empty(backend.Saved);
    });

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, limit.Token);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed record Recording(CancellationToken Token, TaskCompletionSource<LearnedExit> Result);
    private sealed class Backend : ICloseLearningBackend
    {
        private readonly object sync = new();
        private readonly List<ProcessIdentity> running = [];
        private readonly HashSet<ProcessIdentity> owned = [];
        private int nextPid;
        public int Launches { get; private set; }
        public int Commits { get; private set; }
        public Action<CloseProfile>? Verified { get; init; }
        public bool RemainAlive { get; init; }
        public Channel<Recording> Records { get; } = Channel.CreateUnbounded<Recording>();
        public List<(uint, ExitRoute)> Attempts { get; } = [];
        public List<CloseProfile> Saved { get; } = [];
        public Dictionary<(uint, ExitRoute), (bool Issued, bool Alive)> Failures { get; } = [];
        public ProcessIdentity External()
        {
            lock (sync)
            {
                var identity = new ProcessIdentity(++nextPid, nextPid, Path.GetFullPath("learning-test.exe"));
                running.Add(identity); return identity;
            }
        }
        public Task<IReadOnlyList<LearningProcess>> RunningAsync(CancellationToken ct)
        { lock (sync) return Task.FromResult<IReadOnlyList<LearningProcess>>(running.Select(p => new LearningProcess(p, "Test app")).ToArray()); }
        public Task<ProcessIdentity?> LaunchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (sync) { Launches++; var identity = External(); owned.Add(identity); return Task.FromResult<ProcessIdentity?>(identity); }
        }
        public Task<IReadOnlyList<ProcessIdentity>> OwnedAsync(ProcessIdentity launch, CancellationToken ct)
        { lock (sync) return Task.FromResult<IReadOnlyList<ProcessIdentity>>(owned.Contains(launch) && running.Contains(launch) ? [launch] : []); }
        public Task<ProcessIdentity> ReadyAsync(ProcessIdentity launch, ExitRecipe recipe, CancellationToken ct) => Task.FromResult(launch);
        public async Task<LearnedExit> RecordAsync(ProcessIdentity identity, CancellationToken ct, Action? started = null)
        {
            var request = new Recording(ct, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Records.Writer.TryWrite(request); started?.Invoke();
            var result = await request.Result.Task.WaitAsync(ct);
            lock (sync) running.Remove(identity);
            return result;
        }
        public Task<CloseProfile> VerifyAsync(LearnedExit learned, ProcessIdentity identity, ExitRoute route, CancellationToken ct,
            Action<string>? trace = null, Action? requestIssued = null)
        {
            ct.ThrowIfCancellationRequested();
            Attempts.Add((learned.Recipe.CommandId, route));
            if (Failures.TryGetValue((learned.Recipe.CommandId, route), out var failure))
            {
                if (failure.Issued) requestIssued?.Invoke();
                if (!failure.Alive) { lock (sync) running.Remove(identity); }
                throw new InvalidOperationException("Test route unavailable");
            }
            requestIssued?.Invoke();
            if (!RemainAlive) { lock (sync) running.Remove(identity); }
            var profile = new CloseProfile { Executable = identity.Executable, FileHash = learned.FileHash, Recipe = learned.Recipe,
                BackgroundVerified = route != ExitRoute.VisibleWindow, DirectVerified = route == ExitRoute.Direct,
                VisibleVerified = route == ExitRoute.VisibleWindow, VerifiedAt = DateTimeOffset.UtcNow };
            Verified?.Invoke(profile);
            return Task.FromResult(profile);
        }
        public Task SaveManyAsync(IReadOnlyCollection<CloseProfile> profiles, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Commits++; Saved.AddRange(profiles); return Task.CompletedTask; }
    }
}

public sealed class CloseProfileBatchTests
{
    [Fact]
    public async Task BatchPreservesExistingAndAllNewMethodsAndMergesVerifiedStates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-batch-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "app.exe");
            await File.WriteAllTextAsync(executable, "test version");
            var hash = await CloseProfileStore.FingerprintAsync(executable);
            CloseProfile Profile(uint id) => new() { Executable = executable, FileHash = hash, Recipe = new()
                { Window = new("TestWindow", "Test"), CommandId = id, Label = $"Exit {id}" },
                BackgroundVerified = true, DirectVerified = true, VerifiedAt = DateTimeOffset.UtcNow };
            var path = Path.Combine(directory, "profiles.json");
            var store = new CloseProfileStore(path);
            await store.SaveAsync(Profile(1));
            await store.SaveManyAsync(Enumerable.Range(2, 10).Select(id => Profile((uint)id)).ToArray());
            Assert.Equal(11, (await new CloseProfileStore(path).FindAllAsync(executable)).Count);
            await store.SaveAsync(Profile(1) with { BackgroundVerified = false, DirectVerified = false, VisibleVerified = true });
            var merged = Assert.Single(await store.FindAllAsync(executable), p => p.Recipe.CommandId == 1);
            Assert.True(merged.DirectVerified); Assert.True(merged.BackgroundVerified); Assert.True(merged.VisibleVerified);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InvalidOrCancelledBatchLeavesPreviouslySavedProfilesUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-batch-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "app.exe");
            await File.WriteAllTextAsync(executable, "test version");
            var profile = new CloseProfile { Executable = executable, FileHash = await CloseProfileStore.FingerprintAsync(executable),
                Recipe = new() { Window = new("TestWindow", "Test"), CommandId = 1, Label = "Exit" },
                BackgroundVerified = true, DirectVerified = true, VerifiedAt = DateTimeOffset.UtcNow };
            var path = Path.Combine(directory, "profiles.json");
            var store = new CloseProfileStore(path);
            await store.SaveAsync(profile);
            var before = await File.ReadAllTextAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveManyAsync([profile,
                profile with { BackgroundVerified = false, DirectVerified = false }]));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveManyAsync([profile], cancel.Token));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
