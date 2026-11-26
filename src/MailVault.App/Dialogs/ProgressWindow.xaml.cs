using MailVault.App.Localization;
using System.ComponentModel;
using System.Windows;
using MailVault.Core;

namespace MailVault.App.Dialogs;

/// <summary>Fortschrittsdialog mit Protokollausgabe.</summary>
public partial class ProgressWindow : Window
{
    readonly Func<IProgress<ProgressInfo>, CancellationToken, Task<string>> _work;
    readonly CancellationTokenSource _cts = new();
    bool _finished;

    public string? ResultText { get; private set; }
    /// <summary>Bei Erfolg automatisch schließen (z. B. Update: danach geht es ohne Klick weiter).</summary>
    public bool AutoCloseOnSuccess { get; init; }
    public bool Succeeded { get; private set; }
    /// <summary>Zeigt „Im Hintergrund“: Das Fenster lässt sich ausblenden, die Aufgabe läuft weiter.</summary>
    public bool AllowBackground { get; init; }
    /// <summary>Wurde ausgeblendet und ist noch nicht wieder sichtbar.</summary>
    public bool InBackground { get; private set; }
    /// <summary>Nach Ende der Aufgabe (Erfolg, Abbruch oder Fehler).</summary>
    public event Action<ProgressWindow>? Finished;

    public ProgressWindow(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task<string>> work)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _work = work;
        Loaded += async (_, _) =>
        {
            if (AllowBackground) BackgroundButton.Visibility = Visibility.Visible;
            await RunAsync();
        };
    }

    async Task RunAsync()
    {
        var progress = new Progress<ProgressInfo>(p =>
        {
            if (p.Status is not null) StatusText.Text = p.Status;
            if (p.Total is > 0)
            {
                Bar.IsIndeterminate = false;
                Bar.Maximum = p.Total.Value;
                Bar.Value = Math.Min(p.Processed, p.Total.Value);
            }
            if (p.LogLine is not null) AppendLog(p.LogLine);
        });
        AppendLog(L.F("Dialog_Progress_Start", DateTime.Now));
        try
        {
            ResultText = await Task.Run(() => _work(progress, _cts.Token));
            StatusText.Text = ResultText;
            AppendLog(ResultText);
            Succeeded = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Dialog_Progress_Canceled");
            AppendLog(L.T("Dialog_Progress_CanceledLog"));
        }
        catch (Exception ex)
        {
            StatusText.Text = MailVault.Core.ErrorText.Describe(ex);
            AppendLog(L.F("Dialog_Progress_ErrorLog", ex is InvalidOperationException ? ex.Message : ex.ToString()));
        }
        _finished = true;
        Bar.IsIndeterminate = false;
        Bar.Value = Bar.Maximum;
        ActionButton.Content = L.T("Common_Close");
        ActionButton.IsEnabled = true;
        BackgroundButton.Visibility = Visibility.Collapsed;
        Finished?.Invoke(this);
        if (Succeeded && AutoCloseOnSuccess)
        {
            await Task.Delay(800); // Erfolgsmeldung kurz zeigen
            Close();
        }
    }

    void AppendLog(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) { Close(); return; }
        _cts.Cancel();
        ActionButton.IsEnabled = false;
        StatusText.Text = L.T("Dialog_Progress_Canceling");
    }

    void Background_Click(object sender, RoutedEventArgs e)
    {
        InBackground = true;
        Hide();
        Owner?.Activate();
    }

    /// <summary>Holt das ausgeblendete Fenster wieder nach vorn.</summary>
    public void Reveal()
    {
        InBackground = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(LogBox.Text);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            _cts.Cancel();
            e.Cancel = true; // erst schließen, wenn die Aufgabe sauber beendet ist
            StatusText.Text = L.T("Dialog_Progress_Canceling");
        }
        base.OnClosing(e);
    }
}
