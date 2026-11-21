using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Localization;
using MailVault.App.Services;
using MailVault.Core;
using MailVault.Core.Updates;

namespace MailVault.App.ViewModels;

/// <summary>Verwaltung → Updates: Version anzeigen, nach Updates suchen, mit einem Klick aktualisieren.</summary>
public partial class UpdateViewModel : ObservableObject
{
    [ObservableProperty] string _currentVersion = "";
    [ObservableProperty] bool _isInstalled;
    [ObservableProperty] int _channelIndex;        // 0 = stabil, 1 = auch Vorabversionen
    [ObservableProperty] bool _autoCheck;
    [ObservableProperty] bool _autoInstall;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] bool _statusIsError;
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] bool _updateAvailable;
    [ObservableProperty] string _updateTitle = "";
    [ObservableProperty] ObservableCollection<string> _changes = new();
    UpdateCheck? _check;
    bool _loading;

    public static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\', '/');
    public string InstallTarget => UpdateManager.DefaultInstallDir;

    public void Refresh()
    {
        _loading = true;
        CurrentVersion = BuildInfo.Current.Display;
        var info = InstallInfo.Load(InstallDir);
        IsInstalled = info is not null;
        ChannelIndex = info?.IncludesPrereleases == true ? 1 : 0;
        AutoCheck = AppState.Settings.Updates.AutoCheck;
        AutoInstall = AppState.Settings.Updates.AutoInstall;
        _loading = false;
        if (!IsInstalled)
            Status(L.T("Upd_NotInstalledStatus"), false);
        else if (UpdateService.Pending is { } pending)
            ApplyCheck(pending);
    }

    /// <summary>Woher Updates kommen (Anzeige).</summary>
    public string ServerDisplay => new Uri(UpdateManager.FeedBaseFromEnvironment ?? UpdateManager.DefaultFeedBase).Host;

    void Status(string text, bool error) { StatusText = text; StatusIsError = error; }

    partial void OnAutoCheckChanged(bool value)
    {
        if (_loading) return;
        AppState.Settings.Updates.AutoCheck = value;
        AppState.Settings.Save();
    }

    partial void OnAutoInstallChanged(bool value)
    {
        if (_loading) return;
        AppState.Settings.Updates.AutoInstall = value;
        AppState.Settings.Save();
    }

    partial void OnChannelIndexChanged(int value)
    {
        if (_loading || InstallInfo.Load(InstallDir) is not { } info) return;
        info.Channel = value == 1 ? "beta" : "stable";
        info.Save(InstallDir);
        UpdateAvailable = false;
        Status(value == 1 ? L.T("Upd_ChannelBetaStatus") : L.T("Upd_ChannelStableStatus"), false);
    }

    [RelayCommand]
    async Task CheckAsync()
    {
        if (!IsInstalled) { Status(L.T("Upd_OnlyInstalled"), true); return; }
        IsBusy = true;
        UpdateAvailable = false;
        Status(L.T("Upd_Searching"), false);
        try
        {
            var check = await UpdateService.CheckNowAsync();
            if (check is null) { Status(L.T("Upd_OnlyInstalled"), true); return; }
            ApplyCheck(check);
        }
        catch (Exception ex) { Status(L.F("Upd_CheckFailed", ErrorText.Describe(ex)), true); }
        finally { IsBusy = false; }
    }

    public void ApplyCheck(UpdateCheck check)
    {
        _check = check;
        if (check.Problem is not null) { Status(check.Problem, true); return; }
        UpdateAvailable = check.Available;
        Changes = new(check.Changes);
        UpdateTitle = L.F("Upd_AvailableTitle", check.TargetVersion);
        Status(check.Available ? "" : L.F("Upd_UpToDate", check.TargetVersion), false);
    }

    [RelayCommand]
    async Task InstallAsync()
    {
        if (_check is not { Available: true } check) return;
        if (!Ui.Confirm(L.F("Upd_ConfirmInstall", check.TargetVersion)))
            return;
        var manager = await UpdateService.CreateManagerAsync();
        if (manager is null) return;
        string? staging = null;
        Ui.RunWithProgress(L.F("Upd_ProgressTitle", check.TargetVersion), async (p, ct) =>
        {
            var progress = new Progress<(string Text, double? Percent)>(s => p.Report(new ProgressInfo(s.Text, (int)(s.Percent ?? 0), s.Percent is null ? null : 100, s.Text)));
            staging = await manager.DownloadAsync(check, progress, ct);
            return L.T("Upd_Ready");
        }, autoClose: true);
        if (staging is null) return; // abgebrochen oder fehlgeschlagen – Meldung stand im Fortschrittsfenster
        UpdateService.ApplyAndRestart(manager, staging, check.TargetVersion);
    }

    [RelayCommand]
    void InstallApp() => App.Current.InstallFromThisCopy(fromRunningApp: true);
}
