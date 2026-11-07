using System.Windows.Input;
using AppFlow.Desktop.Services;

namespace AppFlow.Desktop.ViewModels;

internal sealed class CloseLearningViewModel : ObservableObject, IDisposable
{
    private readonly CloseLearningSession session;
    private readonly string name;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<LearnedExit> learned = [];
    private Task recordingTask = Task.CompletedTask, launchTask = Task.CompletedTask, testingTask = Task.CompletedTask;
    private bool recording, launching, disposed, initialized;
    private int generation;
    private Stage stage = Stage.Learning;
    private string status = "Подготавливаю обучение…", detail = "", error = "";
    public CloseLearningViewModel(CloseLearningSession session, string name)
    {
        this.session = session; this.name = name;
        LaunchCommand = new AsyncCommand(() => launchTask = LaunchAsync(), ShowError);
        AddMethodCommand = new AsyncCommand(() => recordingTask = RecordAsync(), ShowError);
        FinishCommand = new AsyncCommand(() => testingTask = FinishAsync(), ShowError);
    }
    public event Action? Completed;
    public string Name => name;
    public string LaunchLabel => $"Запустить {name}";
    public bool IsLearning => stage == Stage.Learning;
    public bool IsRecorded => stage == Stage.Recorded;
    public bool IsTesting => stage == Stage.Testing;
    public bool ShowProgress => IsTesting || IsLearning && recording;
    public bool ShowRecordedIcon => IsRecorded && !HasError;
    public bool CanLaunch => IsLearning && !launching && !disposed;
    public bool CanFinish => IsRecorded && !launching && !disposed;
    public int LearnedCount => learned.Count;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public string Error { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); Raise(nameof(ShowRecordedIcon)); } }
    public bool HasError => Error.Length != 0;
    public string ResultMessage { get; private set; } = "";
    public ICommand LaunchCommand { get; }
    public ICommand AddMethodCommand { get; }
    public ICommand FinishCommand { get; }

    public Task InitializeAsync()
    {
        if (initialized || disposed) return Task.CompletedTask;
        initialized = true;
        return recordingTask = RecordAsync();
    }

    private async Task RecordAsync()
    {
        if (recording || IsTesting || disposed) return;
        recording = true;
        var current = ++generation;
        stage = Stage.Learning;
        Error = ""; Detail = ""; Status = "Подготавливаю обучение…"; Refresh();
        var ready = new Progress<bool>(value =>
        {
            if (disposed || !IsLearning || generation != current) return;
            Status = value ? "Закройте программу вручную" : "Запустите программу и закройте её вручную";
        });
        try
        {
            learned.Add(await Task.Run(() => session.ObserveAsync(ready, lifetime.Token), lifetime.Token));
            stage = Stage.Recorded;
            Status = "Способ закрытия записан";
            Detail = learned.Count == 1 ? "Можно закончить или показать ещё один способ." : $"Записано способов: {learned.Count}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            stage = learned.Count == 0 ? Stage.Learning : Stage.Recorded;
            Status = "Попробуйте закрыть программу через её меню «Выход»";
        }
        finally { recording = false; Refresh(); }
    }

    private async Task LaunchAsync()
    {
        if (!CanLaunch) return;
        launching = true; Error = ""; Refresh();
        try
        {
            if (!recording) recordingTask = RecordAsync();
            await Task.Run(() => session.LaunchAsync(lifetime.Token), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { launching = false; Refresh(); }
    }

    private async Task FinishAsync()
    {
        if (!CanFinish) return;
        stage = Stage.Testing;
        Error = ""; Status = "Проверяю способы закрытия…";
        Detail = "Программа может открываться и закрываться."; Refresh();
        var progress = new Progress<LearningTestProgress>(value =>
        {
            if (disposed || !IsTesting) return;
            Detail = $"Способ {value.Current} из {value.Total}. Программа может открываться и закрываться.";
        });
        try
        {
            var result = await Task.Run(() => session.TestAndSaveAsync(learned.ToArray(), progress, lifetime.Token), lifetime.Token);
            ResultMessage = result.Saved == result.Recorded
                ? $"Обучение «{name}» завершено. Проверено и сохранено способов: {result.Saved}."
                : $"Обучение «{name}» завершено. Сохранено новых способов: {result.Saved} из {result.Recorded}. Прежние способы сохранены. {result.Errors.FirstOrDefault()}";
            stage = Stage.Complete; Refresh();
            Completed?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            stage = Stage.Recorded;
            Status = "Проверка не завершена";
            Detail = "Можно повторить проверку.";
            Error = ex.Message;
            Refresh();
        }
    }

    private void ShowError(Exception ex) => Error = ex.Message;
    private void Refresh()
    {
        Raise(nameof(IsLearning)); Raise(nameof(IsRecorded)); Raise(nameof(IsTesting)); Raise(nameof(ShowProgress));
        Raise(nameof(ShowRecordedIcon));
        Raise(nameof(CanLaunch)); Raise(nameof(CanFinish)); Raise(nameof(LearnedCount));
    }
    public async Task StopAsync()
    {
        Dispose();
        try { await Task.WhenAll(recordingTask, launchTask, testingTask); }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        _ = Task.WhenAll(recordingTask, launchTask, testingTask).ContinueWith(_ => lifetime.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private enum Stage { Learning, Recorded, Testing, Complete }
}
