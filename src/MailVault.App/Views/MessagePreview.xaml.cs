using MailVault.App.Localization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using MailVault.App.Services;
using MailVault.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MimeKit;

namespace MailVault.App.Views;

public sealed record AttachmentItem(string FileName, long Size, MimeEntity Entity)
{
    public string Display => $"{FileName} ({Ui.FormatSize(Size)})";

    public byte[] GetContent()
    {
        using var ms = new MemoryStream();
        if (Entity is MimePart { Content: { } c }) c.DecodeTo(ms);
        else if (Entity is MessagePart { Message: { } m }) m.WriteTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Sichere Nachrichtenansicht: HTML wird in WebView2 mit deaktiviertem JavaScript und einer
/// Content-Security-Policy angezeigt, die externe Inhalte (Tracking-Pixel) standardmäßig blockiert.
/// </summary>
public partial class MessagePreview : UserControl
{
    public static readonly DependencyProperty MessageIdProperty = DependencyProperty.Register(
        nameof(MessageId), typeof(long?), typeof(MessagePreview),
        new PropertyMetadata(null, (d, _) => ((MessagePreview)d).OnMessageChanged()));

    public long? MessageId
    {
        get => (long?)GetValue(MessageIdProperty);
        set => SetValue(MessageIdProperty, value);
    }

    Task<bool>? _webViewReady;
    bool _allowNavigation;
    long? _externalAllowedFor;
    int _loadVersion;

    public MessagePreview() => InitializeComponent();

    async Task<bool> EnsureWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Browser.EnsureCoreWebView2Async(env);
            var s = Browser.CoreWebView2.Settings;
            s.IsScriptEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = true;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            Browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (_allowNavigation) { _allowNavigation = false; return; }
                e.Cancel = true;
                OpenExternalLink(e.Uri);
            };
            Browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternalLink(e.Uri);
            };
            return true;
        }
        catch (Exception)
        {
            return false; // WebView2-Laufzeit fehlt → Textanzeige
        }
    }

    static void OpenExternalLink(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "mailto"))
        {
            if (u.Scheme != "mailto" && !Ui.Confirm(L.F("Preview_OpenLink", u))) return;
            Ui.OpenWithShell(u.ToString());
        }
    }

    async void OnMessageChanged()
    {
        var version = ++_loadVersion;
        var id = MessageId;
        if (id is null)
        {
            ContentPanel.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        MimeMessage msg;
        MessageSummary? summary;
        try
        {
            (msg, summary) = await Task.Run(() => (AppState.Archive.LoadMessage(id.Value), AppState.Archive.GetSummary(id.Value)));
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            EmptyText.Text = L.F("Preview_LoadFailed", ErrorText.Describe(ex));
            ContentPanel.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        if (version != _loadVersion) return;

        SubjectText.Text = string.IsNullOrWhiteSpace(msg.Subject) ? L.T("Common_NoSubject") : msg.Subject;
        FromText.Text = MessageIndexer.FormatAddresses(msg.From);
        ToText.Text = MessageIndexer.FormatAddresses(msg.To);
        CcText.Text = MessageIndexer.FormatAddresses(msg.Cc);
        CcLabel.Visibility = CcText.Visibility = CcText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DateText.Text = msg.Date == DateTimeOffset.MinValue ? "" : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:dddd}, {0:g}", msg.Date.LocalDateTime);
        FolderText.Text = summary?.FolderPath ?? "";
        AttachmentList.ItemsSource = msg.Attachments.Select(a =>
        {
            var name = a is MimePart mp ? mp.FileName : (a as MessagePart)?.Message?.Subject + ".eml";
            long size = a is MimePart { Content.Stream: { } st } ? EstimateDecodedSize(st.Length, (a as MimePart)!.ContentTransferEncoding) : 0;
            return new AttachmentItem(string.IsNullOrWhiteSpace(name) ? L.T("Common_Attachment") : name!, size, a);
        }).ToList();

        EmptyText.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        var allowExternal = _externalAllowedFor == id || !AppState.Settings.BlockExternalContent;
        var (html, hasExternal) = MailHtml.Build(msg, allowExternal);
        BlockedBar.Visibility = hasExternal && !allowExternal ? Visibility.Visible : Visibility.Collapsed;

        _webViewReady ??= EnsureWebViewAsync();
        var ready = await _webViewReady;
        if (version != _loadVersion) return;
        if (ready && html.Length < 1_500_000)
        {
            FallbackText.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
            _allowNavigation = true;
            Browser.CoreWebView2.NavigateToString(html);
        }
        else
        {
            Browser.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text = msg.TextBody ?? MessageIndexer.HtmlToText(msg.HtmlBody ?? "");
        }
    }

    static long EstimateDecodedSize(long encoded, ContentEncoding enc) =>
        enc == ContentEncoding.Base64 ? encoded * 3 / 4 : encoded;

    void LoadExternal_Click(object sender, RoutedEventArgs e)
    {
        _externalAllowedFor = MessageId;
        OnMessageChanged();
    }

    static AttachmentItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as AttachmentItem;

    void Attachment_Click(object sender, RoutedEventArgs e) => OpenAttachment(ItemOf(sender));
    void AttachmentOpen_Click(object sender, RoutedEventArgs e) => OpenAttachment(ItemOf(sender));

    static void OpenAttachment(AttachmentItem? item)
    {
        if (item is null) return;
        try { Ui.OpenTempFile(item.FileName, item.GetContent()); }
        catch (Exception ex) { Ui.Error(L.F("Preview_AttachmentOpenFailed", ErrorText.Describe(ex))); }
    }

    void AttachmentSave_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var dlg = new SaveFileDialog { FileName = Exporter.SafeName(item.FileName) };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllBytes(dlg.FileName, item.GetContent());
    }
}
