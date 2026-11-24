using MailVault.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MailVault.Core;
using MailVault.Core.OAuth;
using MailVault.Core.Sources;
using Microsoft.Win32;

namespace MailVault.App.Dialogs;

public partial class FolderItem : ObservableObject
{
    public required string Path { get; init; }
    public string Display => string.IsNullOrEmpty(Path) ? L.T("Profile_MainFolder") : Path;
    [ObservableProperty] bool _isChecked = true;
}

public partial class ProfileEditorWindow : Window
{
    readonly ArchiveProfile _profile;
    readonly ObservableCollection<FolderItem> _folders = new();
    bool _foldersLoaded;

    public ArchiveProfile Profile => _profile;
    public bool RunAfterSave { get; private set; }

    public ProfileEditorWindow(ArchiveProfile profile)
    {
        InitializeComponent();
        _profile = profile.Clone();
        FolderList.ItemsSource = _folders;
        LoadFromProfile();
        Closed += (_, _) => RestoreOAuthIfCancelled();
    }

    bool IsServer => _profile.Type is ProfileType.Imap or ProfileType.Pop3;

    void LoadFromProfile()
    {
        var p = _profile;
        TypeText.Text = p.TypeDisplayName;
        NameBox.Text = p.Name;
        ServerPanel.Visibility = IsServer ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyNote.Visibility = IsServer ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = IsServer ? Visibility.Collapsed : Visibility.Visible;
        RecursiveBox.Visibility = p.Type == ProfileType.EmlFolder ? Visibility.Visible : Visibility.Collapsed;
        ThunderbirdProfileBox.Visibility = p.Type is ProfileType.Thunderbird or ProfileType.OutlookPst ? Visibility.Visible : Visibility.Collapsed;
        OutlookHint.Visibility = p.Type == ProfileType.OutlookPst ? Visibility.Visible : Visibility.Collapsed;
        FileHeader.Text = p.Type switch
        {
            ProfileType.Mbox => L.T("Profile_MboxFile"),
            ProfileType.EmlFolder => L.T("Profile_EmlFolder"),
            ProfileType.Thunderbird => L.T("Profile_ThunderbirdProfile"),
            ProfileType.OutlookPst => L.T("Profile_OutlookFile"),
            _ => L.T("Profile_Source"),
        };

        UserBox.Text = p.UserName;
        PasswordBox.Password = p.Password;
        HostBox.Text = p.Host;
        PortBox.Text = p.Port > 0 ? p.Port.ToString() : "";
        SecurityBox.SelectedIndex = (int)p.Security;
        IgnoreCertBox.IsChecked = p.AcceptInvalidCertificates;
        LoadAuthChoices(); // nach den Serverfeldern: OAuth gibt Server und Verschlüsselung fest vor

        PathBox.Text = p.Path;
        RecursiveBox.IsChecked = p.Recursive;
        if (p.Type == ProfileType.Thunderbird)
        {
            ThunderbirdProfileBox.Items.Add(L.T("Profile_AllThunderbird"));
            foreach (var prof in ThunderbirdSource.FindProfiles()) ThunderbirdProfileBox.Items.Add(prof);
            ThunderbirdProfileBox.SelectedIndex = string.IsNullOrEmpty(p.Path) ? 0 : Math.Max(0, ThunderbirdProfileBox.Items.IndexOf(p.Path));
        }
        else if (p.Type == ProfileType.OutlookPst)
        {
            var files = OutlookPstSource.FindDataFiles();
            ThunderbirdProfileBox.Items.Add(files.Count == 0 ? L.T("Profile_NoOutlookFiles") : L.T("Profile_FoundOutlookFiles"));
            foreach (var file in files) ThunderbirdProfileBox.Items.Add(file);
            if (string.IsNullOrEmpty(p.Path) && files.Count > 0) ThunderbirdProfileBox.SelectedIndex = 1;
            else ThunderbirdProfileBox.SelectedIndex = Math.Max(0, ThunderbirdProfileBox.Items.IndexOf(p.Path));
        }

        SpecialFoldersBox.IsChecked = p.ExcludeSpecialFolders;
        if (p.ExcludeFolders.Count > 0)
        {
            ExcludedPanel.Visibility = Visibility.Visible;
            ExcludedBox.Text = string.Join(Environment.NewLine, p.ExcludeFolders);
        }
        NewerThanBox.IsChecked = p.OnlyNewerThanDays.HasValue;
        NewerThanDays.Text = (p.OnlyNewerThanDays ?? 30).ToString();
        TargetBox.Text = p.TargetFolder;

        IntervalBox.ItemsSource = Intervals;
        IntervalBox.SelectedValue = Intervals.Any(i => i.Key == p.IntervalMinutes) ? p.IntervalMinutes : 60;
        DailyTimeBox.Text = p.DailyTime;
        ScheduleBox.SelectedIndex = (int)p.Schedule;
        UpdateScheduleControls();
    }

    static readonly KeyValuePair<int, string>[] Intervals =
    {
        new(15, L.T("Profile_Every15")), new(30, L.T("Profile_Every30")), new(60, L.T("Profile_Hourly")), new(120, L.F("Profile_EveryHours", 2)),
        new(240, L.F("Profile_EveryHours", 4)), new(360, L.F("Profile_EveryHours", 6)), new(720, L.F("Profile_EveryHours", 12)),
    };

    // ------------------------------------------------------------------ Anmeldung (Kennwort oder OAuth)

    bool _oauthTouched;
    OAuthAccount? _oauthBefore;
    CancellationTokenSource? _signInCts;

    /// <summary>Kennwort immer; Microsoft/Google nur, wenn diese MailVault-Version dafür eingerichtet ist.</summary>
    void LoadAuthChoices()
    {
        AuthBox.Items.Clear();
        AuthBox.Items.Add(new ComboBoxItem { Content = L.T("Profile_AuthPassword"), Tag = MailAuth.Password });
        foreach (var provider in new[] { OAuthProviders.Microsoft, OAuthProviders.Google })
            if (provider.IsConfigured || _profile.AuthMethod == provider.Kind)
                AuthBox.Items.Add(new ComboBoxItem
                {
                    Content = L.T(provider.Kind == MailAuth.Microsoft ? "Profile_AuthMicrosoft" : "Profile_AuthGoogle"),
                    Tag = provider.Kind,
                });
        var single = AuthBox.Items.Count == 1;
        AuthBox.Visibility = AuthLabel.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SelectAuth(_profile.AuthMethod);
    }

    MailAuth SelectedAuth => (AuthBox.SelectedItem as ComboBoxItem)?.Tag is MailAuth a ? a : MailAuth.Password;

    void SelectAuth(MailAuth kind)
    {
        foreach (ComboBoxItem item in AuthBox.Items)
            if (item.Tag is MailAuth a && a == kind) { AuthBox.SelectedItem = item; break; }
        if (AuthBox.SelectedIndex < 0) AuthBox.SelectedIndex = 0;
        UpdateAuthControls();
    }

    void Auth_Changed(object sender, SelectionChangedEventArgs e) => UpdateAuthControls();

    void UpdateAuthControls()
    {
        if (OAuthPanel is null) return;
        var kind = SelectedAuth;
        var oauth = kind != MailAuth.Password;
        PasswordBox.Visibility = PasswordLabel.Visibility = oauth ? Visibility.Collapsed : Visibility.Visible;
        OAuthPanel.Visibility = oauth ? Visibility.Visible : Visibility.Collapsed;
        AppPasswordHint.Visibility = oauth ? Visibility.Collapsed : Visibility.Visible;
        // Server und Verschlüsselung sind bei Microsoft/Google fest vorgegeben.
        HostBox.IsEnabled = SecurityPanel.IsEnabled = IgnoreCertBox.IsEnabled = !oauth;
        if (OAuthProviders.For(kind) is not { } provider) return;
        HostBox.Text = _profile.Type == ProfileType.Pop3 ? provider.Pop3Host : provider.ImapHost;
        PortBox.Text = "";
        SecurityBox.SelectedIndex = (int)ConnectionSecurity.SslOnConnect;
        IgnoreCertBox.IsChecked = false;
        var account = OAuthService.Current?.Store.Get(_profile.Id);
        var signedIn = account is not null && account.Provider == kind;
        SignInButton.Content = L.F(signedIn ? "Profile_SignInAgain" : "Profile_SignInWith", provider.DisplayName);
        OAuthStatus.Text = signedIn ? L.F("Profile_SignedInAs", account!.Account.Length > 0 ? account.Account : provider.DisplayName)
                                    : L.T("Profile_NotSignedIn");
        // Google: Das Gmail-Häkchen auf der Zustimmungsseite ist nicht vorausgewählt und wird leicht übersehen.
        OAuthHint.Text = kind == MailAuth.Google ? L.T("Profile_GoogleConsentHint") : "";
        OAuthHint.Visibility = kind == MailAuth.Google ? Visibility.Visible : Visibility.Collapsed;
    }

    async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (OAuthService.Current is not { } service || OAuthProviders.For(SelectedAuth) is not { } provider) return;
        ErrorText.Text = "";
        if (!_oauthTouched) { _oauthBefore = service.Store.Get(_profile.Id); _oauthTouched = true; }
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SetSigningIn(true);
        OAuthStatus.Text = L.T("Profile_SignInWaiting");
        try
        {
            var hint = UserBox.Text.Contains('@') ? UserBox.Text.Trim() : null;
            var tokens = await service.SignInAsync(provider.Kind, hint, url =>
            {
                try { Services.Ui.OpenWithShell(url); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Dispatcher.Invoke(() => OAuthStatus.Text = L.F("Profile_OpenBrowserManually", url));
                }
            }, _signInCts.Token);
            service.Remember(_profile.Id, provider.Kind, tokens);
            if (!string.IsNullOrWhiteSpace(tokens.Account)) UserBox.Text = tokens.Account;
            if (NameBox.Text.Trim().Length == 0 && !string.IsNullOrWhiteSpace(tokens.Account)) NameBox.Text = tokens.Account;
            UpdateAuthControls();
            Activate(); // Fenster nach der Browser-Anmeldung wieder nach vorn
        }
        catch (OperationCanceledException)
        {
            UpdateAuthControls();
            OAuthStatus.Text = L.T("Profile_SignInCancelled");
        }
        catch (OAuthException ex)
        {
            UpdateAuthControls();
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SetSigningIn(false);
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    void SignInCancel_Click(object sender, RoutedEventArgs e) => _signInCts?.Cancel();

    void SetSigningIn(bool busy)
    {
        SignInButton.IsEnabled = AuthBox.IsEnabled = SaveRunButton.IsEnabled = TestButton.IsEnabled = !busy;
        SignInCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Editor abgebrochen: eine hier vorgenommene Anmeldung wieder zurücknehmen.</summary>
    void RestoreOAuthIfCancelled()
    {
        _signInCts?.Cancel();
        if (DialogResult == true || !_oauthTouched || OAuthService.Current is not { } service) return;
        service.Store.Restore(_profile.Id, _oauthBefore);
        service.InvalidateCache(_profile.Id);
    }

    void Schedule_Changed(object sender, SelectionChangedEventArgs e) => UpdateScheduleControls();

    void UpdateScheduleControls()
    {
        if (IntervalBox is null || DailyPanel is null) return;
        IntervalBox.Visibility = ScheduleBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        DailyPanel.Visibility = ScheduleBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Schreibt die Formularwerte ins Profil; liefert eine Fehlermeldung oder null.</summary>
    string? ApplyToProfile()
    {
        var p = _profile;
        p.UserName = UserBox.Text.Trim();
        p.AuthMethod = IsServer ? SelectedAuth : MailAuth.Password;
        p.Password = p.AuthMethod == MailAuth.Password ? PasswordBox.Password : "";
        p.Host = HostBox.Text.Trim();
        p.Port = int.TryParse(PortBox.Text, out var port) && port is > 0 and < 65536 ? port : 0;
        p.Security = (ConnectionSecurity)Math.Max(0, SecurityBox.SelectedIndex);
        p.AcceptInvalidCertificates = IgnoreCertBox.IsChecked == true;
        p.Path = p.Type == ProfileType.Thunderbird && ThunderbirdProfileBox.SelectedIndex <= 0 && string.IsNullOrWhiteSpace(PathBox.Text)
            ? "" : PathBox.Text.Trim();
        p.Recursive = RecursiveBox.IsChecked == true;
        p.ExcludeSpecialFolders = SpecialFoldersBox.IsChecked == true;
        p.TargetFolder = TargetBox.Text.Trim();

        if (_foldersLoaded)
            p.ExcludeFolders = _folders.Where(f => !f.IsChecked).Select(f => f.Path).ToList();
        else
            p.ExcludeFolders = ExcludedBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (NewerThanBox.IsChecked == true)
        {
            if (!int.TryParse(NewerThanDays.Text, out var d) || d < 1) return L.T("Profile_InvalidDays");
            p.OnlyNewerThanDays = d;
        }
        else p.OnlyNewerThanDays = null;

        p.Schedule = (ScheduleKind)Math.Max(0, ScheduleBox.SelectedIndex);
        if (IntervalBox.SelectedValue is int minutes) p.IntervalMinutes = minutes;
        if (p.Schedule == ScheduleKind.Daily)
        {
            if (!ArchiveProfile.TryParseTime(DailyTimeBox.Text, out var t)) return L.T("Profile_InvalidTime");
            p.DailyTime = t.ToString(@"hh:mm");
        }


        if (IsServer)
        {
            if (p.Host.Length == 0) return L.T("Profile_EnterServer");
            if (p.UserName.Length == 0) return L.T("Profile_EnterUser");
            if (OAuthProviders.For(p.AuthMethod) is { } provider)
            {
                var acc = OAuthService.Current?.Store.Get(p.Id);
                if (acc is null || acc.Provider != p.AuthMethod) return L.F("Profile_SignInRequired", provider.DisplayName);
            }
        }
        else if (p.Type == ProfileType.Mbox && !File.Exists(p.Path)) return L.T("Profile_MboxMissing");
        else if (p.Type == ProfileType.EmlFolder && !Directory.Exists(p.Path)) return L.T("Profile_FolderMissing");
        else if (p.Type == ProfileType.OutlookPst && !File.Exists(p.Path)) return L.T("Profile_PstMissing");

        p.Name = NameBox.Text.Trim();
        if (p.Name.Length == 0) p.Name = $"{p.TypeDisplayName} – {p.EffectiveTargetFolder}";
        return null;
    }

    CancellationTokenSource? _autoConfigCts;

    void UserBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Sobald eine Adresse eingegeben ist, die Servereinstellungen ermitteln.
        if (HostBox.Text.Length == 0 && UserBox.Text.Contains('@')) _ = RunAutoConfigAsync();
    }

    void AutoConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!UserBox.Text.Contains('@'))
        {
            ShowAutoConfig(L.T("Profile_EnterFullAddress"), warn: true);
            return;
        }
        _ = RunAutoConfigAsync();
    }

    async Task RunAutoConfigAsync()
    {
        _autoConfigCts?.Cancel();
        var cts = _autoConfigCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var email = UserBox.Text.Trim();
        AutoConfigButton.IsEnabled = false;
        ShowAutoConfig(L.T("Profile_Discovering"), warn: false);
        try
        {
            var result = await AutoConfig.DiscoverAsync(email, cts.Token);
            if (cts != _autoConfigCts) return; // inzwischen neu gestartet
            if (result is null)
            {
                ShowAutoConfig(L.T("Profile_NothingFound"), warn: true);
                return;
            }
            var imap = _profile.Type == ProfileType.Imap;
            var s = imap ? result.Imap : result.Pop3;
            if (s is null)
            {
                ShowAutoConfig(imap ? L.T("Profile_NoImap") : L.T("Profile_NoPop3"), warn: true);
                return;
            }
            HostBox.Text = s.Host;
            PortBox.Text = s.Port > 0 ? s.Port.ToString() : "";
            SecurityBox.SelectedIndex = (int)s.Security;
            if (!string.Equals(UserBox.Text.Trim(), s.UserName, StringComparison.OrdinalIgnoreCase)) UserBox.Text = s.UserName;
            // Outlook.com/Microsoft 365 bzw. Gmail: gleich die passende Anmeldung vorschlagen, wenn sie eingerichtet ist.
            if (OAuthProviders.For(OAuthProviders.Suggest(email, s.Host)) is { IsConfigured: true } suggested && SelectedAuth == MailAuth.Password)
                SelectAuth(suggested.Kind);
            var text = L.F("Profile_Configured", s.Host, s.Port, SecurityName(s.Security), result.Source);
            if (result.Hint is not null) text += Environment.NewLine + result.Hint;
            ShowAutoConfig(text, warn: result.Hint is not null);
        }
        catch (OperationCanceledException) { if (cts == _autoConfigCts) ShowAutoConfig(L.T("Profile_DiscoveryTimeout"), warn: true); }
        catch (Exception ex) { ShowAutoConfig(L.F("Profile_DiscoveryFailed", MailVault.Core.ErrorText.Describe(ex)), warn: true); }
        finally { if (cts == _autoConfigCts) AutoConfigButton.IsEnabled = true; }
    }

    static string SecurityName(ConnectionSecurity s) => s switch
    {
        ConnectionSecurity.SslOnConnect => "SSL/TLS",
        ConnectionSecurity.StartTls => "STARTTLS",
        ConnectionSecurity.None => L.T("Profile_SecUnencrypted"),
        _ => L.T("Profile_SecAutomatic"),
    };

    void ShowAutoConfig(string text, bool warn)
    {
        AutoConfigText.Text = text;
        AutoConfigText.Foreground = warn ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x5A, 0x00))
                                         : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x6B, 0x3A));
        AutoConfigText.Visibility = Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_profile.Type == ProfileType.Mbox)
        {
            var dlg = new OpenFileDialog { Filter = L.T("Profile_MboxFilter"), Title = L.T("Profile_PickMbox") };
            if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
        }
        else if (_profile.Type == ProfileType.OutlookPst)
        {
            var dlg = new OpenFileDialog { Filter = L.T("Profile_PstFilter"), Title = L.T("Profile_PickPst") };
            if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = L.T("Profile_PickFolder") };
            if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
        }
    }

    void ThunderbirdProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThunderbirdProfileBox.SelectedIndex > 0) PathBox.Text = (string)ThunderbirdProfileBox.SelectedItem;
        else if (_profile.Type == ProfileType.Thunderbird) PathBox.Text = "";
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (ApplyToProfile() is { } err) { ErrorText.Text = err; return; }
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var profile = _profile.Clone();
            var folders = await Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var source = SourceFactory.Create(profile);
                await source.ConnectAsync(cts.Token);
                return await source.ListFoldersAsync(cts.Token);
            });
            _folders.Clear();
            foreach (var f in folders)
            {
                var path = string.Join("/", f);
                _folders.Add(new FolderItem
                {
                    Path = path,
                    IsChecked = ImportJob.FolderFilter(new ArchiveProfile { ExcludeFolders = _profile.ExcludeFolders, ExcludeSpecialFolders = false }, f),
                });
            }
            _foldersLoaded = true;
            FolderList.Visibility = Visibility.Visible;
            ExcludedPanel.Visibility = Visibility.Collapsed;
            FolderHint.Text = L.F("Profile_FoldersFound", _folders.Count);
        }
        catch (Exception ex)
        {
            ErrorText.Text = L.F("Profile_ConnectFailed", ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e) => Finish(false);
    void SaveRun_Click(object sender, RoutedEventArgs e) => Finish(true);

    void Finish(bool run)
    {
        if (ApplyToProfile() is { } err) { ErrorText.Text = err; return; }
        RunAfterSave = run;
        DialogResult = true;
    }
}
