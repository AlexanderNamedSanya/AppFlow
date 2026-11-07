// Dedicated disposable process for integration tests. Never uses a user's applications.
if (args is ["--window", var marker]) TestWindow.Run(marker);
else if (args is ["--window", var scenarioMarker, var scenario])
{
    if (scenario == "no-window") { File.WriteAllText(scenarioMarker + ".ready", "ready"); await Task.Delay(Timeout.InfiniteTimeSpan); }
    else if (scenario == "console-save") await RunConsoleAsync(scenarioMarker);
    else TestWindow.Run(scenarioMarker, scenario);
}
else if (Environment.GetEnvironmentVariable("APPFLOW_TEST_WINDOW_MARKER") is { Length: > 0 } uiMarker)
    TestWindow.Run(uiMarker, "visible-menu");
else await Task.Delay(Timeout.InfiniteTimeSpan);

static async Task RunConsoleAsync(string marker)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        File.AppendAllText(marker + ".console-close", "close\n");
        File.WriteAllText(marker, "saved before normal exit");
        completion.TrySetResult();
    };
    File.WriteAllText(marker + ".ready", "ready");
    await completion.Task;
}
