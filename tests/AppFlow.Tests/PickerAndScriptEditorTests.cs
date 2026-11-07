using System.Text;
using AppFlow.Core;
using AppFlow.Desktop.Services;
using AppFlow.Desktop.ViewModels;
using Xunit;

namespace AppFlow.Tests;

public sealed class PickerAndScriptEditorTests
{
    [Fact]
    public Task UnifiedSearchFindsProgramsAndAddsWebsiteWithoutSwitching() => AutoSaveTests.OnDispatcher(() =>
    {
        var picker = new ProgramPickerViewModel(true);
        var program = new InstalledProgram("Блокнот", Path.GetFullPath("notepad.exe"));
        picker.Programs.Add(program);
        picker.Search = "Блок";
        Assert.Same(program, picker.Selected);
        picker.Search = "example.com/path?q=1";
        Assert.Equal(TargetKind.Website, picker.Selected!.Kind);
        Assert.Equal("https://example.com/path?q=1", picker.Selected.Executable);
        Assert.Equal("Добавить сайт", picker.SelectLabel);
        Assert.Single(picker.View.Cast<InstalledProgram>());
        picker.Search = "https://other.example/";
        Assert.Single(picker.Programs, p => p.Kind == TargetKind.Website);
        picker.Search = "notepad.exe";
        Assert.Same(program, picker.Selected);
        Assert.DoesNotContain(picker.Programs, p => p.Kind == TargetKind.Website);
        picker.Search = "https://user:password@example.com";
        Assert.False(picker.CanSelect);
        var mainPicker = new ProgramPickerViewModel();
        mainPicker.Search = "example.com";
        Assert.False(mainPicker.CanSelect);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(".ps1")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public async Task NamedScriptsAreStoredAsFilesWithUnicodeAndUniquePaths(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppFlow-editor-" + Guid.NewGuid());
        var editor = new ScriptEditorViewModel { Name = " Мой скрипт ", Code = "echo Привет\necho Готово", Extension = extension };
        Assert.True(editor.CanAdd);
        var first = await editor.CreateAsync(directory);
        var second = await editor.CreateAsync(directory);
        Assert.Equal("Мой скрипт", first.Name);
        Assert.Equal(TargetKind.Script, first.Kind);
        Assert.Equal(extension, Path.GetExtension(first.Executable));
        Assert.NotEqual(first.Executable, second.Executable);
        Assert.Equal(Path.GetFullPath(directory), Path.GetDirectoryName(first.Executable));
        var content = await File.ReadAllTextAsync(first.Executable!);
        Assert.Contains("echo Привет\r\necho Готово", content);
        if (extension == ".ps1") Assert.True((await File.ReadAllBytesAsync(first.Executable!)).Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
        else Assert.StartsWith("@chcp 65001 >nul\r\n", content);
        File.Delete(first.Executable!); File.Delete(second.Executable!); Directory.Delete(directory);
    }

    [Fact]
    public async Task EmptyScriptOrNameCannotBeAdded()
    {
        var editor = new ScriptEditorViewModel();
        Assert.False(editor.CanAdd);
        editor.Name = "Название";
        Assert.False(editor.CanAdd);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.CreateAsync("unused"));
        editor.Code = "echo test";
        editor.Name = " ";
        Assert.False(editor.CanAdd);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.CreateAsync("unused"));
    }
}
