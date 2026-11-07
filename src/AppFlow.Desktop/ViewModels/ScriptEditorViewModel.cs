using System.Text;
using AppFlow.Desktop.Services;

namespace AppFlow.Desktop.ViewModels;

public sealed class ScriptEditorViewModel : ObservableObject
{
    private string name = "";
    private string code = "";
    private string extension = ".ps1";
    private string error = "";
    private bool busy;
    public string Name { get => name; set { if (Set(ref name, value)) Raise(nameof(CanAdd)); } }
    public string Code { get => code; set { if (Set(ref code, value)) Raise(nameof(CanAdd)); } }
    public string Extension { get => extension; set => Set(ref extension, value); }
    public string Error { get => error; set => Set(ref error, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(CanAdd)); } }
    public bool CanAdd => !Busy && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Code);

    public async Task<InstalledProgram> CreateAsync(string directory)
    {
        if (!CanAdd) throw new InvalidDataException("Укажите название и текст скрипта.");
        if (Extension is not (".ps1" or ".cmd" or ".bat")) throw new InvalidDataException("Выберите тип скрипта.");
        Busy = true;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(Path.GetFullPath(directory), Guid.NewGuid().ToString("N") + Extension);
            // Windows PowerShell 5.1 requires a BOM to read non-ASCII text as UTF-8.
            var encoding = Extension == ".ps1" ? new UTF8Encoding(true) : new UTF8Encoding(false);
            var text = Code.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            if (Extension != ".ps1") text = "@chcp 65001 >nul\r\n" + text;
            await File.WriteAllTextAsync(path, text, encoding);
            return new(Name.Trim(), path);
        }
        finally { Busy = false; }
    }
}
