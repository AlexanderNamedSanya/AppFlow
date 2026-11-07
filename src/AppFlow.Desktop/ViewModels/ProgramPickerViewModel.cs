using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AppFlow.Desktop.Services;
using AppFlow.Core;

namespace AppFlow.Desktop.ViewModels;

public sealed class ProgramPickerViewModel : ObservableObject
{
    private string search = "";
    private string message = "Поиск установленных программ…";
    private InstalledProgram? selected;
    private InstalledProgram? websiteSuggestion;
    public ProgramPickerViewModel(bool allowOtherTargets = false)
    {
        AllowOtherTargets = allowOtherTargets;
        View = CollectionViewSource.GetDefaultView(Programs);
        View.Filter = item => item is InstalledProgram p && (ReferenceEquals(p, websiteSuggestion) || p.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || (p.Executable?.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
    }
    public ObservableCollection<InstalledProgram> Programs { get; } = [];
    public bool AllowOtherTargets { get; }
    public string Heading => AllowOtherTargets ? "Программа или сайт" : "Установленные программы";
    public string Subheading => AllowOtherTargets ? "Найдите программу или введите адрес сайта." : "Выберите из списка или укажите конкретный файл.";
    public string SearchPlaceholder => AllowOtherTargets ? "Название программы или адрес сайта" : "Поиск по названию или пути EXE";
    public string PickerTitle => AllowOtherTargets ? "Добавить действие" : "Выбор программы";
    public ICollectionView View { get; }
    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value)) return;
            if (websiteSuggestion is not null) Programs.Remove(websiteSuggestion);
            websiteSuggestion = null;
            var input = value.Trim();
            if (AllowOtherTargets && (input.Contains("://", StringComparison.Ordinal)
                || (input.Contains('.') && !input.Contains('\\') && !input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !ActionTarget.IsScript(input))))
            {
                try
                {
                    var address = ActionTarget.Website(input);
                    websiteSuggestion = new(new Uri(address).Host, address);
                    Programs.Insert(0, websiteSuggestion);
                }
                catch (InvalidDataException) { }
            }
            View.Refresh();
            Selected = websiteSuggestion ?? View.Cast<InstalledProgram>().FirstOrDefault();
            UpdateMessage();
        }
    }
    public string Message { get => message; private set => Set(ref message, value); }
    public InstalledProgram? Selected { get => selected; set { Set(ref selected, value); Raise(nameof(CanSelect)); Raise(nameof(SelectLabel)); } }
    public bool CanSelect => Selected is not null;
    public string SelectLabel => Selected?.Kind == TargetKind.Website ? "Добавить сайт" : Selected is { Executable: null } ? "Выбрать EXE…" : "Выбрать";
    public void ShowError(Exception ex) => Message = ex.Message;
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var programs = await InstalledProgramService.DiscoverAsync(ct);
            foreach (var program in programs) Programs.Add(program);
            UpdateMessage();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Message = $"Не удалось получить список: {ex.Message}. Используйте «Обзор»."; }
    }
    private void UpdateMessage() => Message = websiteSuggestion is not null ? "Сайт откроется в браузере по умолчанию." : Programs.Count == 0 ? "Программы не найдены. Укажите EXE через «Обзор»."
        : View.IsEmpty ? "Ничего не найдено. Измените поиск или нажмите «Обзор»." : "Выберите программу. Если она использует лаунчер, укажите EXE самого приложения.";
}
