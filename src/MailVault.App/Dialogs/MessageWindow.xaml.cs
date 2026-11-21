using MailVault.App.Localization;
using System.Windows;
using MailVault.App.Services;

namespace MailVault.App.Dialogs;

/// <summary>Zeigt eine einzelne archivierte E-Mail (z. B. einen Beleg aus einer KI-Antwort).</summary>
public partial class MessageWindow : Window
{
    public MessageWindow(long messageId)
    {
        InitializeComponent();
        if (AppState.Archive.GetSummary(messageId) is { } s) Title = string.IsNullOrWhiteSpace(s.Subject) ? L.T("Common_NoSubject") : s.Subject;
        Preview.MessageId = messageId;
    }
}
