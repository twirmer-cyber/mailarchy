using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailVault.App.ViewModels;
using Page = MailVault.App.ViewModels.Page;

namespace MailVault.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    bool _syncingNav;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainViewModel();
        _vm.PropertyChanged += OnVmPropertyChanged;
        SyncNav();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Strg+F (und F3) springt von überall ins Suchfeld.</summary>
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!((e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.F3 && Keyboard.Modifiers == ModifierKeys.None)))
            return;
        e.Handled = true;
        if (_vm.CurrentPageKind != Page.Search) _vm.Navigate(Page.Search);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (FindDescendant<Views.SearchView>(this)?.FindName("QueryBox") is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        });
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPageKind)) SyncNav();
    }

    void SyncNav()
    {
        _syncingNav = true;
        NavStart.IsChecked = _vm.CurrentPageKind == Page.Start;
        NavArchive.IsChecked = _vm.CurrentPageKind == Page.Archive;
        NavSearch.IsChecked = _vm.CurrentPageKind == Page.Search;
        NavExport.IsChecked = _vm.CurrentPageKind == Page.Export;
        NavSettings.IsChecked = _vm.CurrentPageKind == Page.Settings;
        NavAi.IsChecked = _vm.CurrentPageKind == Page.Ai;
        _syncingNav = false;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav || _vm is null) return;
        var page = sender == NavArchive ? Page.Archive : sender == NavSearch ? Page.Search :
                   sender == NavExport ? Page.Export : sender == NavSettings ? Page.Settings : sender == NavAi ? Page.Ai : Page.Start;
        _vm.Navigate(page);
    }

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node) _vm.ShowFolder(node);
    }

    void FolderTree_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Rechtsklick wählt den Ordner aus, damit sich das Kontextmenü auf ihn bezieht.
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem item) { item.IsSelected = true; item.Focus(); }
    }

    FolderNodeViewModel? SelectedNode => FolderTree.SelectedItem as FolderNodeViewModel;

    void FolderSearch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n) return;
        _vm.SearchPage.Folder = _vm.SearchPage.Folders.FirstOrDefault(f => f.Id == n.Id);
        _vm.SearchPage.ShowAdvanced = true;
        _vm.Navigate(Page.Search);
    }

    void FolderExport_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n) _vm.ExportFolder(n);
    }

    void FolderRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n) _vm.RenameFolder(n);
    }

    async void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n) await _vm.DeleteFolderAsync(n);
    }
}
