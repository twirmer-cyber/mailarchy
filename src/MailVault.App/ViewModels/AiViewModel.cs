using MailVault.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Services;
using MailVault.Core;
using MailVault.Core.Ai;

namespace MailVault.App.ViewModels;

public sealed record ScopeChoice(long? FolderId, string Display);

/// <summary>KI-Bereich: Fragen an das Archiv (mit Beispielen) und das Beziehungsnetz "Wer kennt wen".</summary>
public partial class AiViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly List<AiHtml.ChatEntry> _entries = new();
    AiAssistant? _assistant;
    long? _assistantScope = -1;
    CancellationTokenSource? _cts;

    public IReadOnlyList<AiPromptCategory> Categories => AiPromptLibrary.Categories;

    [ObservableProperty] string _question = "";
    [ObservableProperty] string _chatHtml = "";
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] ObservableCollection<ScopeChoice> _scopes = new();
    [ObservableProperty] ScopeChoice? _scope;
    [ObservableProperty] bool _isReady;
    [ObservableProperty] string _setupHint = "";
    [ObservableProperty] int _selectedTab;

    // Beziehungsnetz
    [ObservableProperty] string _networkHtml = "";
    [ObservableProperty] int _networkSize = 50;
    [ObservableProperty] int _networkRange;   // 0 = alles, 1 = 12 Monate, 2 = 3 Jahre, 3 = 5 Jahre
    [ObservableProperty] bool _hideAutomated = true;
    [ObservableProperty] string _networkStatus = "";
    ContactNetwork? _network;

    public AiViewModel(MainViewModel main)
    {
        _main = main;
        AiSettingsViewModel.AiSettingsChanged += () => { _assistant = null; Refresh(); };
        AppState.ArchiveChanged += RefreshScopes;
        RefreshScopes();
        Render();
    }

    public void Refresh()
    {
        var ai = AppState.Settings.Ai;
        IsReady = ai.IsConfigured && ai.ConsentGiven;
        SetupHint = !ai.IsConfigured
            ? L.T("Ai_NeedKey")
            : !ai.ConsentGiven ? L.T("Ai_NeedConsent") : "";
        if (_network is null) _ = BuildNetworkAsync();
    }

    void RefreshScopes()
    {
        var selected = Scope?.FolderId;
        var list = new List<ScopeChoice> { new(null, L.T("Ai_AllMailboxes")) };
        list.AddRange(AppState.Archive.GetFolderTree().Select(f => new ScopeChoice(f.Id, $"{f.Name} ({f.TotalCount:N0})")));
        Scopes = new(list);
        Scope = list.FirstOrDefault(s => s.FolderId == selected) ?? list[0];
    }

    string? CitationTitle(long id)
    {
        var s = AppState.Archive.GetSummary(id);
        return s is null ? null : L.F("Ai_CitationFormat", s.DateUtc.ToLocalTime(), s.FromName, string.IsNullOrWhiteSpace(s.Subject) ? L.T("Common_NoSubject") : s.Subject);
    }

    void Render(string? pending = null) => ChatHtml = AiHtml.Chat(_entries, pending, CitationTitle);

    [RelayCommand]
    void UsePrompt(AiPrompt? p)
    {
        if (p is null) return;
        Question = p.Prompt;
        SelectedTab = 0;
    }

    [RelayCommand]
    void NewConversation()
    {
        _cts?.Cancel();
        _assistant?.Reset();
        _entries.Clear();
        StatusText = "";
        Render();
    }

    [RelayCommand]
    void Cancel() => _cts?.Cancel();

    [RelayCommand]
    async Task AskAsync()
    {
        var q = Question.Trim();
        if (q.Length == 0 || IsBusy) return;
        Refresh();
        if (!IsReady) { Ui.Info(SetupHint); return; }
        if (q.Contains('[') && System.Text.RegularExpressions.Regex.IsMatch(q, @"\[(?!#\d)[^\]]+\]") &&
            !Ui.Confirm(L.T("Ai_PlaceholderConfirm"))) return;

        var scopeId = Scope?.FolderId;
        if (_assistant is null || _assistantScope != scopeId)
        {
            var tools = new AiArchiveTools(AppState.Archive, scopeId is { } id ? new[] { id } : null, AppState.Settings.Ai.MaxEmailsRead);
            try { _assistant = new AiAssistant(AppState.Settings.Ai, tools); }
            catch (Exception ex) { Ui.Error(ErrorText.Describe(ex)); return; }
            _assistantScope = scopeId;
            if (_entries.Count > 0) _entries.Add(new AiHtml.ChatEntry(false, L.F("Ai_ScopeChanged", Scope?.Display)));
        }

        _entries.Add(new AiHtml.ChatEntry(true, q));
        Question = "";
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(s => { StatusText = s; Render(s); });
        Render(L.T("Ai_Searching"));
        try
        {
            var a = await _assistant.AskAsync(q, progress, _cts.Token);
            var meta = L.F("Ai_Meta", AiModels.All.FirstOrDefault(m => m.Id == AppState.Settings.Ai.Model).Name?.Split('–')[0].Trim() ?? AppState.Settings.Ai.Model,
                           a.ToolCalls, a.EmailsRead, a.InputTokens, a.OutputTokens);
            _entries.Add(new AiHtml.ChatEntry(false, a.Text, meta));
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            _entries.Add(new AiHtml.ChatEntry(false, L.T("Ai_Canceled")));
            _assistant = null; // Verlauf könnte unvollständig sein
            StatusText = "";
        }
        catch (Exception ex)
        {
            _entries.Add(new AiHtml.ChatEntry(false, L.F("Ai_Error", ErrorText.Describe(ex))));
            _assistant = null;
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
            Render();
        }
    }

    /// <summary>Klick auf einen Beleg im Gesprächsverlauf.</summary>
    public void OpenMessage(long id)
    {
        if (AppState.Archive.GetSummary(id) is null) { Ui.Info(L.T("Ai_MessageGone")); return; }
        new Dialogs.MessageWindow(id) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    /// <summary>Von außen: Frage zu einer bestimmten E-Mail vorbereiten (Kontextmenü "Mit KI analysieren").</summary>
    public void PrepareForMessage(MessageSummary m)
    {
        SelectedTab = 0;
        Question = L.F("Ai_MessagePrompt", m.Id, m.Subject);
    }

    // ------------------------------------------------------------------ Beziehungsnetz

    partial void OnNetworkSizeChanged(int value) => _ = BuildNetworkAsync();
    partial void OnNetworkRangeChanged(int value) => _ = BuildNetworkAsync();
    partial void OnHideAutomatedChanged(bool value) => _ = BuildNetworkAsync();

    [RelayCommand]
    async Task BuildNetworkAsync()
    {
        NetworkStatus = L.T("Ai_NetworkComputing");
        var size = NetworkSize;
        var from = NetworkRange switch { 1 => DateTime.UtcNow.AddMonths(-12), 2 => DateTime.UtcNow.AddYears(-3), 3 => DateTime.UtcNow.AddYears(-5), _ => (DateTime?)null };
        var owners = AppState.Profiles.All.Where(p => p.UserName.Contains('@')).Select(p => p.UserName).ToList();
        var hide = HideAutomated;
        try
        {
            var net = await Task.Run(() => ContactNetwork.Build(AppState.Archive.GetHeaderRows(), owners, maxNodes: size, minMessages: 2, hideAutomated: hide, fromUtc: from));
            _network = net;
            NetworkHtml = AiHtml.Network(net);
            NetworkStatus = net.Nodes.Count <= 1
                ? L.T("Ai_NetworkTooFew")
                : L.F("Ai_NetworkStats", net.Nodes.Count - net.OwnerAddresses.Count, net.MessageCount);
        }
        catch (Exception ex) { NetworkStatus = MailVault.Core.ErrorText.Describe(ex); }
    }

    [RelayCommand]
    async Task InterpretNetworkAsync()
    {
        Refresh();
        if (!IsReady) { Ui.Info(SetupHint); return; }
        if (_network is null || _network.Nodes.Count < 3) { Ui.Info(L.T("Ai_NetworkTooSmall")); return; }
        if (!Ui.Confirm(L.T("Ai_InterpretConfirm"))) return;
        SelectedTab = 0;
        _entries.Add(new AiHtml.ChatEntry(true, L.T("Ai_InterpretQuestion")));
        IsBusy = true;
        Render(L.T("Ai_Interpreting"));
        try
        {
            // Anweisung an Claude bleibt deutsch; nur die Antwortsprache folgt der Oberfläche.
            var system = $"""
                Du analysierst das E-Mail-Kontaktnetz einer Privatperson (Archivbesitzer). Du bekommst nur Adressen, Namen, Zählwerte und
                wer gemeinsam in denselben E-Mails vorkommt. Antworte auf {L.T("Ai_AnswerLanguage")} in einfachem Markdown (Überschriften in dieser Sprache):
                ## Gruppen – ordne die Kontakte plausiblen Gruppen zu (Familie, Freundeskreis, Arbeit/Kollegen, Vereine, Behörden, Firmen/Dienstleister …),
                begründe kurz (Domain, gemeinsame E-Mails, Namen) und kennzeichne Vermutungen als solche.
                ## Wer kennt wen – die stärksten Verbindungen zwischen Kontakten (nicht zum Besitzer) und "Brückenpersonen", die mehrere Gruppen verbinden.
                ## Entwicklung – Kontakte, die neu hinzukamen oder eingeschlafen sind (erster/letzter Kontakt).
                ## Auffälligkeiten – z. B. sehr einseitige Kommunikation.
                Erfinde keine Fakten über Personen; es geht nur um Muster in den Daten. Nenne Personen mit Namen (oder Adresse, wenn kein Name).
                """;
            var (text, inTok, outTok) = await AiAssistant.CompleteAsync(AppState.Settings.Ai, system, _network.ToPromptText(), CancellationToken.None);
            _entries.Add(new AiHtml.ChatEntry(false, text, L.F("Ai_InterpretMeta", inTok, outTok)));
        }
        catch (Exception ex) { _entries.Add(new AiHtml.ChatEntry(false, L.F("Ai_Error", ErrorText.Describe(ex)))); }
        finally { IsBusy = false; Render(); }
    }

    /// <summary>Klick auf eine Person im Netz: ihre E-Mails in der Suche anzeigen.</summary>
    public void ShowContact(string address) => _main.ShowSearch($"\"{address}\"");
}
