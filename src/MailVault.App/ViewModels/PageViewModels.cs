using MailVault.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Dialogs;
using MailVault.App.Services;
using MailVault.Core;
using Microsoft.Win32;

namespace MailVault.App.ViewModels;

// ---------------------------------------------------------------- Startseite

public partial class StartViewModel : ObservableObject
{
    readonly MainViewModel _main;
    [ObservableProperty] string _quickSearch = "";
    [ObservableProperty] string _messageCount = "";
    [ObservableProperty] string _archiveSize = "";
    /// <summary>„Original 12,4 GB · 59 % gespart“ (bzw. Hinweis, solange es nichts zu sparen gibt).</summary>
    [ObservableProperty] string _sizeDetail = "";
    [ObservableProperty] string _checkTitle = "";
    [ObservableProperty] string _checkDetail = "";
    [ObservableProperty] bool _checkOk;
    [ObservableProperty] bool _checkFailed;
    [ObservableProperty] bool _checkRunning;
    [ObservableProperty] string _checkProblems = "";
    [ObservableProperty] string _dataDirectory = "";
    [ObservableProperty] string _dateRange = "";
    [ObservableProperty] string _lastArchived = "";
    [ObservableProperty] string _protectionText = "";
    [ObservableProperty] bool _showRecoveryWarning;
    [ObservableProperty] ObservableCollection<ArchiveProfile> _recentProfiles = new();

    public StartViewModel(MainViewModel main)
    {
        _main = main;
        ArchiveCheckService.Completed += Refresh;
        ArchiveCheckService.RunningChanged += RefreshCheck;
    }

    public void Refresh()
    {
        var s = AppState.Archive.GetStats();
        MessageCount = s.MessageCount.ToString("N0");
        ArchiveSize = Ui.FormatSize(s.DatabaseFileBytes);
        SizeDetail = s.MessageCount == 0 ? L.T("Start_SizeEmpty")
            : s.SavedFraction is { } saved ? L.F("Start_SizeSaved", Ui.FormatSize(s.TotalMessageBytes), saved)
            : L.F("Start_SizeOriginal", Ui.FormatSize(s.TotalMessageBytes));
        DataDirectory = AppState.Archive.DataDirectory;
        DateRange = s.OldestMessageUtc is { } o && s.NewestMessageUtc is { } n
            ? $"{o.ToLocalTime():d} – {n.ToLocalTime():d}" : "–";
        LastArchived = s.LastArchivedUtc is { } l ? l.ToLocalTime().ToString("g") : L.T("Start_Never");
        ProtectionText = s.IsProtected ? L.T("Start_Protected") : L.T("Start_NotProtected");
        ShowRecoveryWarning = s.IsProtected && !AppState.Archive.RecoveryKeyExported;
        RecentProfiles = new(AppState.Profiles.All.OrderByDescending(p => p.LastRunUtc ?? DateTime.MinValue).Take(5));
        RefreshCheck();
    }

    void RefreshCheck()
    {
        var c = AppState.Settings.ArchiveCheck;
        var current = c.LastUtc is not null && string.Equals(c.DataDirectory, AppState.Archive.DataDirectory, StringComparison.OrdinalIgnoreCase);
        CheckRunning = ArchiveCheckService.IsRunning;
        CheckOk = current && c.LastOk == true && !CheckRunning;
        CheckFailed = current && c.LastOk == false && !CheckRunning;
        CheckTitle = CheckRunning ? L.T("Start_CheckRunning") : CheckOk ? L.T("Start_CheckOk") : CheckFailed ? L.T("Start_CheckFailed") : L.T("Start_CheckNever");
        if (!CheckRunning)
            CheckDetail = current ? L.F("Start_CheckWhen", c.LastUtc!.Value.ToLocalTime(), c.LastMessages)
                : c.Automatic ? L.T("Start_CheckSoon") : L.T("Start_CheckOff");
        CheckProblems = CheckFailed ? string.Join(Environment.NewLine, c.LastProblems) : "";
    }

    [RelayCommand]
    async Task CheckNowAsync()
    {
        if (ArchiveCheckService.IsRunning) return;
        var progress = new Progress<ProgressInfo>(p => { if (p.Total is > 0) CheckDetail = L.F("Start_CheckProgress", (double)p.Processed / p.Total.Value); });
        CheckDetail = L.T("Start_CheckStarting");
        try { await ArchiveCheckService.RunAsync(progress, CancellationToken.None); }
        catch (Exception ex) { Ui.Error(ErrorText.Describe(ex)); }
        Refresh();
    }

    [RelayCommand] void OpenDataDirectory() => Ui.OpenWithShell(AppState.Archive.DataDirectory);

    [RelayCommand] void Search() => _main.ShowSearch(QuickSearch);
    [RelayCommand] void GoArchive() => _main.Navigate(Page.Archive);
    [RelayCommand] void GoExport() => _main.Navigate(Page.Export);
    [RelayCommand] void GoSearch() => _main.Navigate(Page.Search);
    [RelayCommand] void GoSettings() => _main.Navigate(Page.Settings);

    [RelayCommand]
    void RunProfile(ArchiveProfile? p)
    {
        if (p is null) return;
        ArchiveViewModel.Run(p);
        Refresh();
    }
}

// ---------------------------------------------------------------- Archivieren

public sealed record SourceTile(ProfileType Type, string Title, string Description, string Icon);

public partial class ArchiveViewModel : ObservableObject
{
    public IReadOnlyList<SourceTile> Sources { get; } = new[]
    {
        new SourceTile(ProfileType.Imap, L.T("Source_ImapTitle"), L.T("Source_ImapDesc"), ""),
        new SourceTile(ProfileType.Pop3, L.T("Source_Pop3Title"), L.T("Source_Pop3Desc"), ""),
        new SourceTile(ProfileType.OutlookPst, "Microsoft Outlook", L.T("Source_OutlookDesc"), ""),
        new SourceTile(ProfileType.Thunderbird, "Mozilla Thunderbird", L.T("Source_ThunderbirdDesc"), ""),
        new SourceTile(ProfileType.EmlFolder, L.T("Source_EmlTitle"), L.T("Source_EmlDesc"), ""),
        new SourceTile(ProfileType.Mbox, L.T("Profile_MboxFile"), L.T("Source_MboxDesc"), ""),
    };

    [ObservableProperty] ObservableCollection<ArchiveProfile> _profiles = new();
    [ObservableProperty] ArchiveProfile? _selectedProfile;

    public ArchiveViewModel()
    {
        AppState.ProfilesChanged += Reload;
        Reload();
    }

    void Reload()
    {
        var selected = SelectedProfile?.Id;
        Profiles = new(AppState.Profiles.All.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selected);
    }

    [RelayCommand]
    void NewProfile(SourceTile tile)
    {
        var profile = new ArchiveProfile { Type = tile.Type };
        if (tile.Type == ProfileType.Thunderbird) profile.Name = "Mozilla Thunderbird";
        Edit(profile);
    }

    [RelayCommand]
    void EditProfile(ArchiveProfile? p)
    {
        if ((p ?? SelectedProfile) is { } profile) Edit(profile);
    }

    static void Edit(ArchiveProfile profile)
    {
        var dlg = new ProfileEditorWindow(profile) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        AppState.Profiles.Save(dlg.Profile);
        AppState.NotifyProfilesChanged();
        if (dlg.RunAfterSave) Run(dlg.Profile);
    }

    [RelayCommand]
    void RunProfile(ArchiveProfile? p)
    {
        if ((p ?? SelectedProfile) is { } profile) Run(profile);
    }

    public static void Run(ArchiveProfile profile)
    {
        Ui.RunWithProgress(L.F("Archive_RunTitle", profile.Name), async (progress, ct) =>
        {
            if (AppState.Scheduler.IsRunning)
                progress.Report(new ProgressInfo(L.T("Archive_WaitForScheduled"), 0, null, L.T("Archive_WaitForScheduledLog")));
            var outcome = await AppState.Scheduler.RunNowAsync(profile, progress, ct);
            if (outcome.Error is not null) throw new InvalidOperationException(outcome.Error);
            return outcome.Result!.ToString();
        });
        AppState.NotifyArchiveChanged();
    }

    [RelayCommand]
    void DeleteProfile(ArchiveProfile? p)
    {
        if ((p ?? SelectedProfile) is not { } profile) return;
        if (!Ui.Confirm(L.F("Archive_DeleteConfirm", profile.Name))) return;
        AppState.Profiles.Delete(profile.Id);
        MailVault.Core.OAuth.OAuthService.Current?.Forget(profile.Id);
        AppState.NotifyProfilesChanged();
    }
}

// ---------------------------------------------------------------- Exportieren

public sealed record FolderChoice(long? Id, string Display, string Path);

public partial class ExportViewModel : ObservableObject
{
    readonly MainViewModel _main;
    [ObservableProperty] ObservableCollection<FolderChoice> _folderChoices = new();
    [ObservableProperty] FolderChoice? _selectedFolder;
    [ObservableProperty] bool _formatEml = true;
    [ObservableProperty] bool _formatMbox;
    [ObservableProperty] bool _formatPdf;
    [ObservableProperty] bool _keepStructure = true;
    [ObservableProperty] string _targetPath = "";

    public ExportViewModel(MainViewModel main)
    {
        _main = main;
        TargetPath = AppState.Settings.LastExportDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), L.T("Export_DefaultFolder"));
    }

    public void RefreshFolders()
    {
        var selected = SelectedFolder?.Id;
        var list = new List<FolderChoice> { new(null, L.T("Ai_WholeArchive"), "") };
        void Add(ArchiveFolder f, int depth)
        {
            list.Add(new FolderChoice(f.Id, new string(' ', depth * 4) + $"{f.Name} ({f.TotalCount:N0})", AppState.Archive.GetFolderPath(f.Id)));
            foreach (var c in f.Children) Add(c, depth + 1);
        }
        foreach (var r in AppState.Archive.GetFolderTree()) Add(r, 0);
        FolderChoices = new(list);
        SelectedFolder = list.FirstOrDefault(f => f.Id == selected) ?? list[0];
    }

    [RelayCommand]
    void Browse()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Export_PickTarget") };
        if (dlg.ShowDialog() == true) TargetPath = dlg.FolderName;
    }

    [RelayCommand]
    void Export()
    {
        if (string.IsNullOrWhiteSpace(TargetPath)) { Ui.Error(L.T("Export_NoTarget")); return; }
        var folder = SelectedFolder;
        var ids = folder?.Id is { } fid
            ? AppState.Archive.GetMessageIds(fid, recursive: true)
            : AppState.Archive.GetFolderTree().SelectMany(r => AppState.Archive.GetMessageIds(r.Id, true)).ToList();
        if (ids.Count == 0) { Ui.Info(L.T("Export_FolderEmpty")); return; }

        var format = FormatMbox ? ExportFormat.Mbox : FormatPdf ? ExportFormat.Pdf : ExportFormat.EmlFiles;
        var target = TargetPath.Trim();
        if (format == ExportFormat.Mbox && !KeepStructure)
            target = Path.Combine(target, Exporter.SafeName(string.IsNullOrEmpty(folder?.Path) ? L.T("Start_Archive") : folder!.Path.Replace('/', '_')) + ".mbox");
        AppState.Settings.LastExportDirectory = TargetPath.Trim();
        AppState.Settings.Save();

        var result = Ui.RunWithProgress(L.T("Nav_Export"), async (p, ct) =>
        {
            using var pdf = format == ExportFormat.Pdf ? new PdfExport() : null;
            var n = await Exporter.ExportAsync(AppState.Archive, ids, target, format, KeepStructure, p, ct, pdf);
            return L.F("Export_Done", n, target);
        });
        if (result is not null && Ui.Confirm(L.F("Export_OpenTarget", result)))
            Ui.OpenWithShell(Directory.Exists(target) ? target : Path.GetDirectoryName(target)!);
    }
}

// ---------------------------------------------------------------- Suchen

public sealed record Option<T>(T Value, string Display);

public partial class SearchViewModel : ObservableObject
{
    readonly MainViewModel _main;

    [ObservableProperty] string _query = "";
    [ObservableProperty] bool _inSubject = true;
    [ObservableProperty] bool _inFromTo = true;
    [ObservableProperty] bool _inBody = true;
    [ObservableProperty] bool _inAttachmentNames = true;
    [ObservableProperty] bool _inAttachmentContents;
    [ObservableProperty] string _from = "";
    [ObservableProperty] string _to = "";
    [ObservableProperty] Option<DateRangeType> _dateRange;
    [ObservableProperty] DateTime? _dateFrom;
    [ObservableProperty] DateTime? _dateTo;
    [ObservableProperty] string _days = "30";
    [ObservableProperty] Option<bool?> _attachments;
    [ObservableProperty] Option<int?> _priority;
    [ObservableProperty] string _sizeMinKb = "";
    [ObservableProperty] string _sizeMaxKb = "";
    [ObservableProperty] FolderChoice? _folder;
    [ObservableProperty] ObservableCollection<FolderChoice> _folders = new();
    [ObservableProperty] bool _showAdvanced;
    [ObservableProperty] MessageListViewModel? _results;
    /// <summary>0 = alle, 1 = letzte 30 Tage, 2 = dieses Jahr, 3 = letztes Jahr</summary>
    [ObservableProperty] int _quickRange;
    [ObservableProperty] bool _withAttachment;
    [ObservableProperty] bool _sortByRelevance;
    [ObservableProperty] bool _showHelp;
    [ObservableProperty] string _errorText = "";
    public IReadOnlyList<SearchSyntax.Help> SyntaxExamples => SearchSyntax.Examples;
    CancellationTokenSource? _live;

    public IReadOnlyList<Option<DateRangeType>> DateRanges { get; } = new[]
    {
        new Option<DateRangeType>(DateRangeType.NotSpecified, L.T("Search_Any")),
        new(DateRangeType.Today, L.T("Search_Today")),
        new(DateRangeType.Yesterday, L.T("Search_Yesterday")),
        new(DateRangeType.ThisWeek, L.T("Search_ThisWeek")),
        new(DateRangeType.LastWeek, L.T("Search_LastWeek")),
        new(DateRangeType.ThisMonth, L.T("Search_ThisMonth")),
        new(DateRangeType.LastMonth, L.T("Search_LastMonth")),
        new(DateRangeType.ThisYear, L.T("Search_ThisYear")),
        new(DateRangeType.LastYear, L.T("Search_LastYear")),
        new(DateRangeType.NotOlderThanDays, L.T("Search_NotOlderThan")),
        new(DateRangeType.OlderThanDays, L.T("Search_OlderThan")),
        new(DateRangeType.Custom, L.T("Search_CustomRange")),
    };
    public IReadOnlyList<Option<bool?>> AttachmentOptions { get; } = new[]
    {
        new Option<bool?>(null, L.T("Search_DontCare")), new(true, L.T("Search_WithAttachments")), new(false, L.T("Search_WithoutAttachments")),
    };
    public IReadOnlyList<Option<int?>> PriorityOptions { get; } = new[]
    {
        new Option<int?>(null, L.T("Search_DontCare")), new(1, L.T("Search_PrioHigh")), new(3, L.T("Search_PrioNormal")), new(5, L.T("Search_PrioLow")),
    };

    public bool IsCustomRange => DateRange.Value == DateRangeType.Custom;
    public bool IsDaysRange => DateRange.Value is DateRangeType.OlderThanDays or DateRangeType.NotOlderThanDays;
    partial void OnDateRangeChanged(Option<DateRangeType> value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        OnPropertyChanged(nameof(IsDaysRange));
    }

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        _dateRange = DateRanges[0];
        _attachments = AttachmentOptions[0];
        _priority = PriorityOptions[0];
        AppState.ArchiveChanged += RefreshFolders;
        RefreshFolders();
    }

    void RefreshFolders()
    {
        var selected = Folder?.Id;
        var list = new List<FolderChoice> { new(null, L.T("Search_AllFolders"), "") };
        void Add(ArchiveFolder f, int depth)
        {
            list.Add(new FolderChoice(f.Id, new string(' ', depth * 4) + f.Name, ""));
            foreach (var c in f.Children) Add(c, depth + 1);
        }
        foreach (var r in AppState.Archive.GetFolderTree()) Add(r, 0);
        Folders = new(list);
        Folder = list.FirstOrDefault(f => f.Id == selected) ?? list[0];
    }

    SearchCriteria BuildCriteria()
    {
        static long? Kb(string s) => long.TryParse(s, out var v) && v >= 0 ? v * 1024 : null;
        var criteria = new SearchCriteria
        {
            Query = Query,
            InSubject = InSubject,
            InFromToCcBcc = InFromTo,
            InBody = InBody,
            InAttachmentNames = InAttachmentNames,
            InAttachmentContents = InAttachmentContents,
            From = From,
            To = To,
            DateRange = DateRange.Value,
            DateFrom = DateFrom,
            DateTo = DateTo,
            Days = int.TryParse(Days, out var d) ? d : 30,
            HasAttachments = Attachments.Value,
            Priority = Priority.Value,
            SizeMinBytes = Kb(SizeMinKb),
            SizeMaxBytes = Kb(SizeMaxKb),
            FolderId = Folder?.Id,
            SortByRelevance = SortByRelevance,
        };
        // Filter-Chips (die erweiterte Suche hat Vorrang, wenn dort etwas eingestellt ist)
        if (criteria.DateRange == DateRangeType.NotSpecified)
            criteria.DateRange = QuickRange switch
            {
                1 => DateRangeType.NotOlderThanDays,
                2 => DateRangeType.ThisYear,
                3 => DateRangeType.LastYear,
                _ => DateRangeType.NotSpecified,
            };
        if (QuickRange == 1 && DateRange.Value == DateRangeType.NotSpecified) criteria.Days = 30;
        if (WithAttachment && criteria.HasAttachments is null) criteria.HasAttachments = true;
        return SearchSyntax.Apply(criteria); // von:, an:, betreff:, anhang:, jahr:, nach:, vor: im Suchfeld
    }

    /// <summary>Suche beim Tippen (verzögert) und bei jedem Filterwechsel, sobald schon gesucht wurde.</summary>
    partial void OnQueryChanged(string value) => ScheduleLiveSearch(value.Trim().Length >= 2 ? 400 : -1);
    partial void OnQuickRangeChanged(int value) => ScheduleLiveSearch(0);
    partial void OnWithAttachmentChanged(bool value) => ScheduleLiveSearch(0);
    partial void OnSortByRelevanceChanged(bool value) => ScheduleLiveSearch(0);

    async void ScheduleLiveSearch(int delayMs)
    {
        _live?.Cancel();
        if (delayMs < 0) return;
        if (delayMs == 0 && Results is null && string.IsNullOrWhiteSpace(Query)) return;
        var cts = _live = new CancellationTokenSource();
        try { await Task.Delay(delayMs, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (cts != _live) return;
        await RunSearchAsync(quiet: true);
    }

    [RelayCommand]
    Task SearchAsync() { _live?.Cancel(); return RunSearchAsync(quiet: false); }

    async Task RunSearchAsync(bool quiet)
    {
        SearchCriteria c;
        try { c = BuildCriteria(); }
        catch (ArgumentException ex) { ErrorText = ex.Message; return; }
        ErrorText = "";
        if (c.IsEmpty)
        {
            if (!quiet) ErrorText = L.T("Search_EnterTerm");
            return;
        }
        var title = string.IsNullOrWhiteSpace(Query) ? L.T("Search_Result") : L.F("Search_ResultFor", Query.Trim());
        var list = new MessageListViewModel(title, () => AppState.Archive.Search(c));
        Results = list;
        await list.RefreshAsync();
    }

    [RelayCommand]
    void InsertExample(SearchSyntax.Help? h)
    {
        if (h is null) return;
        var example = h.Example.Split("  ")[0];
        Query = string.IsNullOrWhiteSpace(Query) ? example : Query.TrimEnd() + " " + example;
    }

    [RelayCommand]
    void Reset()
    {
        Query = From = To = SizeMinKb = SizeMaxKb = "";
        InSubject = InFromTo = InBody = InAttachmentNames = true;
        InAttachmentContents = false;
        DateRange = DateRanges[0];
        Attachments = AttachmentOptions[0];
        Priority = PriorityOptions[0];
        Folder = Folders.FirstOrDefault();
        QuickRange = 0;
        WithAttachment = SortByRelevance = false;
        ErrorText = "";
        _live?.Cancel();
        Results = null;
    }
}

// ---------------------------------------------------------------- Einstellungen

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] string _dataDirectory = "";
    [ObservableProperty] bool _isProtected;
    [ObservableProperty] bool _recoveryKeyMissing;
    [ObservableProperty] bool _blockExternalContent;
    [ObservableProperty] string _statsText = "";

    public bool IsPortable => AppSettings.IsPortable;
    public AutomationSettingsViewModel Automation { get; } = new();
    public WebSettingsViewModel Web { get; } = new();
    public AiSettingsViewModel Ai { get; } = new();
    public UpdateViewModel Updates { get; } = new();
    public OAuthSettingsViewModel OAuth { get; } = new();
    [ObservableProperty] int _selectedTab;

    /// <summary>0 = wie Windows, 1 = hell, 2 = dunkel; wirkt sofort.</summary>
    public int ThemeIndex
    {
        get => AppState.Settings.Theme switch { ThemeManager.Light => 1, ThemeManager.Dark => 2, _ => 0 };
        set
        {
            var mode = value switch { 1 => ThemeManager.Light, 2 => ThemeManager.Dark, _ => ThemeManager.System };
            if (mode == AppState.Settings.Theme) return;
            AppState.Settings.Theme = mode;
            AppState.Settings.Save();
            ThemeManager.SetMode(mode);
            OnPropertyChanged();
        }
    }

    static readonly string[] LanguageCodes = { "system", "de", "en", "fr", "es" };

    /// <summary>0 = wie Windows, 1 = Deutsch, 2 = Englisch, 3 = Französisch, 4 = Spanisch; wirkt nach einem Neustart.</summary>
    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(LanguageCodes, AppState.Settings.Language));
        set
        {
            var code = LanguageCodes[Math.Clamp(value, 0, LanguageCodes.Length - 1)];
            if (code == AppState.Settings.Language) return;
            AppState.Settings.Language = code;
            AppState.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Startet MailVault neu, damit eine geänderte Sprache wirkt.</summary>
    [RelayCommand] void RestartApp() => App.Current.Restart();

    public void Refresh()
    {
        Automation.Refresh();
        Web.Refresh();
        Ai.Refresh();
        Updates.Refresh();
        OAuth.Refresh();
        DataDirectory = AppState.Archive.DataDirectory;
        IsProtected = AppState.Archive.IsProtected;
        RecoveryKeyMissing = IsProtected && !AppState.Archive.RecoveryKeyExported;
        BlockExternalContent = AppState.Settings.BlockExternalContent;
        var s = AppState.Archive.GetStats();
        StatsText = L.F("Settings_Stats", s.MessageCount, Ui.FormatSize(s.TotalMessageBytes), Ui.FormatSize(s.DatabaseFileBytes));
    }

    partial void OnBlockExternalContentChanged(bool value)
    {
        if (AppState.Settings.BlockExternalContent == value) return;
        AppState.Settings.BlockExternalContent = value;
        AppState.Settings.Save();
    }

    [RelayCommand] void OpenDataDirectory() => Ui.OpenWithShell(DataDirectory);

    [RelayCommand]
    void ChangeDataDirectory()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Settings_PickArchive") };
        if (dlg.ShowDialog() != true) return;
        AppState.Settings.DataDirectory = dlg.FolderName;
        AppState.Settings.Save();
        Ui.Info(L.T("Settings_LocationChanged"));
    }

    /// <summary>Gesichertes Archiv einbinden: übernehmen (zusammenführen) oder die Installation darauf umstellen.</summary>
    [RelayCommand]
    void ImportArchive()
    {
        var dlg = new ImportArchiveWindow(AppState.Archive.DataDirectory) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        if (dlg.SwitchToArchive)
        {
            AppState.Settings.DataDirectory = dlg.SourceDirectory;
            AppState.Settings.Save();
            if (Ui.Confirm(L.T("ImportArchive_RestartNow"))) App.Current.Restart();
            else Ui.Info(L.T("Settings_LocationChanged"));
            return;
        }
        var dir = dlg.SourceDirectory;
        var credentials = dlg.Credentials;
        Ui.RunWithProgress(L.T("ImportArchive_RunTitle"), async (p, ct) =>
            (await ArchiveImport.MergeAsync(AppState.Archive, dir, credentials, p, ct)).ToString());
        AppState.NotifyArchiveChanged();
        Refresh();
    }

    [RelayCommand]
    async Task EnableProtectionAsync()
    {
        var dlg = new PasswordDialog(L.T("Settings_ProtectTitle"), askCurrent: false, askNew: true) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        string? recovery = null;
        var pw = dlg.NewPassword;
        Ui.RunWithProgress(L.T("Settings_Encrypting"), async (p, ct) =>
        {
            p.Report(new ProgressInfo(L.T("Settings_EncryptingStatus"), 0, null));
            recovery = await Task.Run(() => AppState.Archive.EnableProtection(pw), ct);
            return L.T("Settings_Encrypted");
        });
        if (recovery is not null)
        {
            var rk = new RecoveryKeyWindow(recovery, AppState.Archive.CurrentKeyFile, DataDirectory) { Owner = System.Windows.Application.Current.MainWindow };
            rk.ShowDialog();
            if (rk.Exported) AppState.Archive.MarkRecoveryKeyExported();
        }
        Refresh();
        await Task.CompletedTask;
    }

    [RelayCommand]
    void ChangePassword()
    {
        var dlg = new PasswordDialog(L.T("Settings_ChangePasswordTitle"), askCurrent: true, askNew: true) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            AppState.Archive.ChangePassword(dlg.CurrentPassword, dlg.NewPassword);
            Ui.Info(L.T("Settings_PasswordChanged"));
        }
        catch (CryptographicException) { Ui.Error(L.T("Settings_OldPasswordWrong")); }
    }

    [RelayCommand]
    void DisableProtection()
    {
        var dlg = new PasswordDialog(L.T("Settings_RemoveProtectionTitle"), askCurrent: true, askNew: false) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var pw = dlg.CurrentPassword;
        if (!AppState.Archive.CheckPassword(pw)) { Ui.Error(L.T("Login_WrongPassword")); return; }
        if (!Ui.Confirm(L.T("Settings_RemoveProtectionConfirm"))) return;
        Ui.RunWithProgress(L.T("Settings_Decrypting"), async (p, ct) =>
        {
            await Task.Run(() => AppState.Archive.DisableProtection(pw), ct);
            return L.T("Settings_Decrypted");
        });
        Refresh();
    }

    /// <summary>Neuer Wiederherstellungsschlüssel samt Notfallblatt (z. B. wenn der alte verloren oder bekannt geworden ist).</summary>
    [RelayCommand]
    void NewRecoveryKey()
    {
        var dlg = new PasswordDialog(L.T("Settings_NewRecoveryKeyTitle"), askCurrent: true, askNew: false) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        string recovery;
        try { recovery = AppState.Archive.CreateNewRecoveryKey(dlg.CurrentPassword); }
        catch (CryptographicException) { Ui.Error(L.T("Login_WrongPassword")); return; }
        var rk = new RecoveryKeyWindow(recovery, AppState.Archive.CurrentKeyFile, DataDirectory, replaced: true) { Owner = System.Windows.Application.Current.MainWindow };
        rk.ShowDialog();
        if (rk.Exported) AppState.Archive.MarkRecoveryKeyExported();
        Refresh();
    }

    /// <summary>Prüft einen aufbewahrten Wiederherstellungsschlüssel, ohne etwas zu ändern.</summary>
    [RelayCommand]
    void CheckRecoveryKey()
    {
        var key = InputDialog.Ask(System.Windows.Application.Current.MainWindow, L.T("Settings_CheckRecoveryKey"),
            L.T("Settings_CheckRecoveryKeyPrompt"), confirmText: L.T("Settings_CheckRecoveryKeyButton"));
        if (string.IsNullOrWhiteSpace(key)) return;
        if (AppState.Archive.CheckRecoveryKey(key)) Ui.Info(L.T("Settings_RecoveryKeyValid"));
        else Ui.Error(L.T("Settings_RecoveryKeyInvalid"));
    }

    /// <summary>Kopie der Schlüsseldatei an einen zweiten Ort (allein nutzlos, mit Kennwort oder Schlüssel lebenswichtig).</summary>
    [RelayCommand]
    void BackupKeyFile()
    {
        if (AppState.Archive.CurrentKeyFile is not { } kf) return;
        var dlg = new SaveFileDialog { FileName = "MailVault-" + MailArchive.KeyFileName, Filter = L.T("Settings_KeyFileFilter") };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, KeyVault.Serialize(kf));
        Ui.Info(L.F("Settings_KeyFileSaved", dlg.FileName));
    }

    [RelayCommand]
    void RebuildIndex()
    {
        if (!Ui.Confirm(L.T("Settings_RebuildConfirm"))) return;
        Ui.RunWithProgress(L.T("Settings_RebuildIndex"), (p, ct) =>
        {
            AppState.Archive.RebuildIndex(p, ct);
            return Task.FromResult(L.T("Settings_Rebuilt"));
        });
    }

    [RelayCommand]
    void Compact()
    {
        Ui.RunWithProgress(L.T("Settings_Compact"), (p, ct) =>
        {
            var before = AppState.Archive.GetStats().DatabaseFileBytes;
            AppState.Archive.Compact(p, ct);
            var after = AppState.Archive.GetStats().DatabaseFileBytes;
            return Task.FromResult(L.F("Settings_CompactResult", Ui.FormatSize(before), Ui.FormatSize(after)));
        });
        Refresh();
    }

    [RelayCommand]
    void Verify()
    {
        Ui.RunWithProgress(L.T("Settings_Verify"), async (p, ct) =>
        {
            p.Report(new ProgressInfo(L.T("Settings_Checking"), 0, null));
            var r = await ArchiveCheckService.RunAsync(p, ct) ?? throw new InvalidOperationException(L.T("Start_CheckRunning"));
            return r.Ok ? L.F("Settings_ArchiveOkDetail", r.MessagesChecked, r.PartsChecked)
                        : L.F("Settings_ArchiveProblems", string.Join(Environment.NewLine, r.Problems));
        });
    }
}
