using AppFlow.Core;
using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

public sealed class PackagedProcessSessionTests
{
    private const string AppId = "Package_family!App";
    private static readonly ProcessIdentity Root = new(100, 1000, @"C:\package\updatecheck.exe");
    private static readonly ProcessIdentity Runtime = new(101, 1100, @"C:\package\bin\app.exe");

    [Fact]
    public void LeaseFollowsRuntimeAfterBootstrapExits()
    {
        var session = new PackagedProcessSession(Root, AppId);
        Assert.Equal(Root, Assert.Single(session.Update([new(Root, 1, AppId)])));
        Assert.Equal(Runtime, Assert.Single(session.Update([new(Runtime, Root.Pid, AppId)])));
        Assert.Equal(Runtime, Assert.Single(session.Update([new(Runtime, Root.Pid, AppId)])));
        Assert.Empty(session.Update([]));
    }

    [Fact]
    public void IndependentOldOrUnpackagedInstancesAreNeverAdopted()
    {
        var session = new PackagedProcessSession(Root, AppId);
        Assert.Empty(session.Update([
            new(Runtime with { Pid = 102 }, 1, AppId),
            new(Runtime with { Pid = 103, StartTimeUtcTicks = 900 }, Root.Pid, AppId),
            new(Runtime with { Pid = 104 }, Root.Pid, "Other_family!App"),
            new(Runtime with { Pid = 105 }, Root.Pid, null)]));
    }

    [Fact]
    public void ReusedParentAndRuntimePidsDoNotGrantOwnership()
    {
        var session = new PackagedProcessSession(Root, AppId);
        Assert.Empty(session.Update([
            new(Root with { StartTimeUtcTicks = 1050 }, 1, "Other_family!App"),
            new(Runtime, Root.Pid, AppId)]));
        session = new(Root, AppId);
        Assert.Single(session.Update([new(Runtime, Root.Pid, AppId)]));
        Assert.Empty(session.Update([new(Runtime with { StartTimeUtcTicks = 1200 }, Root.Pid, AppId)]));
    }

    [Fact]
    public void NativeCaptureReadsProcessIdentitiesAndParents()
    {
        var current = Assert.Single(PackagedProcessSession.Capture(CancellationToken.None), p => p.Identity.Pid == Environment.ProcessId);
        Assert.True(current.ParentPid > 0);
        Assert.True(current.Identity.StartTimeUtcTicks > 0);
        Assert.True(Path.IsPathFullyQualified(current.Identity.Executable));
    }

    [Fact]
    public void FilteredCaptureIncludesOnlyRequestedExecutableNames()
    {
        var name = Path.GetFileName(Environment.ProcessPath!);
        var snapshot = PackagedProcessSession.Capture(CancellationToken.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name });
        Assert.Contains(snapshot, p => p.Identity.Pid == Environment.ProcessId);
        Assert.All(snapshot, p => Assert.Equal(name, Path.GetFileName(p.Identity.Executable), ignoreCase: true));
        Assert.Empty(PackagedProcessSession.Capture(CancellationToken.None, new HashSet<string> { "AppFlow-nonexistent.exe" }));
    }

    [Fact]
    public void FilteredCaptureKeepsTrackedPidsEvenOutsideTheRequestedPackage()
    {
        var snapshot = PackagedProcessSession.Capture(CancellationToken.None, new HashSet<string>(),
            new HashSet<string> { "NonexistentPackage!App" }, new HashSet<int> { Environment.ProcessId });
        Assert.Equal(Environment.ProcessId, Assert.Single(snapshot).Identity.Pid);
    }
}
