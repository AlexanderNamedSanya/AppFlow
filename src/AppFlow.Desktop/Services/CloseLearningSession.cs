using AppFlow.Core;

namespace AppFlow.Desktop.Services;

internal interface ICloseLearningBackend
{
    Task<IReadOnlyList<LearningProcess>> RunningAsync(CancellationToken ct);
    Task<ProcessIdentity?> LaunchAsync(CancellationToken ct);
    Task<IReadOnlyList<ProcessIdentity>> OwnedAsync(ProcessIdentity launch, CancellationToken ct);
    Task<ProcessIdentity> ReadyAsync(ProcessIdentity launch, ExitRecipe recipe, CancellationToken ct);
    Task<LearnedExit> RecordAsync(ProcessIdentity identity, CancellationToken ct, Action? started = null);
    Task<CloseProfile> VerifyAsync(LearnedExit learned, ProcessIdentity identity, ExitRoute route, CancellationToken ct,
        Action<string>? trace = null, Action? requestIssued = null);
    Task SaveManyAsync(IReadOnlyCollection<CloseProfile> profiles, CancellationToken ct);
}

internal sealed record LearningTestProgress(int Current, int Total);
internal sealed record LearningResult(int Recorded, int Saved, IReadOnlyList<string> Errors);

internal sealed class CloseLearningSession(ICloseLearningBackend backend)
{
    public async Task LaunchAsync(CancellationToken ct) => _ = await backend.LaunchAsync(ct);

    public async Task<LearnedExit> ObserveAsync(IProgress<bool> ready, CancellationToken ct)
    {
        IReadOnlyList<LearningProcess> running;
        while ((running = await backend.RunningAsync(ct)).Count == 0)
        {
            ready.Report(false);
            await Task.Delay(250, ct);
        }
        using var recording = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = running.DistinctBy(p => p.Identity)
            .Select(p => backend.RecordAsync(p.Identity, recording.Token, () => ready.Report(true))).ToList();
        var all = pending.ToArray();
        Exception? lastError = null;
        try
        {
            // Any existing instance can be the user's demonstration; none is adopted by automation.
            while (pending.Count != 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                try { return await finished; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { lastError = ex; }
            }
            throw lastError ?? new InvalidOperationException("Не удалось записать выход. Запустите программу и попробуйте ещё раз.");
        }
        finally
        {
            await recording.CancelAsync();
            try { await Task.WhenAll(all); }
            catch (Exception) { /* Other observers are cancelled after the demonstrated instance exits. */ }
        }
    }

    public async Task<LearningResult> TestAndSaveAsync(IReadOnlyList<LearnedExit> learned,
        IProgress<LearningTestProgress> progress, CancellationToken ct)
    {
        var verified = new List<CloseProfile>();
        var errors = new List<string>();
        ProcessIdentity? launch = null;
        string? blocked = null;
        for (var index = 0; index < learned.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new(index + 1, learned.Count));
            var exit = learned[index];
            if (blocked is not null) { errors.Add($"Способ {index + 1}: {blocked}"); continue; }
            try
            {
                if (launch is not null && (await backend.OwnedAsync(launch, ct)).Count == 0) launch = null;
                if (launch is null)
                {
                    // Never use an unrelated instance that the user opened between demonstrations.
                    if ((await backend.RunningAsync(ct)).Count != 0)
                        throw new InvalidOperationException("Программа уже открыта вне проверки. Этот экземпляр оставлен работать.");
                    launch = await backend.LaunchAsync(ct)
                        ?? throw new InvalidOperationException("Программу открыли вне проверки. Этот экземпляр оставлен работать.");
                }
                var routes = exit.Recipe.TrayIcon is null
                    ? new[] { ExitRoute.Direct, ExitRoute.VisibleWindow }
                    : new[] { ExitRoute.Direct, ExitRoute.Tray, ExitRoute.VisibleWindow };
                Exception? failure = null;
                foreach (var route in routes)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = await backend.ReadyAsync(launch, exit.Recipe, ct);
                    var issued = false;
                    try
                    {
                        var profile = await backend.VerifyAsync(exit, target, route, ct, requestIssued: () => issued = true);
                        for (var attempt = 0; attempt < 25 && (await backend.OwnedAsync(launch, ct)).Count != 0; attempt++)
                            await Task.Delay(200, ct);
                        if ((await backend.OwnedAsync(launch, ct)).Count != 0)
                            throw new InvalidOperationException("После команды выхода программа ещё работает. Способ не сохранён.");
                        verified.Add(profile);
                        failure = null;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failure = ex;
                        var alive = (await backend.OwnedAsync(launch, ct)).Count != 0;
                        if (issued)
                        {
                            // A late exit must not make another recipe appear to work, or send two exit requests.
                            if (alive) blocked = "Предыдущая команда ещё не завершила программу. Дальнейшие запросы не отправлены.";
                            break;
                        }
                        if (!alive) break;
                    }
                }
                if (failure is not null) errors.Add($"Способ {index + 1}: {failure.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"Способ {index + 1}: {ex.Message}"); }
        }
        ct.ThrowIfCancellationRequested();
        // Commit together, so cancellation cannot replace old profiles with a half-tested session.
        await backend.SaveManyAsync(verified, ct);
        return new(learned.Count, verified.Count, errors);
    }
}
