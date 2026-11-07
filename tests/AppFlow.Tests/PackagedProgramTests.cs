using AppFlow.Desktop.Services;
using Xunit;
using Xunit.Abstractions;

namespace AppFlow.Tests;

public sealed class PackagedProgramTests(ITestOutputHelper output)
{
    [Fact]
    public Task RunningRiftIsDetectedThroughManifestLauncherWithoutAdoptingIt() => AutoSaveTests.OnDispatcher(async () =>
    {
        var program = PackagedProgramService.Discover(CancellationToken.None).FirstOrDefault(p => p.Name == "RIFT Intel Fusion Tool");
        if (program?.Executable is null) return;
        var appId = await PackagedProgramService.FindApplicationIdAsync(program.Executable, CancellationToken.None);
        var live = PackagedProcessSession.Capture(CancellationToken.None).Where(p => p.ApplicationId == appId).ToArray();
        if (live.Length == 0) return;
        var service = new WindowsProcessService();
        var snapshot = await service.SnapshotAsync([program.Executable], CancellationToken.None);
        Assert.True(snapshot.Contains(program.Executable));
        Assert.Null(await service.LaunchIfAbsentAsync(program.Executable, CancellationToken.None));
        output.WriteLine($"Recognized existing RIFT without activation or ownership: {string.Join(", ", live.Select(p => p.Identity.Executable))}");
    });

    [Fact]
    public Task RegisteredStoreApplicationsAreDiscoveredWithoutLaunchingThem() => AutoSaveTests.OnDispatcher(async () =>
    {
        var programs = PackagedProgramService.Discover(CancellationToken.None);
        Assert.NotEmpty(programs);
        var resolved = programs.Where(p => p.Executable?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        Assert.NotEmpty(resolved);
        foreach (var program in resolved.Take(8)) output.WriteLine($"{program.Name}: {program.Executable}");
        Assert.All(resolved, p => Assert.True(File.Exists(p.Executable)));
        Assert.NotNull(await PackagedProgramService.FindApplicationIdAsync(resolved[0].Executable!, CancellationToken.None));
    });

    [Fact]
    public void ManifestSelectsCorrectApplicationAndRejectsEscapingPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-package-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="First" Executable="first.exe" />
                <Application Id="Second" Executable="bin\second.exe" />
                <Application Id="Escape" Executable="..\outside.exe" />
              </Applications>
            </Package>
            """);
        Assert.Equal(Path.Combine(directory, "bin", "second.exe"), PackagedProgramService.ResolveExecutable(directory, "Family!Second"));
        Assert.Null(PackagedProgramService.ResolveExecutable(directory, "Family!Missing"));
        Assert.Null(PackagedProgramService.ResolveExecutable(directory, "Family!Escape"));
        Assert.Null(PackagedProgramService.ResolveExecutable(directory, "not-an-application-id"));
        File.Delete(Path.Combine(directory, "AppxManifest.xml")); Directory.Delete(directory);
    }
}
