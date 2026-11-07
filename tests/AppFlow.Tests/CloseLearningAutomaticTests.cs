using AppFlow.Core;
using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

public sealed partial class StorageAndProcessTests
{
    [Fact]
    public async Task AutomaticLearningTestsLaunchEachMethodAndAppendVerifiedProfilesWithoutDesktopInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-auto-learning-" + Guid.NewGuid());
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string executable;
            await using (var seed = await TestApplicationWindow.StartAsync("native-menu")) executable = seed.Process.MainModule!.FileName;
            var hash = await CloseProfileStore.FingerprintAsync(executable, limit.Token);
            ExitRecipe Recipe(string label) => new() { Window = new("AppFlowDisposableTestWindow", "AppFlow test"), CommandId = 42017, Label = label };
            var store = new CloseProfileStore(Path.Combine(directory, "profiles.json"));
            await store.SaveAsync(new() { Executable = executable, FileHash = hash, Recipe = Recipe("Existing learned method"),
                BackgroundVerified = true, DirectVerified = true, VerifiedAt = DateTimeOffset.UtcNow }, limit.Token);
            await using var backend = new NativeLearningBackend(store);
            var result = await new CloseLearningSession(backend).TestAndSaveAsync([
                new(Recipe("First recorded method"), 0, hash), new(Recipe("Second recorded method"), 0, hash)
            ], new LearningProgressSink(), limit.Token);
            Assert.Equal(2, result.Saved); Assert.Empty(result.Errors);
            Assert.Equal(2, backend.Launches); Assert.Equal(1, backend.Commits);
            Assert.Equal(3, (await new CloseProfileStore(store.FilePath).FindAllAsync(executable, limit.Token)).Count);
            Assert.All(backend.Windows, window =>
            {
                Assert.True(window.Process.HasExited); Assert.Equal(0, window.Process.ExitCode);
                Assert.Equal("saved before normal exit", File.ReadAllText(window.Marker));
                Assert.False(File.Exists(window.Marker + ".requests"));
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class LearningProgressSink : IProgress<LearningTestProgress> { public void Report(LearningTestProgress value) { } }
    private sealed class NativeLearningBackend(CloseProfileStore store) : ICloseLearningBackend, IAsyncDisposable
    {
        private readonly WindowsProcessService adapter = new();
        private readonly Dictionary<ProcessIdentity, TestApplicationWindow> launches = [];
        public IReadOnlyCollection<TestApplicationWindow> Windows => launches.Values;
        public int Launches => launches.Count;
        public int Commits { get; private set; }
        public Task<IReadOnlyList<LearningProcess>> RunningAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LearningProcess>>(
            launches.Where(p => !p.Value.Process.HasExited).Select(p => new LearningProcess(p.Key, "Native fixture")).ToArray());
        public async Task<ProcessIdentity?> LaunchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var window = await TestApplicationWindow.StartAsync("native-menu");
            var identity = await window.IdentityAsync(adapter);
            launches.Add(identity, window);
            return identity;
        }
        private CloseLearningService Service(ProcessIdentity identity) => new(identity.Executable, adapter, store);
        public Task<IReadOnlyList<ProcessIdentity>> OwnedAsync(ProcessIdentity launch, CancellationToken ct) => Service(launch).OwnedAsync(launch, ct);
        public Task<ProcessIdentity> ReadyAsync(ProcessIdentity launch, ExitRecipe recipe, CancellationToken ct) => Service(launch).ReadyAsync(launch, recipe, ct);
        public Task<LearnedExit> RecordAsync(ProcessIdentity identity, CancellationToken ct, Action? started = null) => throw new NotSupportedException();
        public Task<CloseProfile> VerifyAsync(LearnedExit learned, ProcessIdentity identity, ExitRoute route, CancellationToken ct,
            Action<string>? trace = null, Action? requestIssued = null) => Service(identity).VerifyAsync(learned, identity, route, ct, trace, requestIssued);
        public async Task SaveManyAsync(IReadOnlyCollection<CloseProfile> profiles, CancellationToken ct)
        { await store.SaveManyAsync(profiles, ct); Commits++; }
        public async ValueTask DisposeAsync()
        { foreach (var window in launches.Values) await window.DisposeAsync(); }
    }
}
