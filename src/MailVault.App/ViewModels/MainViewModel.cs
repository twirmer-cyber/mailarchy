using MailVault.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Dialogs;
using MailVault.App.Services;
using MailVault.Core;

namespace MailVault.App.ViewModels;

public partial class FolderNodeViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public long TotalCount { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();
    [ObservableProperty] bool _isExpanded;
    [ObservableProperty] bool _isSelected;

    public string Display => TotalCount > 0 ? $"{Name} ({TotalCount:N0})" : Name;

    public FolderNodeViewModel(ArchiveFolder f, ISet<long> expanded)
    {
        Id = f.Id;
        Name = f.Name;
        TotalCount = f.TotalCount;
        _isExpanded = expanded.Contains(f.Id);
        foreach (var c in f.Children) Children.Add(new FolderNodeViewModel(c, expanded));
    }

    public IEnumerable<FolderNodeViewModel> Flatten() => new[] { this }.Concat(Children.SelectMany(c => c.Flatten()));
}

public enum Page { Start, Archive, Export, Search, Settings, Folder, Ai }

public partial class MainViewModel : ObservableObject
{
    public StartViewModel Start { get; }
    public ArchiveViewModel ArchivePage { get; }
    public ExportViewModel ExportPage { get; }
    public SearchViewModel SearchPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public AiViewModel AiPage { get; }

    [ObservableProperty] object? _currentPage;
    [ObservableProperty] Page _currentPageKind;
    [ObservableProperty] ObservableCollection<FolderNodeViewModel> _folders = new();
    [ObservableProperty] string _rootTitle = L.T("Main_MyArchive");

    public MainViewModel()
    {
        Start = new StartViewModel(this);
        ArchivePage = new ArchiveViewModel();
        ExportPage = new ExportViewModel(this);
        SearchPage = new SearchViewModel(this);
        SettingsPage = new SettingsViewModel();
        AiPage = new AiViewModel(this);
        MessageListViewModel.AiRequested += m => { Navigate(Page.Ai); AiPage.PrepareForMessage(m); };
        AppState.ArchiveChanged += RefreshFolders;
        RefreshFolders();
        Navigate(Page.Start);
    }

    [RelayCommand]
    public void Navigate(Page page)
    {
        CurrentPageKind = page;
        CurrentPage = page switch
        {
            Page.Start => Start,
            Page.Archive => ArchivePage,
            Page.Export => ExportPage,
            Page.Search => SearchPage,
            Page.Settings => SettingsPage,
            Page.Ai => AiPage,
            _ => CurrentPage,
        };
        if (page == Page.Start) Start.Refresh();
        if (page == Page.Export) ExportPage.RefreshFolders();
        if (page == Page.Settings) SettingsPage.Refresh();
        if (page == Page.Ai) AiPage.Refresh();
    }

    public void ShowFolder(FolderNodeViewModel? node)
    {
        if (node is null) return;
        var path = AppState.Archive.GetFolderPath(node.Id);
        var vm = new MessageListViewModel(path, () => AppState.Archive.GetMessages(node.Id, recursive: true)) { ScopeFolderId = node.Id };
        SearchPage.ScopeToFolder(node.Id); // Suche gilt ab jetzt nur in diesem Teilbaum (Chip „Nur in …“ hebt das auf)
        CurrentPageKind = Page.Folder;
        CurrentPage = vm;
        _ = vm.RefreshAsync();
    }

    public void ShowSearch(string query)
    {
        SearchPage.Query = query;
        Navigate(Page.Search);
        _ = SearchPage.SearchCommand.ExecuteAsync(null);
    }

    void RefreshFolders()
    {
        var expanded = Folders.SelectMany(f => f.Flatten()).Where(f => f.IsExpanded).Select(f => f.Id).ToHashSet();
        var tree = AppState.Archive.GetFolderTree();
        Folders = new ObservableCollection<FolderNodeViewModel>(tree.Select(t => new FolderNodeViewModel(t, expanded)));
        var total = tree.Sum(t => t.TotalCount);
        RootTitle = total > 0 ? L.F("Main_MyArchiveCount", total) : L.T("Main_MyArchive");
        // Offene Ordneransicht aktualisieren
        if (CurrentPage is MessageListViewModel list && CurrentPageKind == Page.Folder) _ = list.RefreshAsync();
    }

    public void RenameFolder(FolderNodeViewModel node)
    {
        var name = InputDialog.Ask(System.Windows.Application.Current.MainWindow, L.T("Main_RenameTitle"), L.T("Main_NewName"), node.Name, L.T("Main_Rename"));
        if (name is null || name.Trim() == node.Name) return;
        try
        {
            AppState.Archive.RenameFolder(node.Id, name);
            AppState.NotifyArchiveChanged();
        }
        catch (Exception ex) { Ui.Error(ErrorText.Describe(ex)); }
    }

    public async Task DeleteFolderAsync(FolderNodeViewModel node)
    {
        if (!Ui.Confirm(L.F("Main_DeleteFolderConfirm", node.Name, node.TotalCount)))
            return;
        await Task.Run(() => AppState.Archive.DeleteFolder(node.Id));
        if (CurrentPageKind == Page.Folder) Navigate(Page.Start);
        AppState.NotifyArchiveChanged();
    }

    public void ExportFolder(FolderNodeViewModel node)
    {
        ExportPage.RefreshFolders();
        ExportPage.SelectedFolder = ExportPage.FolderChoices.FirstOrDefault(f => f.Id == node.Id);
        Navigate(Page.Export);
    }
}
