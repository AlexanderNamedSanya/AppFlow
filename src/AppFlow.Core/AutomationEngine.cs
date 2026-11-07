namespace AppFlow.Core;

// All state changes and process operations are serialized; no per-rule timer threads.
public sealed class AutomationEngine(IProcessService processes, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim monitorWake = new(0, 1);
    private readonly Dictionary<Guid, Run> runs = [];
    private readonly Dictionary<string, OwnedProcess> owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessIdentity, AppLink> pendingReleases = [];
    private readonly Dictionary<Guid, string> messages = [];
    private HashSet<Guid> active = [];
    private readonly HashSet<Guid> rearm = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset Since)> suspendedTargets = new(StringComparer.OrdinalIgnoreCase);
    private AppConfiguration configuration = new();
    private string[] parentPaths = [];
    private EngineStatus? lastStatus;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    public event Action<EngineStatus>? StatusChanged;
    public event Action<ExecutionActivity>? ActivityOccurred;
    private DateTimeOffset CurrentTime => DateTimeOffset.UnixEpoch + time.GetElapsedTime(0, time.GetTimestamp());

    public async Task<IAsyncDisposable> SuspendTargetAsync(string executable, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            suspendedTargets[executable] = suspendedTargets.TryGetValue(executable, out var existing)
                ? (existing.Count + 1, existing.Since) : (1, CurrentTime);
            return new TargetSuspension(this, executable);
        }
        finally { gate.Release(); }
    }

    private async ValueTask ResumeTargetAsync(string executable)
    {
        await gate.WaitAsync();
        try
        {
            if (!suspendedTargets.TryGetValue(executable, out var suspension)) return;
            if (suspension.Count > 1) { suspendedTargets[executable] = (suspension.Count - 1, suspension.Since); return; }
            suspendedTargets.Remove(executable);
            var now = CurrentTime;
            foreach (var run in runs.Values.Where(r => string.Equals(r.Link.Executable, executable, StringComparison.OrdinalIgnoreCase)))
            {
                var paused = now - (run.CreatedAt > suspension.Since ? run.CreatedAt : suspension.Since);
                run.Due += paused;
                if (run.RestartAt is { } restart) run.RestartAt = restart + paused;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> LaunchManuallyAsync(string executable, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            // A direct user launch is not an automation lease. Timers must not adopt it.
            if (ActionTarget.Kind(executable) == TargetKind.Website)
            {
                await processes.OpenWebsiteAsync(ActionTarget.Website(executable), ct);
                return true;
            }
            return await LaunchProcessAsync(executable, ct) is not null;
        }
        finally { gate.Release(); }
    }

    public async Task ConfigureAsync(AppConfiguration next, CancellationToken ct = default)
    {
        next.Validate();
        next = next with { Parents = next.Parents.Select(p => p with { Links = p.Links.SelectMany(l => l.ExecutionRules()).ToList() }).ToList() };
        await gate.WaitAsync(ct);
        try
        {
            var previous = configuration.Parents.SelectMany(p => p.Links.Select(l => (Parent: p, Link: l))).ToDictionary(x => x.Link.Id);
            var following = next.Parents.SelectMany(p => p.Links.Select(l => (Parent: p, Link: l))).ToDictionary(x => x.Link.Id);
            foreach (var (id, entry) in previous)
            {
                if (following.TryGetValue(id, out var replacement) && entry.Parent.Executable == replacement.Parent.Executable
                    && System.Text.Json.JsonSerializer.Serialize(entry.Link) == System.Text.Json.JsonSerializer.Serialize(replacement.Link)) continue;
                var release = ReleaseOutcome.Skipped;
                if (entry.Link.Steps.Any(s => s.Action == FlowAction.Launch)
                    || !following.Values.Any(x => x.Link.ClaimId == entry.Link.ClaimId && x.Link.Enabled))
                    release = await ReleaseAsync(entry.Link, ct);
                if (release == ReleaseOutcome.Deferred && owned.TryGetValue(entry.Link.Executable, out var deferred))
                    pendingReleases[deferred.Identity] = entry.Link;
                runs.Remove(id);
                if (release is not (ReleaseOutcome.Blocked or ReleaseOutcome.Deferred)) messages.Remove(entry.Link.ClaimId);
                if (following.ContainsKey(id)) rearm.Add(id);
            }
            foreach (var id in following.Keys.Except(previous.Keys)) rearm.Add(id);
            configuration = next;
            foreach (var pending in pendingReleases.ToArray())
                if (following.Values.Any(entry => entry.Link.Enabled && entry.Link.ClaimId == pending.Value.ClaimId
                    && string.Equals(entry.Link.Executable, pending.Value.Executable, StringComparison.OrdinalIgnoreCase)))
                    pendingReleases.Remove(pending.Key);
            parentPaths = next.Parents.Select(p => p.Executable).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            active.IntersectWith(next.Parents.Select(p => p.Id));
            lastStatus = null; // Configuration changes also affect disabled-link labels in the UI.
            if (monitorWake.CurrentCount == 0) monitorWake.Release();
        }
        finally { gate.Release(); }
    }

    public void Start()
    {
        if (loop is not null) return;
        lifetime = new();
        loop = Task.Run(() => MonitorAsync(lifetime.Token));
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // No timer or process enumeration while there is nothing to monitor.
                monitorWake.Wait(0);
                try { await TickAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Publish(ex.Message); }
                await monitorWake.WaitAsync(parentPaths.Length == 0 && owned.Count == 0
                    ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = parentPaths.Length == 0 ? new ProcessSnapshot([]) : await processes.SnapshotAsync(parentPaths, ct);
            foreach (var entry in owned.ToArray())
                if (!await processes.IsSameProcessAsync(entry.Value.Identity, ct))
                {
                    foreach (var id in entry.Value.Claims) messages[id] = "Программа завершилась самостоятельно";
                    owned.Remove(entry.Key);
                }
            // Monotonic time: changing Windows time must not shorten a rule's delay.
            var now = CurrentTime;
            foreach (var pending in pendingReleases.ToArray())
            {
                if (!owned.TryGetValue(pending.Value.Executable, out var lease) || lease.Identity != pending.Key
                    || !lease.Claims.Contains(pending.Value.ClaimId)) { pendingReleases.Remove(pending.Key); continue; }
                if (suspendedTargets.ContainsKey(pending.Value.Executable) || lease.RetryAt > now) continue;
                try
                {
                    if (await ReleaseAsync(pending.Value, ct) != ReleaseOutcome.Deferred) pendingReleases.Remove(pending.Key);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { messages[pending.Value.ClaimId] = ex.Message; pendingReleases.Remove(pending.Key); }
            }
            foreach (var parent in configuration.Parents)
            {
                var running = snapshot.Contains(parent.Executable);
                var wasRunning = active.Contains(parent.Id);
                var transition = running != wasRunning;
                if (running) active.Add(parent.Id); else active.Remove(parent.Id);
                if (transition) ActivityOccurred?.Invoke(new(parent.Id, null, running ? ParentEvent.Started : ParentEvent.Exited,
                    running ? FlowAction.Launch : FlowAction.Close));
                foreach (var link in parent.Links.Where(l => l.Enabled))
                {
                    var edited = rearm.Remove(link.Id);
                    if (!transition && !(edited && running && link.Trigger != ParentEvent.Exited)) continue;
                    if (transition && running && link.Trigger == ParentEvent.Exited) runs.Remove(link.Id);
                    if (transition && !running && link.Trigger != ParentEvent.Exited)
                    {
                        // Retain already scheduled closes at their original deadlines.
                        // Main exit itself never grants an implicit close action.
                        if (runs.TryGetValue(link.Id, out var pending) && pending.RestartAt is null
                            && (pending.Index > 0 || link.Steps.All(s => s.Action != FlowAction.Launch))
                            && link.Steps.Skip(pending.Index).Any(s => s.Action == FlowAction.Close))
                        {
                            pending.ClosingOnly = true;
                            pending.ClosingIdentity = owned.TryGetValue(link.Executable, out var tracked) && tracked.Claims.Contains(link.ClaimId) ? tracked.Identity : null;
                        }
                        else runs.Remove(link.Id);
                    }
                    if (running == (link.Trigger != ParentEvent.Exited))
                    {
                        runs[link.Id] = new(parent.Id, link, now);
                        messages[link.ClaimId] = "Ожидание действия";
                    }
                }
            }
            foreach (var run in runs.Values.ToArray())
            {
                if (!runs.ContainsKey(run.Link.Id)) continue;
                if (suspendedTargets.ContainsKey(run.Link.Executable)) continue;
                // A postponed request belongs to one exact launch, never a later replacement.
                if (run.RetryIdentity is { } retryIdentity && (!owned.TryGetValue(run.Link.Executable, out var retryProcess)
                    || retryProcess.Identity != retryIdentity || !retryProcess.Claims.Contains(run.Link.ClaimId)))
                { runs.Remove(run.Link.Id); continue; }
                try
                {
                    if (run.RestartAt is { } restart && now >= restart)
                    {
                        run.Index = 0;
                        run.Due = now.AddSeconds(run.Link.Steps[0].DelaySeconds);
                        run.RestartAt = null;
                    }
                    while (run.RestartAt is null && run.Index < run.Link.Steps.Count && now >= run.Due)
                    {
                        var step = run.Link.Steps[run.Index];
                        var performed = false;
                        if (step.Action == FlowAction.Launch && !run.ClosingOnly) performed = await AcquireAsync(run.Link, ct);
                        else if (step.Action == FlowAction.Close && (!run.ClosingOnly
                            || run.ClosingIdentity is not null && owned.TryGetValue(run.Link.Executable, out var tracked) && tracked.Identity == run.ClosingIdentity))
                        {
                            var release = await ReleaseAsync(run.Link, ct);
                            performed = release == ReleaseOutcome.Closed;
                            if (release == ReleaseOutcome.Deferred)
                            {
                                run.RetryIdentity = owned[run.Link.Executable].Identity;
                                run.Due = owned[run.Link.Executable].RetryAt;
                                break;
                            }
                            if (release == ReleaseOutcome.Blocked)
                            {
                                // Pause when the verified instance has not completed its graceful exit.
                                foreach (var id in runs.Where(r => r.Value.Link.ClaimId == run.Link.ClaimId).Select(r => r.Key).ToArray()) runs.Remove(id);
                                break;
                            }
                        }
                        if (performed) ActivityOccurred?.Invoke(new(run.ParentId, run.Link.Id, run.Link.Trigger, step.Action));
                        run.RetryIdentity = null;
                        run.Index++;
                        if (run.Index < run.Link.Steps.Count) run.Due = CurrentTime.AddSeconds(run.Link.Steps[run.Index].DelaySeconds);
                    }
                    if (run.Index == run.Link.Steps.Count && run.RestartAt is null)
                    {
                        if (run.Link.RepeatSeconds is double interval && active.Contains(run.ParentId) && !run.ClosingOnly)
                            run.RestartAt = CurrentTime.AddSeconds(interval);
                        else runs.Remove(run.Link.Id);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { messages[run.Link.ClaimId] = ex.Message; runs.Remove(run.Link.Id); }
            }
            Publish();
        }
        finally { gate.Release(); }
    }

    private async Task<bool> AcquireAsync(AppLink link, CancellationToken ct)
    {
        if (link.Kind == TargetKind.Website)
        {
            await processes.OpenWebsiteAsync(ActionTarget.Website(link.Executable), ct);
            messages[link.ClaimId] = "Сайт открыт";
            return true;
        }
        if (owned.TryGetValue(link.Executable, out var existing))
        {
            if (await processes.IsSameProcessAsync(existing.Identity, ct))
            {
                existing.Claims.Add(link.ClaimId);
                messages[link.ClaimId] = "Запущено AppFlow · общая связь";
                return false;
            }
            owned.Remove(link.Executable);
        }
        var identity = await LaunchProcessAsync(link.Executable, ct);
        if (identity is null) { messages[link.ClaimId] = "Уже работает · без вмешательства"; return false; }
        owned[link.Executable] = new(identity, [link.ClaimId]);
        messages[link.ClaimId] = "Запущено AppFlow";
        return true;
    }

    private Task<ProcessIdentity?> LaunchProcessAsync(string target, CancellationToken ct) => ActionTarget.Kind(target) == TargetKind.Script
        ? processes.LaunchScriptIfAbsentAsync(target, ct) : processes.LaunchIfAbsentAsync(target, ct);

    private async Task<ReleaseOutcome> ReleaseAsync(AppLink link, CancellationToken ct)
    {
        if (suspendedTargets.ContainsKey(link.Executable))
        {
            messages[link.ClaimId] = "Обучение закрытию · автоматизация приостановлена";
            return ReleaseOutcome.Blocked;
        }
        if (!owned.TryGetValue(link.Executable, out var process) || !process.Claims.Remove(link.ClaimId)) return ReleaseOutcome.Skipped;
        if (process.Claims.Count != 0) { messages[link.ClaimId] = "Используется другой связью"; return ReleaseOutcome.Skipped; }
        // Keep the claim even when the request throws or waiting is cancelled.
        process.Claims.Add(link.ClaimId);
        if (process.RetryAt > CurrentTime) return ReleaseOutcome.Deferred;
        messages[link.ClaimId] = "Ожидание штатного закрытия…";
        Publish();
        var result = await processes.CloseWithResultAsync(process.Identity, ct);
        if (result is ProcessCloseResult.Closed or ProcessCloseResult.Forced)
        {
            owned.Remove(link.Executable);
            messages[link.ClaimId] = result == ProcessCloseResult.Forced
                ? link.Kind == TargetKind.Script ? "Скрипт остановлен принудительно" : "Завершено принудительно"
                : "Завершено";
            return ReleaseOutcome.Closed;
        }
        else
        {
            if (result == ProcessCloseResult.Deferred)
            {
                process.CloseAttempts = Math.Min(process.CloseAttempts + 1, 4);
                process.RetryAt = CurrentTime.AddSeconds(Math.Min(15, 1 << process.CloseAttempts));
                messages[link.ClaimId] = "Закрытие отложено: ожидаю свободный трей. Повторю автоматически.";
                Publish();
                return ReleaseOutcome.Deferred;
            }
            messages[link.ClaimId] = result switch
            {
                ProcessCloseResult.ConfirmationRequired => "Нужно подтверждение в окне программы. Сохраните данные или отмените закрытие.",
                ProcessCloseResult.CloseNotLearned => "Способ закрытия не обучен или программа обновилась. Нажмите «Обучить закрытию».",
                ProcessCloseResult.AccessDenied => "Нет доступа для закрытия. Проверьте права обеих программ.",
                ProcessCloseResult.CloseUnavailable => "Обученный способ закрытия сейчас недоступен. Проверьте состояние программы или повторите обучение.",
                _ => "Программа продолжает работать после запроса закрытия. Проверьте её окно или команду «Выход» в трее."
            } + " Действия связи приостановлены.";
            Publish();
            return ReleaseOutcome.Blocked;
        }
    }

    private void Publish(string? error = null)
    {
        var handler = StatusChanged;
        if (handler is null) return;
        if (lastStatus is { } previous && previous.Error == error && active.SetEquals(previous.RunningParents)
            && messages.Count == previous.LinkMessages.Count
            && messages.All(entry => previous.LinkMessages.TryGetValue(entry.Key, out var value) && value == entry.Value)) return;
        lastStatus = new(new HashSet<Guid>(active), new Dictionary<Guid, string>(messages), error);
        handler(lastStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetime is not null) await lifetime.CancelAsync();
        if (loop is not null) await loop;
        lifetime?.Dispose();
        // Exiting AppFlow leaves applications running; no surprise termination on exit.
        monitorWake.Dispose();
        gate.Dispose();
    }

    private sealed class Run(Guid parentId, AppLink link, DateTimeOffset now)
    {
        public DateTimeOffset CreatedAt { get; } = now;
        public Guid ParentId { get; } = parentId;
        public AppLink Link { get; } = link;
        public int Index { get; set; }
        public DateTimeOffset Due { get; set; } = now.AddSeconds(link.Steps[0].DelaySeconds);
        public DateTimeOffset? RestartAt { get; set; }
        public bool ClosingOnly { get; set; }
        public ProcessIdentity? ClosingIdentity { get; set; }
        public ProcessIdentity? RetryIdentity { get; set; }
    }
    private sealed record OwnedProcess(ProcessIdentity Identity, HashSet<Guid> Claims)
    {
        public DateTimeOffset RetryAt { get; set; }
        public int CloseAttempts { get; set; }
    }
    private enum ReleaseOutcome { Skipped, Closed, Blocked, Deferred }
    private sealed class TargetSuspension(AutomationEngine engine, string executable) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) await engine.ResumeTargetAsync(executable);
        }
    }
}
