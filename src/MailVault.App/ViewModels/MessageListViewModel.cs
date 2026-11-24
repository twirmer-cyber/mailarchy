using MailVault.App.Localization;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailVault.App.Services;
using MailVault.Core;
using Microsoft.Win32;

namespace MailVault.App.ViewModels;

/// <summary>Nachrichtenliste mit Vorschau – für Ordneransicht und Suchergebnisse.</summary>
public partial class MessageListViewModel : ObservableObject
{
    readonly Func<List<MessageSummary>> _loader;

    [ObservableProperty] string _title;
    [ObservableProperty] ObservableCollection<MessageSummary> _messages = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedMessageId))] MessageSummary? _selectedMessage;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] bool _isBusy;

    public long? SelectedMessageId => SelectedMessage?.Id;
    /// <summary>Vom DataGrid gesetzte Mehrfachauswahl.</summary>
    public IList SelectedItems { get; set; } = new List<object>();

    public MessageListViewModel(string title, Func<List<MessageSummary>> loader)
    {
        _title = title;
        _loader = loader;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = L.T("List_Loading");
        try
        {
            var list = await Task.Run(_loader);
            Messages = new ObservableCollection<MessageSummary>(list);
            StatusText = list.Count switch
            {
                0 => L.T("List_NoneFound"),
                1 => L.T("List_OneEmail"),
                _ => L.F("List_Count", list.Count),
            };
        }
        catch (ArgumentException ex) { StatusText = ex.Message; Messages = new(); }
        catch (Exception ex) { StatusText = MailVault.Core.ErrorText.Describe(ex); }
        finally { IsBusy = false; }
    }

    List<MessageSummary> Selection()
    {
        var selected = SelectedItems.OfType<MessageSummary>().ToList();
        if (selected.Count == 0 && SelectedMessage is { } m) selected.Add(m);
        return selected;
    }

    /// <summary>Wird vom Hauptfenster abonniert: öffnet den KI-Assistenten mit einer Frage zu dieser E-Mail.</summary>
    public static event Action<MessageSummary>? AiRequested;

    [RelayCommand]
    void AskAi()
    {
        if (SelectedMessage is { } m) AiRequested?.Invoke(m);
    }

    [RelayCommand]
    void Open()
    {
        if (SelectedMessage is not { } m) return;
        var raw = AppState.Archive.GetRawMessage(m.Id);
        Ui.OpenTempFile($"{Exporter.SafeName(m.Subject, 60)}.eml", raw);
    }

    [RelayCommand]
    void SaveAs()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        if (sel.Count == 1)
        {
            var dlg = new SaveFileDialog { FileName = Exporter.SafeName(sel[0].Subject, 80) + ".eml", Filter = L.T("List_EmlFilter") };
            if (dlg.ShowDialog() == true) File.WriteAllBytes(dlg.FileName, AppState.Archive.GetRawMessage(sel[0].Id));
            return;
        }
        var fd = new OpenFolderDialog { Title = L.F("List_SaveManyTitle", sel.Count) };
        if (fd.ShowDialog() != true) return;
        var ids = sel.Select(s => s.Id).ToList();
        Ui.RunWithProgress(L.T("List_Saving"), async (p, ct) =>
        {
            var n = await Exporter.ExportAsync(AppState.Archive, ids, fd.FolderName, ExportFormat.EmlFiles, false, p, ct);
            return L.F("List_Saved", n);
        });
    }

    [RelayCommand]
    void SavePdf()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var ids = sel.Select(s => s.Id).ToList();
        if (sel.Count == 1)
        {
            var dlg = new SaveFileDialog { FileName = Exporter.SafeName(sel[0].Subject, 80) + ".pdf", Filter = L.T("List_PdfFilter") };
            if (dlg.ShowDialog() != true) return;
            var file = dlg.FileName;
            if (Ui.RunWithProgress(L.T("List_SavingPdf"), async (p, ct) =>
                {
                    using var pdf = new PdfExport();
                    await pdf.WriteAsync(ids[0], file, ct);
                    return L.F("List_Saved", 1);
                }, autoClose: true) is not null)
                Ui.OpenWithShell(file);
            return;
        }
        var fd = new OpenFolderDialog { Title = L.F("List_SaveManyTitle", sel.Count) };
        if (fd.ShowDialog() != true) return;
        Ui.RunWithProgress(L.T("List_SavingPdf"), async (p, ct) =>
        {
            using var pdf = new PdfExport();
            var n = await Exporter.ExportAsync(AppState.Archive, ids, fd.FolderName, ExportFormat.Pdf, false, p, ct, pdf);
            return L.F("List_Saved", n);
        });
    }

    [RelayCommand]
    async Task DeleteAsync()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var text = sel.Count == 1 ? L.F("List_DeleteOne", sel[0].Subject)
                                  : L.F("List_DeleteMany", sel.Count);
        if (!Ui.Confirm(text)) return;
        await Task.Run(() => AppState.Archive.DeleteMessages(sel.Select(s => s.Id)));
        foreach (var s in sel) Messages.Remove(s);
        StatusText = L.F("List_Deleted", sel.Count);
        AppState.NotifyArchiveChanged();
    }

    [RelayCommand]
    void CopyToClipboard()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
            sel.Select(s => $"{s.DateUtc.ToLocalTime():g}\t{s.From}\t{s.Subject}")));
    }
}
