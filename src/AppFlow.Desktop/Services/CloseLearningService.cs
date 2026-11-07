using AppFlow.Core;

namespace AppFlow.Desktop.Services;

public sealed record LearningProcess(ProcessIdentity Identity, string Description);
internal sealed record LearnedExit(ExitRecipe Recipe, int ExitCode, string FileHash);

internal sealed class CloseLearningService(string executable, WindowsProcessService processes, CloseProfileStore store) : ICloseLearningBackend
{
    public string Executable => executable;
    public async Task<IReadOnlyList<LearningProcess>> RunningAsync(CancellationToken ct)
    {
        var snapshot = await processes.SnapshotAsync([executable], ct);
        IReadOnlyList<ProcessIdentity> identities = snapshot.Processes;
        if (identities.Count == 0 && await PackagedProgramService.FindApplicationIdAsync(executable, ct) is { } applicationId)
            identities = PackagedProcessSession.Capture(ct).Where(p => string.Equals(p.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Identity).ToArray();
        return identities.Select(p =>
        {
            var title = ExitNative.Windows(new HashSet<int> { p.Pid }).FirstOrDefault(w => w.Owner == IntPtr.Zero && w.Title.Length != 0)?.Title;
            return new LearningProcess(p, $"{title ?? Path.GetFileNameWithoutExtension(p.Executable)} · PID {p.Pid}");
        }).ToArray();
    }
    public Task<ProcessIdentity?> LaunchAsync(CancellationToken ct) => processes.LaunchIfAbsentAsync(executable, ct);
    public Task<IReadOnlyList<ProcessIdentity>> OwnedAsync(ProcessIdentity launch, CancellationToken ct) => processes.OwnedIdentitiesAsync(launch, ct);
    public Task SaveManyAsync(IReadOnlyCollection<CloseProfile> profiles, CancellationToken ct) => store.SaveManyAsync(profiles, ct);

    public async Task<ProcessIdentity> ReadyAsync(ProcessIdentity launch, ExitRecipe recipe, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var owned = await OwnedAsync(launch, ct);
            foreach (var identity in owned)
                if (ExitNative.Windows(new HashSet<int> { identity.Pid }).Any(w => w.ClassName == recipe.Window.ClassName)) return identity;
            // A recorded popup may exist only after opening the tray, not during startup.
            if (attempt >= 5 && recipe.TrayIcon is not null && owned.Count != 0) return owned[0];
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("Не удалось дождаться готовности запущенной для проверки программы.");
    }

    public async Task<LearnedExit> RecordAsync(ProcessIdentity identity, CancellationToken ct, Action? started = null)
    {
        var handle = ExitNative.Verify(identity);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Этот экземпляр уже завершился. Запустите программу и повторите обучение.");
        try
        {
            var hash = await CloseProfileStore.FingerprintAsync(executable, ct);
            using var recorder = new ExitRecorder(identity);
            await recorder.Ready.WaitAsync(ct);
            started?.Invoke();
            while (ExitNative.Alive(handle)) { ct.ThrowIfCancellationRequested(); await Task.Delay(80, ct); }
            // Accessibility notifications are queued; allow already emitted events to arrive.
            await Task.Delay(200, ct);
            await recorder.FlushAsync(ct);
            var recipe = recorder.Recorded ?? throw new InvalidOperationException("Выход произошёл, но команду распознать не удалось. Повторите обучение через пункт «Выход» в меню программы или трея.");
            var code = ExitNative.ExitCode(handle);
            if (code < 0) throw new InvalidOperationException($"Программа завершилась с системным кодом ошибки 0x{unchecked((uint)code):X8}. Такой выход не сохранён.");
            return new(recipe, code, hash);
        }
        finally { ExitNative.CloseHandle(handle); }
    }

    public async Task<CloseProfile> VerifyAsync(LearnedExit learned, ProcessIdentity identity, ExitRoute route, CancellationToken ct,
        Action<string>? trace = null, Action? requestIssued = null)
    {
        if (learned.FileHash != await CloseProfileStore.FingerprintAsync(executable, ct))
            throw new InvalidOperationException("Программа изменилась после записи. Повторите обучение.");
        var handle = ExitNative.Verify(identity);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Программа ещё не запущена или уже завершилась.");
        try
        {
            var verified = new[] { (handle, identity.Pid) };
            var window = ExitNative.Locate(learned.Recipe.Window, verified);
            var hiddenWindows = new List<IntPtr>();
            if (ExitNative.IsModal(verified)) throw new InvalidOperationException("Сначала завершите диалог в окне программы, затем повторите проверку.");
            ct.ThrowIfCancellationRequested();
            if (route == ExitRoute.VisibleWindow)
            {
                if (window == IntPtr.Zero) throw new InvalidOperationException("Окно программы пока не найдено. Дождитесь её запуска.");
                ExitNative.ShowWindowAsync(window, 9); // Explicitly permitted only inside learning.
            }
            else
            {
                foreach (var main in ExitNative.Windows(new HashSet<int> { identity.Pid }).Where(w => w.Visible && !w.Minimized
                    && (w.Handle == window || w.Owner == IntPtr.Zero || !ExitNative.IsWindowVisible(w.Owner))))
                { ct.ThrowIfCancellationRequested(); hiddenWindows.Add(main.Handle); ExitNative.ShowWindowAsync(main.Handle, 6); }
                if (window != IntPtr.Zero && !hiddenWindows.Contains(window)) hiddenWindows.Add(window);
            }
            await Task.Delay(400, ct);
            bool Hidden() => hiddenWindows.All(w => !ExitNative.Owns(w, verified) || !ExitNative.IsWindowVisible(w) || ExitNative.IsIconic(w));
            if (route != ExitRoute.VisibleWindow && !Hidden()) throw new InvalidOperationException("Не удалось подготовить свёрнутое состояние. Способ в фоне не проверен.");
            var issued = route switch
            {
                ExitRoute.Tray => await ExitAutomation.TryTrayAsync(verified, learned.Recipe, ct, trace, protectUser: false),
                ExitRoute.VisibleWindow when learned.Recipe.Method == ExitMethod.Automation => await ExitAutomation.TryWindowAsync(verified, learned.Recipe, ct),
                _ => await LearnedApplicationCloser.DirectAsync(verified, learned.Recipe, ct)
            };
            if (!issued) throw new InvalidOperationException("Не удалось воспроизвести записанный выход. Покажите другой способ закрытия.");
            requestIssued?.Invoke();
            var result = await LearnedApplicationCloser.WaitAsync(verified, TimeSpan.FromSeconds(20), ct,
                route == ExitRoute.VisibleWindow ? null : Hidden);
            if (result != ProcessCloseResult.Closed) throw new InvalidOperationException(result == ProcessCloseResult.ConfirmationRequired
                ? "Программа ждёт подтверждения. Завершите диалог вручную; такой выход пока не сохранён."
                : "Команда отправлена, но выход не подтверждён. Способ не сохранён. Можно проверить другой вариант.");
            var exitCode = ExitNative.ExitCode(handle);
            if (exitCode != learned.ExitCode) throw new InvalidOperationException($"Код выхода отличается от ручного: {exitCode} вместо {learned.ExitCode}. Способ не сохранён.");
            return new()
            {
                Executable = executable, FileHash = learned.FileHash, Recipe = learned.Recipe, ManualExitCode = learned.ExitCode,
                VerifiedAt = DateTimeOffset.UtcNow, BackgroundVerified = route != ExitRoute.VisibleWindow,
                VisibleVerified = route == ExitRoute.VisibleWindow, DirectVerified = route == ExitRoute.Direct
            };
        }
        finally { ExitNative.CloseHandle(handle); }
    }
}
