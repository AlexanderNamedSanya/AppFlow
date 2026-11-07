using AppFlow.Desktop.Services;
using Xunit;

namespace AppFlow.Tests;

public sealed class InstalledProgramTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Example\\Client.exe\",0", "C:\\Program Files\\Example\\Client.exe")]
    [InlineData("C:\\Program Files\\Example\\Client.exe,0", "C:\\Program Files\\Example\\Client.exe")]
    [InlineData("\"C:\\Apps\\Client.exe\" --background", "C:\\Apps\\Client.exe")]
    [InlineData("C:\\Apps\\Client.exe --background", "C:\\Apps\\Client.exe")]
    [InlineData("C:\\Apps\\icon.dll,0", null)]
    [InlineData("C:\\Apps\\Client.exe.backup", null)]
    [InlineData("Client.exe", null)]
    [InlineData("\"C:\\Apps\\Client.exe", null)]
    public void ExtractsOnlyAbsoluteExecutablePaths(string input, string? expected)
        => Assert.Equal(expected, InstalledProgramService.ParseExecutablePath(input));

    [Fact]
    public void CatalogDeduplicatesPathsAndPrefersResolvedEntries()
    {
        var result = InstalledProgramService.Merge([
            new("Example", @"C:\Apps\Client.exe"), new("Example shortcut", @"c:\apps\client.EXE"),
            new("Example", null), new("Needs file", null), new("needs FILE", null)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(@"C:\Apps\Client.exe", result[0].Executable);
        Assert.Null(result[1].Executable);
    }
}
