using MailVault.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Services;
using MailVault.Core;
using MailVault.Core.Ai;
using MailVault.Core.OAuth;
using MailVault.Web;

namespace MailVault.App.ViewModels;

// ---------------------------------------------------------------- Automatik & Benachrichtigung

public sealed record ProfileScheduleRow(string Name, string Schedule, string Next, string Status, bool HasError);

public partial class AutomationSettingsViewModel : ObservableObject
{
    [ObservableProperty] bool _startWithWindows;
    [ObservableProperty] bool _closeToTray;
    [ObservableProperty] string _autostartHint = "";
    [ObservableProperty] ObservableCollection<ProfileScheduleRow> _schedules = new();

    [ObservableProperty] bool _notifyEnabled;
    [ObservableProperty] string _notifyTo = "";
    [ObservableProperty] string _smtpHost = "";
    [ObservableProperty] string _smtpPort = "";
    [ObservableProperty] int _smtpSecurity;
    [ObservableProperty] string _smtpUser = "";
    [ObservableProperty] string _smtpFrom = "";
    [ObservableProperty] bool _smtpIgnoreCert;
    [ObservableProperty] string _failureThreshold = "2";
    [ObservableProperty] string _repeatHours = "24";
    [ObservableProperty] bool _notifyOnRecovery = true;
    [ObservableProperty] string _notifyStatus = "";
    [ObservableProperty] bool _notifyStatusIsError;
    [ObservableProperty] bool _isBusy;

    /// <summary>Vom PasswordBox im Code-Behind gesetzt.</summary>
    public string SmtpPassword { get; set; } = "";
    bool _loading;

    public AutomationSettingsViewModel()
    {
        AppState.ProfilesChanged += RefreshSchedules;
        AppState.RunStateChanged += RefreshSchedules;
    }

    public void Refresh()
    {
        _loading = true;
        var s = AppState.Settings;
        StartWithWindows = AppState.Autostart.IsEnabled(AppState.ExePath);
        AutostartHint = !StartWithWindows && AppState.Autostart.RegisteredCommand is { } cmd
            ? L.F("Auto_OtherAutostart", cmd)
            : "";
        CloseToTray = s.CloseToTray;
        var n = s.Notification;
        NotifyEnabled = n.Enabled;
        NotifyTo = n.To;
        SmtpHost = n.SmtpHost;
        SmtpPort = n.SmtpPort > 0 ? n.SmtpPort.ToString() : "";
        SmtpSecurity = (int)n.Security;
        SmtpUser = n.UserName;
        SmtpFrom = n.From;
        SmtpIgnoreCert = n.AcceptInvalidCertificates;
        SmtpPassword = n.Password;
        FailureThreshold = n.FailureThreshold.ToString();
        RepeatHours = n.RepeatHours.ToString();
        NotifyOnRecovery = n.NotifyOnRecovery;
        _loading = false;
        RefreshSchedules();
    }

    void RefreshSchedules()
    {
        if (AppState.Scheduler is null) return;
        Schedules = new(AppState.Profiles.All.OrderBy(p => p.Name).Select(p =>
        {
            var next = AppState.Scheduler.NextRunUtc(p);
            var nextText = AppState.RunningProfile?.Id == p.Id ? L.T("Auto_Running")
                : next is null ? "–" : next <= DateTime.UtcNow ? L.T("Auto_Due") : L.F("Auto_NextFormat", next.Value.ToLocalTime());
            var status = p.LastError is not null ? L.F("Auto_Error", p.ConsecutiveFailures, p.LastError)
                : p.LastRunUtc is { } last ? L.F("Auto_LastRun", last.ToLocalTime(), p.LastResult) : L.T("Auto_NotRunYet");
            return new ProfileScheduleRow(p.Name, p.ScheduleDisplay, nextText, status, p.LastError is not null);
        }));
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            AppState.Autostart.Set(value, AppState.ExePath);
            AppState.Settings.StartWithWindows = value;
            AppState.Settings.Save();
            AutostartHint = value ? L.T("Auto_AutostartOn") : "";
        }
        catch (Exception ex) { Ui.Error(L.F("Auto_AutostartFailed", ErrorText.Describe(ex))); }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loading) return;
        AppState.Settings.CloseToTray = value;
        AppState.Settings.Save();
    }

    string? Apply(NotificationSettings n)
    {
        n.Enabled = NotifyEnabled;
        n.To = NotifyTo.Trim();
        n.SmtpHost = SmtpHost.Trim();
        n.SmtpPort = int.TryParse(SmtpPort, out var port) && port is > 0 and < 65536 ? port : 0;
        n.Security = (ConnectionSecurity)Math.Max(0, SmtpSecurity);
        n.UserName = SmtpUser.Trim();
        n.From = SmtpFrom.Trim();
        n.AcceptInvalidCertificates = SmtpIgnoreCert;
        n.Password = SmtpPassword;
        n.FailureThreshold = int.TryParse(FailureThreshold, out var t) ? Math.Clamp(t, 1, 50) : 2;
        n.RepeatHours = int.TryParse(RepeatHours, out var h) ? Math.Clamp(h, 1, 24 * 14) : 24;
        n.NotifyOnRecovery = NotifyOnRecovery;
        if (!n.Enabled) return null;
        if (!n.IsConfigured) return L.T("Auto_IncompleteFull");
        try { SmtpNotifier.ParseRecipients(n.To); } catch (FormatException ex) { return ex.Message; }
        return null;
    }

    void Status(string text, bool error) { NotifyStatus = text; NotifyStatusIsError = error; }

    [RelayCommand]
    void SaveNotification()
    {
        var n = new NotificationSettings();
        if (Apply(n) is { } err) { Status(err, true); return; }
        AppState.Settings.Notification = n;
        AppState.Settings.Save();
        Status(n.Enabled ? L.T("Auto_SavedOn") : L.T("Auto_SavedOff"), false);
    }

    [RelayCommand]
    async Task SendTestAsync()
    {
        var n = new NotificationSettings();
        var err = Apply(n);
        if (!n.IsConfigured) err ??= L.T("Auto_Incomplete");
        if (err is not null) { Status(err, true); return; }
        IsBusy = true;
        Status(L.T("Auto_Sending"), false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await ArchiveScheduler.SendTestAsync(n, cts.Token);
            Status(L.F("Auto_Sent", n.To), false);
        }
        catch (Exception ex) { Status(L.F("Auto_SendFailed", ErrorText.Describe(ex)), true); }
        finally { IsBusy = false; }
    }

    /// <summary>SMTP-Server über die Absenderadresse automatisch ermitteln (gleiches Verfahren wie bei den Postfächern).</summary>
    [RelayCommand]
    async Task AutoConfigAsync()
    {
        var email = (SmtpFrom.Contains('@') ? SmtpFrom : SmtpUser).Trim();
        if (!email.Contains('@')) { Status(L.T("Auto_EnterSender"), true); return; }
        IsBusy = true;
        Status(L.T("Auto_Discovering"), false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var r = await AutoConfig.DiscoverAsync(email, cts.Token);
            if (r?.Smtp is not { } smtp) { Status(L.T("Auto_NoSmtp"), true); return; }
            SmtpHost = smtp.Host;
            SmtpPort = smtp.Port > 0 ? smtp.Port.ToString() : "";
            SmtpSecurity = (int)smtp.Security;
            if (string.IsNullOrWhiteSpace(SmtpUser)) SmtpUser = smtp.UserName;
            if (string.IsNullOrWhiteSpace(SmtpFrom)) SmtpFrom = email;
            Status(L.F("Auto_Found", smtp.Host, smtp.Port, r.Source) + (r.Hint is null ? "" : " " + r.Hint), r.Hint is not null);
        }
        catch (Exception ex) { Status(L.F("Profile_DiscoveryFailed", ErrorText.Describe(ex)), true); }
        finally { IsBusy = false; }
    }

    /// <summary>Zugangsdaten aus einem vorhandenen IMAP/POP3-Profil übernehmen.</summary>
    [RelayCommand]
    async Task FromProfileAsync()
    {
        var p = AppState.Profiles.All.FirstOrDefault(x => x.IsServer && x.UserName.Contains('@'));
        if (p is null) { Status(L.T("Auto_NoProfile"), true); return; }
        SmtpUser = p.UserName;
        SmtpFrom = p.UserName;
        SmtpPassword = p.Password;
        PasswordReloadRequested?.Invoke();
        if (string.IsNullOrWhiteSpace(NotifyTo)) NotifyTo = p.UserName;
        await AutoConfigAsync();
    }

    public event Action? PasswordReloadRequested;
}

// ---------------------------------------------------------------- Webzugriff

public partial class WebUserRow : ObservableObject
{
    public required WebUser User { get; init; }
    public string UserName => User.UserName;
    public string Role => User.IsAdmin ? L.T("Web_RoleAdmin") : L.T("Web_RoleUser");
    public string Access { get; init; } = "";
    public string State => User.Enabled ? (User.LastLoginUtc is { } l ? L.F("Web_LastLogin", l.ToLocalTime()) : L.T("Web_NeverLoggedIn")) : L.T("Web_Locked");
}

public partial class WebSettingsViewModel : ObservableObject
{
    [ObservableProperty] bool _enabled;
    [ObservableProperty] string _port = "8025";
    [ObservableProperty] bool _lanAccess = true;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] bool _statusIsError;
    [ObservableProperty] ObservableCollection<string> _urls = new();
    [ObservableProperty] ObservableCollection<WebUserRow> _users = new();
    [ObservableProperty] WebUserRow? _selectedUser;
    bool _loading;

    public WebSettingsViewModel() => WebHost.StateChanged += UpdateStatus;

    public void Refresh()
    {
        _loading = true;
        var w = AppState.Settings.Web;
        Enabled = w.Enabled;
        Port = w.Port.ToString();
        LanAccess = w.LanAccess;
        _loading = false;
        ReloadUsers();
        UpdateStatus();
    }

    void ReloadUsers()
    {
        var tree = AppState.Archive.GetFolderTree();
        var names = new Dictionary<long, string>();
        void Walk(ArchiveFolder f, string prefix) { var p = prefix + f.Name; names[f.Id] = p; foreach (var c in f.Children) Walk(c, p + "/"); }
        foreach (var r in tree) Walk(r, "");
        Users = new(WebHost.Users.All.OrderBy(u => u.UserName).Select(u => new WebUserRow
        {
            User = u,
            Access = u.IsAdmin ? L.T("Web_All") : u.AllowedFolderIds.Count == 0 ? L.T("Web_NothingShared")
                : string.Join(", ", u.AllowedFolderIds.Select(id => names.TryGetValue(id, out var n) ? n : L.F("Web_DeletedFolder", id))),
        }));
    }

    void UpdateStatus()
    {
        Urls = new(WebHost.Urls);
        if (WebHost.LastError is { } err) { StatusText = L.F("Web_StartFailed", err); StatusIsError = true; }
        else if (WebHost.IsRunning)
        {
            StatusText = Users.Count == 0
                ? L.T("Web_RunningNoUsers")
                : L.T("Web_Running");
            StatusIsError = false;
        }
        else { StatusText = L.T("Web_Off"); StatusIsError = false; }
    }

    async Task ApplyAsync()
    {
        if (_loading) return;
        if (!int.TryParse(Port, out var port) || port is < 1024 or > 65535) { StatusText = L.T("Web_InvalidPort"); StatusIsError = true; return; }
        var w = AppState.Settings.Web;
        w.Enabled = Enabled;
        w.Port = port;
        w.LanAccess = LanAccess;
        AppState.Settings.Save();
        StatusText = Enabled ? L.T("Web_Starting") : L.T("Web_Stopping");
        await WebHost.RestartAsync();
        UpdateStatus();
    }

    partial void OnEnabledChanged(bool value) => _ = ApplyAsync();
    partial void OnLanAccessChanged(bool value) { if (Enabled) _ = ApplyAsync(); }

    [RelayCommand] Task ApplyPortAsync() => ApplyAsync();

    [RelayCommand] void OpenInBrowser() { if (WebHost.Urls.FirstOrDefault() is { } u) Ui.OpenWithShell(u); }

    [RelayCommand]
    void AddUser()
    {
        var dlg = new Dialogs.WebUserWindow(null) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() == true) ReloadUsers();
        UpdateStatus();
    }

    [RelayCommand]
    void EditUser(WebUserRow? row)
    {
        if ((row ?? SelectedUser) is not { } r) return;
        var dlg = new Dialogs.WebUserWindow(r.User) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() == true) { WebHost.SignOut(r.UserName); ReloadUsers(); }
    }

    [RelayCommand]
    void DeleteUser(WebUserRow? row)
    {
        if ((row ?? SelectedUser) is not { } r) return;
        if (!Ui.Confirm(L.F("Web_DeleteUserConfirm", r.UserName))) return;
        WebHost.Users.Delete(r.UserName);
        WebHost.SignOut(r.UserName);
        ReloadUsers();
    }
}

// ---------------------------------------------------------------- Anmeldung (OAuth)

/// <summary>Eigene Client-IDs für Google und Microsoft (Vorrang vor den eingebauten, siehe docs/OAUTH.md).</summary>
public partial class OAuthSettingsViewModel : ObservableObject
{
    public const string GuideUrl = "https://mailvault.zerze.de/#anleitungen";

    [ObservableProperty] string _googleClientId = "";
    [ObservableProperty] string _microsoftClientId = "";
    [ObservableProperty] string _googleState = "";
    [ObservableProperty] string _microsoftState = "";
    [ObservableProperty] bool _googleReady;
    [ObservableProperty] bool _microsoftReady;
    [ObservableProperty] string _status = "";
    [ObservableProperty] bool _statusIsError;

    /// <summary>Vom PasswordBox im Code-Behind gesetzt.</summary>
    public string GoogleClientSecret { get; set; } = "";

    public void Refresh()
    {
        var o = AppState.Settings.OAuth ??= new();
        GoogleClientId = o.GoogleClientId ?? "";
        MicrosoftClientId = o.MicrosoftClientId ?? "";
        GoogleClientSecret = o.GoogleClientSecret;
        Status = "";
        UpdateStates();
    }

    void UpdateStates()
    {
        (GoogleState, GoogleReady) = StateOf(MailAuth.Google);
        (MicrosoftState, MicrosoftReady) = StateOf(MailAuth.Microsoft);
    }

    static (string, bool) StateOf(MailAuth kind)
    {
        var provider = OAuthProviders.For(kind)!;
        if (OAuthProviders.UsesOwnClient(kind))
            return (OAuthProviders.HasBuiltIn(kind) ? L.T("OAuthSet_StateOwnOverBuiltIn") : L.T("OAuthSet_StateOwn"), true);
        if (provider.IsConfigured) return (L.T("OAuthSet_StateBuiltIn"), true);
        return (L.F("OAuthSet_StateNone", provider.DisplayName), false);
    }

    [RelayCommand]
    void Save()
    {
        var gid = GoogleClientId.Trim();
        var mid = MicrosoftClientId.Trim();
        var secret = GoogleClientSecret.Trim();
        if (!OAuthProviders.IsPlausibleClientId(MailAuth.Google, gid)) { Report(L.F("OAuthSet_InvalidId", "Google"), true); return; }
        if (!OAuthProviders.IsPlausibleClientId(MailAuth.Microsoft, mid)) { Report(L.F("OAuthSet_InvalidId", "Microsoft"), true); return; }
        if (gid.Length > 0 && secret.Length == 0) { Report(L.T("OAuthSet_GoogleSecretMissing"), true); return; }

        var o = new OAuthClientSettings
        {
            GoogleClientId = gid.Length > 0 ? gid : null,
            MicrosoftClientId = mid.Length > 0 ? mid : null,
        };
        if (gid.Length > 0) o.GoogleClientSecret = secret;
        AppState.Settings.OAuth = o;
        AppState.Settings.Save();
        OAuthProviders.ApplyUserClients(o);
        UpdateStates();

        // Anmeldungen, die mit einer anderen Client-ID ausgestellt wurden, gelten nicht mehr.
        var stale = new List<string>();
        if (OAuthService.Current is { } service)
            foreach (var kind in new[] { MailAuth.Google, MailAuth.Microsoft })
                foreach (var id in service.Store.StaleFor(kind, OAuthProviders.For(kind)!.ClientId))
                {
                    service.InvalidateCache(id);
                    if (AppState.Profiles.All.FirstOrDefault(p => p.Id == id) is { } p) stale.Add(p.Name);
                }
        Report(stale.Count == 0 ? L.T("OAuthSet_Saved") : L.F("OAuthSet_SavedReauth", string.Join(", ", stale)), false);
    }

    [RelayCommand] void OpenGuide() => Ui.OpenWithShell(GuideUrl);

    void Report(string text, bool error) { Status = text; StatusIsError = error; }
}

// ---------------------------------------------------------------- KI

public partial class AiSettingsViewModel : ObservableObject
{
    public IReadOnlyList<KeyValuePair<string, string>> Models { get; } = AiModels.All.Select(m => new KeyValuePair<string, string>(m.Id, m.Name)).ToList();
    [ObservableProperty] string _model = AiModels.Default;
    [ObservableProperty] bool _consent;
    [ObservableProperty] string _maxEmailsRead = "25";
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] bool _statusIsError;
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] bool _hasKey;
    public string ApiKey { get; set; } = "";

    public void Refresh()
    {
        var a = AppState.Settings.Ai;
        Model = AiModels.All.Any(m => m.Id == a.Model) ? a.Model : AiModels.Default;
        Consent = a.ConsentGiven;
        MaxEmailsRead = a.MaxEmailsRead.ToString();
        ApiKey = a.ApiKey;
        HasKey = a.IsConfigured;
        StatusText = HasKey ? L.T("AiSet_KeyStored") : "";
        StatusIsError = false;
    }

    [RelayCommand]
    void Save()
    {
        var a = AppState.Settings.Ai;
        if (!string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.Trim().StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            StatusText = L.T("AiSet_NotAKey"); StatusIsError = true; return;
        }
        a.ApiKey = ApiKey;
        a.Model = Model;
        a.ConsentGiven = Consent;
        a.MaxEmailsRead = int.TryParse(MaxEmailsRead, out var m) ? Math.Clamp(m, 1, 200) : 25;
        AppState.Settings.Save();
        HasKey = a.IsConfigured;
        StatusText = L.T("AiSet_Saved"); StatusIsError = false;
        AiSettingsChanged?.Invoke();
    }

    public static event Action? AiSettingsChanged;

    [RelayCommand]
    async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) { StatusText = L.T("AiSet_EnterKey"); StatusIsError = true; return; }
        IsBusy = true;
        StatusText = L.T("AiSet_Testing");
        StatusIsError = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reply = await AiAssistant.TestAsync(ApiKey, Model, cts.Token);
            StatusText = L.F("AiSet_TestOk", reply);
        }
        catch (Exception ex) { StatusText = L.F("AiSet_TestFailed", ErrorText.Describe(ex)); StatusIsError = true; }
        finally { IsBusy = false; }
    }

    [RelayCommand] void OpenConsole() => Ui.OpenWithShell("https://console.anthropic.com/settings/keys");
}
