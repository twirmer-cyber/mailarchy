using MailVault.App.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using MailVault.App.Dialogs;
using MailVault.App.Services;
using MailVault.Core;

namespace MailVault.App;

public partial class App : Application
{
    const string InstanceName = "MailVault.Home.SingleInstance";
    Mutex? _instanceMutex;
    EventWaitHandle? _showSignal;
    BackgroundHost? _background;
    bool _exiting;

    public static new App Current => (App)Application.Current;
    public BackgroundHost? Background => _background;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Sprache zuerst festlegen, damit schon Installations- und Anmeldedialoge passend erscheinen.
        var settings = AppSettings.Load();
        L.Apply(settings.Language);
        // WPF formatiert Bindungen sonst immer mit en-US.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        DispatcherUnhandledException += OnUnhandledException;

        // Erstinstallation aus dem entpackten Paket (Installieren.cmd ruft MailVault.exe --install auf)
        if (e.Args.Contains("--install", StringComparer.OrdinalIgnoreCase) &&
            InstallFromThisCopy(fromRunningApp: false, confirmed: e.Args.Contains("--confirmed"), waitForProcess: WaitArgument(e.Args)))
        {
            Shutdown();
            return;
        }

        // Neustart (z. B. nach Sprachwechsel): warten, bis die alte Instanz beendet ist.
        if (WaitArgument(e.Args) is > 0 and var previous)
        {
            try { System.Diagnostics.Process.GetProcessById(previous).WaitForExit(30_000); } catch (ArgumentException) { /* schon beendet */ }
        }
        // Nur eine Instanz: Ein zweiter Start (z. B. Doppelklick, während MailVault im Infobereich läuft)
        // holt das vorhandene Fenster nach vorn. Zwei Instanzen würden sich um das Archiv streiten.
        var user = Environment.UserName;
        _instanceMutex = new Mutex(true, $@"Local\{InstanceName}.{user}", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}.{user}.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var background = e.Args.Contains(Autostart.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        ThemeManager.Initialize(settings.Theme);
        if (string.IsNullOrWhiteSpace(settings.DataDirectory))
        {
            var firstRun = new FirstRunWindow(AppSettings.DefaultDataDirectory);
            if (firstRun.ShowDialog() != true) { Shutdown(); return; }
            settings.DataDirectory = firstRun.SelectedDirectory;
            settings.Save();
        }

        MailArchive? archive;
        try
        {
            archive = OpenArchive(settings.DataDirectory!);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F("App_OpenFailed", settings.DataDirectory, ex.Message),
                "MailVault", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (archive is null) { Shutdown(); return; }

        AppState.Initialize(settings, archive);
        Ui.CleanupTempFiles(); // entschlüsselte Anhänge aus der letzten Sitzung
        _background = new BackgroundHost(ShowMainWindow, ExitApplication);
        WebHost.StartIfEnabled();

        // Wartet im Hintergrund auf das Signal einer zweiten Instanz.
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "MailVault single instance" }.Start();

        if (!background || !AppState.ShouldStayInTray) ShowMainWindow();

        if (e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
            _background?.ShowInfo(L.T("App_Updated"), L.F("App_UpdatedVersion", MailVault.Core.Updates.BuildInfo.Current.Display));
        else if (e.Args.Contains("--update-failed", StringComparer.OrdinalIgnoreCase))
            MessageBox.Show(L.F("App_UpdateFailed", System.IO.Path.Combine(MailVault.Core.Updates.UpdateManager.BackupRoot, "update.log")),
                            L.T("App_UpdateTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateService.Start();
    }

    /// <summary>
    /// Installiert diese Kopie (z. B. aus dem Download-Ordner) nach %LOCALAPPDATA%\Programs\MailVault, legt eine
    /// Startmenü-Verknüpfung an und startet die installierte Version. Liefert true, wenn sich diese Kopie beenden soll.
    /// </summary>
    /// <param name="fromRunningApp">Aufruf aus Verwaltung → Updates: fragt, startet sich mit --install neu und beendet sich.</param>
    public bool InstallFromThisCopy(bool fromRunningApp, bool confirmed = false, int waitForProcess = 0)
    {
        var source = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var target = MailVault.Core.Updates.UpdateManager.DefaultInstallDir;
        var targetExe = System.IO.Path.Combine(target, "MailVault.exe");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return false; // ist schon die installierte Version

        if (!confirmed)
        {
            var oldCore = System.IO.Path.Combine(target, "MailVault.Core.dll");
            var existing = System.IO.File.Exists(oldCore) ? MailVault.Core.Updates.BuildInfo.FromFile(oldCore).Version : null;
            var current = MailVault.Core.Updates.BuildInfo.Current.Version;
            var question = existing is null
                ? L.F("App_InstallQuestion", current, target)
                : L.F("App_ReplaceQuestion", target, existing, current);
            if (MessageBox.Show(question, L.T("App_InstallTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return !fromRunningApp;
        }
        if (fromRunningApp)
        {
            // Diese Kopie läuft gerade: neu starten lassen, das kopiert dann nach dem Beenden.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,
                $"--install --confirmed --wait {Environment.ProcessId}") { UseShellExecute = false });
            ExitApplication(force: true);
            return true;
        }

        if (waitForProcess > 0)
        {
            try { System.Diagnostics.Process.GetProcessById(waitForProcess).WaitForExit(60_000); } catch (ArgumentException) { /* schon beendet */ }
        }
        // Läuft MailVault noch (z. B. die installierte Version im Infobereich)? Dann lassen sich die Dateien nicht ersetzen.
        if (Mutex.TryOpenExisting($@"Local\{InstanceName}.{Environment.UserName}", out var running))
        {
            running.Dispose();
            MessageBox.Show(L.T("App_AlreadyRunning"),
                L.T("App_InstallTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        try
        {
            MailVault.Core.Updates.Installer.Install(source, target);
            Shortcuts.CreateStartMenu(targetExe);
            // Ein vorhandener Autostart-Eintrag zeigt ab jetzt auf die installierte Version
            var autostart = new Autostart();
            if (autostart.RegisteredCommand is not null) autostart.Set(true, targetExe);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(targetExe) { UseShellExecute = true, WorkingDirectory = target });
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F("App_InstallFailed", ex.Message), L.T("App_InstallTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    static int WaitArgument(string[] args)
    {
        var i = Array.IndexOf(args, "--wait");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) ? pid : 0;
    }

    static MailArchive? OpenArchive(string dir)
    {
        var keyFileMissing = MailArchive.IsKeyFileMissing(dir);
        if (!keyFileMissing && !MailArchive.IsProtectedArchive(dir)) return MailArchive.Open(dir);
        var login = new LoginWindow(dir, keyFileMissing);
        return login.ShowDialog() == true ? login.Archive : null;
    }

    public void ShowMainWindow()
    {
        if (_exiting) return;
        if (MainWindow is not MainWindow main)
        {
            main = new MainWindow();
            MainWindow = main;
            main.Closing += MainWindow_Closing;
        }
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
        main.Topmost = true;   // zuverlässig nach vorn holen ...
        main.Topmost = false;  // ... ohne dauerhaft oben zu bleiben
    }

    void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        if (AppState.ShouldStayInTray)
        {
            e.Cancel = true;
            ((Window)sender!).Hide();
            _background?.ShowStillRunningHint();
            return;
        }
        e.Cancel = true;
        ExitApplication();
    }

    public void ExitApplication() => ExitApplication(force: false);

    public void ExitApplication(bool force)
    {
        if (_exiting) return;
        if (!force && AppState.RunningProfile is { } running &&
            MessageBox.Show(L.F("App_ExitWhileArchiving", running.Name),
                "MailVault", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _exiting = true;
        foreach (Window w in Windows) w.Hide();
        Shutdown();
    }

    /// <summary>Startet MailVault neu (z. B. nach einem Sprachwechsel). Die neue Instanz wartet, bis diese beendet ist.</summary>
    public void Restart()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,
            $"--wait {Environment.ProcessId}") { UseShellExecute = false });
        ExitApplication(force: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        WebHost.Stop();
        _background?.Dispose();
        AppState.Shutdown();
        Ui.CleanupTempFiles();
        _showSignal?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { /* nicht Besitzer */ }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(L.F("App_UnexpectedError", e.Exception.Message), "MailVault",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
