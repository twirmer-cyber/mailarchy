using MailVault.App.Localization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using MailVault.App.Services;
using MailVault.Core;
using MailVault.Web;

namespace MailVault.App.Dialogs;

public partial class FolderAccessItem : ObservableObject
{
    public long Id { get; init; }
    public string Display { get; init; } = "";
    public Thickness Indent { get; init; }
    [ObservableProperty] bool _isChecked;
}

public partial class WebUserWindow : Window
{
    readonly WebUser? _existing;
    readonly List<FolderAccessItem> _items = new();

    public WebUserWindow(WebUser? existing)
    {
        InitializeComponent();
        _existing = existing;
        var allowed = existing?.AllowedFolderIds.ToHashSet() ?? new HashSet<long>();
        void Add(ArchiveFolder f, int depth)
        {
            _items.Add(new FolderAccessItem { Id = f.Id, Display = $"{f.Name} ({f.TotalCount:N0})", Indent = new Thickness(depth * 18, 1, 0, 1), IsChecked = allowed.Contains(f.Id) });
            if (depth < 2) foreach (var c in f.Children) Add(c, depth + 1); // Postfach und zwei Ebenen darunter
        }
        foreach (var r in AppState.Archive.GetFolderTree()) Add(r, 0);
        FolderList.ItemsSource = _items;

        if (existing is not null)
        {
            TitleText.Text = L.T("WebUser_Edit");
            UserBox.Text = existing.UserName;
            UserBox.IsReadOnly = true;
            EnabledBox.IsChecked = existing.Enabled;
            AdminBox.IsChecked = existing.IsAdmin;
            PasswordHint.Text = L.T("WebUser_KeepPassword");
        }
        Admin_Changed(this, new RoutedEventArgs());
    }

    void Admin_Changed(object sender, RoutedEventArgs e)
    {
        if (FolderList is not null) FolderList.IsEnabled = AdminBox.IsChecked != true;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = UserBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = L.T("WebUser_EnterName"); return; }
        if (PasswordBox.Password != Password2Box.Password) { ErrorText.Text = L.T("Password_Mismatch"); return; }
        var pw = PasswordBox.Password.Length == 0 && _existing is not null ? null : PasswordBox.Password;
        if (_existing is null && WebHost.Users.Find(name) is not null) { ErrorText.Text = L.T("WebUser_NameExists"); return; }
        var user = _existing?.Clone() ?? new WebUser { UserName = name };
        user.IsAdmin = AdminBox.IsChecked == true;
        user.Enabled = EnabledBox.IsChecked == true;
        user.AllowedFolderIds = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        if (!user.IsAdmin && user.AllowedFolderIds.Count == 0 &&
            !Ui.Confirm(L.T("WebUser_NoFolderConfirm"))) return;
        try
        {
            WebHost.Users.Upsert(user, pw);
            DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}
