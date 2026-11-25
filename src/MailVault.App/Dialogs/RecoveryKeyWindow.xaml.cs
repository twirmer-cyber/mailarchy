using MailVault.App.Localization;
using MailVault.App.Services;
using MailVault.Core;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace MailVault.App.Dialogs;

/// <summary>
/// Zeigt einen (neuen) Wiederherstellungsschlüssel und erstellt das Notfallblatt: Schlüssel, Anleitung und eine Kopie
/// der Schlüsseldatei. Mit dem Blatt lässt sich das Archiv auch öffnen, wenn Kennwort und Schlüsseldatei verloren sind.
/// </summary>
public partial class RecoveryKeyWindow : Window
{
    readonly KeyVault.KeyFile? _keyFile;
    readonly string _archiveDir;
    public bool Exported { get; private set; }

    public RecoveryKeyWindow(string key, KeyVault.KeyFile? keyFile, string archiveDir, bool replaced = false)
    {
        InitializeComponent();
        KeyBox.Text = key;
        _keyFile = keyFile;
        _archiveDir = archiveDir;
        if (replaced) HeaderText.Text = L.T("Recovery_ReplacedHeader");
    }

    string SheetText() => _keyFile is null
        ? L.F("Recovery_FileContent", DateTime.Now, KeyBox.Text).Replace("\n", Environment.NewLine)
        : EmergencySheet.Build(L.F("Recovery_SheetText", DateTime.Now, KeyBox.Text, _archiveDir, Environment.MachineName)
            .Replace("\n", Environment.NewLine), _keyFile);

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        Ui.CopySecret(KeyBox.Text);
        StatusText.Text = L.T("Recovery_Copied");
        Exported = true;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = L.T("Recovery_FileName"), Filter = L.T("Common_TextFileFilter") };
        if (dlg.ShowDialog(this) != true) return;
        var full = Path.GetFullPath(dlg.FileName);
        if (full.StartsWith(Path.GetFullPath(_archiveDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
            !Ui.Confirm(L.T("Recovery_SaveNextToArchive")))
            return;
        File.WriteAllText(full, SheetText());
        StatusText.Text = L.F("Recovery_Saved", full);
        Exported = true;
    }

    void Print_Click(object sender, RoutedEventArgs e)
    {
        var pd = new PrintDialog();
        if (pd.ShowDialog() != true) return;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11, PagePadding = new Thickness(56),
            ColumnWidth = double.PositiveInfinity, Background = Brushes.White, Foreground = Brushes.Black,
            PageWidth = pd.PrintableAreaWidth, PageHeight = pd.PrintableAreaHeight,
        };
        doc.Blocks.Add(new Paragraph(new Run(SheetText())));
        pd.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "MailArchy – " + L.T("Common_RecoveryKey"));
        StatusText.Text = L.T("Recovery_Printed");
        Exported = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ConfirmBox.IsChecked != true)
        {
            MessageBox.Show(this, L.T("Recovery_PleaseConfirm"), "MailArchy", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
