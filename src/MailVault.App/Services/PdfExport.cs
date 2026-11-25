using System.IO;
using System.Windows;
using MailVault.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailVault.App.Services;

/// <summary>
/// PDF-Export über die Druckfunktion von WebView2 (dieselbe Darstellung wie in der Vorschau, ohne zusätzliche
/// Bibliothek). Ein unsichtbares Fenster außerhalb des Bildschirms trägt den Browser; es wird für einen ganzen
/// Export wiederverwendet. Aufrufe dürfen von jedem Thread kommen, gerendert wird im UI-Thread.
/// </summary>
public sealed class PdfExport : IMessagePdfWriter, IDisposable
{
    Window? _host;
    WebView2? _view;
    TaskCompletionSource<bool>? _navigated;

    public async Task WriteAsync(long messageId, string file, CancellationToken ct)
    {
        // Nachricht außerhalb des UI-Threads laden (Archivzugriff, Entschlüsselung)
        var msg = AppState.Archive.LoadMessage(messageId);
        var folder = AppState.Archive.GetSummary(messageId)?.FolderPath;
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var html = MailHtml.BuildForPrint(msg, folder);
            var view = await EnsureViewAsync();
            _navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _allowNext = true;
            view.CoreWebView2.NavigateToString(html.Length < 1_500_000 ? html : MailHtml.BuildForPrint(TextOnly(msg), folder));
            if (!await _navigated.Task) throw new InvalidOperationException(Localization.L.T("Pdf_RenderFailed"));
            var settings = view.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds = true;
            settings.MarginTop = settings.MarginBottom = 0.5;
            settings.MarginLeft = settings.MarginRight = 0.55;
            var tmp = file + ".tmp";
            if (!await view.CoreWebView2.PrintToPdfAsync(tmp, settings))
                throw new InvalidOperationException(Localization.L.T("Pdf_RenderFailed"));
            File.Move(tmp, file, overwrite: true);
        }).Task.Unwrap();
    }

    /// <summary>Sehr große HTML-Mails (z. B. viele eingebettete Bilder) als reinen Text drucken.</summary>
    static MimeKit.MimeMessage TextOnly(MimeKit.MimeMessage msg)
    {
        var copy = new MimeKit.MimeMessage(msg.From, msg.To, msg.Subject, new MimeKit.TextPart("plain") { Text = msg.TextBody ?? MessageIndexer.HtmlToText(msg.HtmlBody ?? "") }) { Date = msg.Date };
        copy.Cc.AddRange(msg.Cc);
        return copy;
    }

    bool _allowNext;

    async Task<WebView2> EnsureViewAsync()
    {
        if (_view is not null) return _view;
        _view = new WebView2();
        _host = new Window
        {
            Width = 900, Height = 1200, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Content = _view, Title = "MailArchy PDF",
        };
        _host.Show();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await _view.EnsureCoreWebView2Async(env);
        var s = _view.CoreWebView2.Settings;
        s.IsScriptEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        // Nur das eigene HTML laden; Links in der Mail werden nicht aufgerufen
        _view.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (_allowNext) { _allowNext = false; return; }
            e.Cancel = true;
        };
        _view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        _view.CoreWebView2.NavigationCompleted += (_, e) => _navigated?.TrySetResult(e.IsSuccess);
        return _view;
    }

    public void Dispose()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;
        Application.Current.Dispatcher.Invoke(() => { _view?.Dispose(); host.Close(); });
    }
}
