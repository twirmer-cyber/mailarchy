using MailVault.App.Localization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using MailVault.Core;
using Forms = System.Windows.Forms;

namespace MailVault.App.Services;

/// <summary>
/// Hintergrundbetrieb: prüft regelmäßig, ob Profile fällig sind, und zeigt ein Symbol im Infobereich
/// (Öffnen, Jetzt abrufen, Beenden). Fehler werden zusätzlich als Windows-Hinweis angezeigt.
/// </summary>
public sealed class BackgroundHost : IDisposable
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource _cts = new();
    readonly Forms.NotifyIcon _tray;
    readonly Action _showWindow;
    readonly Action _exit;
    readonly Forms.ToolStripMenuItem _statusItem;
    readonly Forms.ToolStripMenuItem _runAllItem;
    Task _current = Task.CompletedTask;
    bool _hintShown;

    public BackgroundHost(Action showWindow, Action exit)
    {
        _showWindow = showWindow;
        _exit = exit;

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _runAllItem = new Forms.ToolStripMenuItem(L.T("Tray_RunAll"), null, (_, _) => RunAllNow());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(L.T("Tray_Open"), null, (_, _) => _showWindow()) { Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_runAllItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem(L.T("Tray_Exit"), null, (_, _) => _exit()));
        menu.Opening += (_, _) => UpdateStatus();

        _tray = new Forms.NotifyIcon { Icon = CreateIcon(), ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _showWindow(); };
        _tray.BalloonTipClicked += (_, _) => _showWindow();

        AppState.RunStateChanged += UpdateStatus;
        AppState.Scheduler.RunFinished += OnRunFinished;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UpdateStatus();
        // Kurz nach dem Start prüfen (verpasste Termine nachholen), ohne den Programmstart zu bremsen.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Tick);
    }

    void Tick()
    {
        if (!_current.IsCompleted || AppState.Scheduler.IsRunning || _cts.IsCancellationRequested) return;
        if (AppState.Scheduler.DueProfiles().Count == 0) return;
        _current = Task.Run(() => AppState.Scheduler.RunDueAsync(_cts.Token));
    }

    void RunAllNow()
    {
        if (!AppState.HasScheduledProfiles)
        {
            _tray.ShowBalloonTip(5000, "MailVault", L.T("Tray_NoScheduled"), Forms.ToolTipIcon.Info);
            return;
        }
        if (!_current.IsCompleted) return;
        _current = Task.Run(() => AppState.Scheduler.RunAllScheduledAsync(_cts.Token));
    }

    void OnRunFinished(ProfileRunOutcome o)
    {
        if (o.Error is null) return;
        AppState.OnUi(() => _tray.ShowBalloonTip(10000, L.F("Tray_RunFailed", o.Profile.Name), o.Error, Forms.ToolTipIcon.Warning));
    }

    public void ShowInfo(string title, string text) => _tray.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Info);

    /// <summary>Einmaliger Hinweis, wenn das Fenster in den Infobereich geschlossen wird.</summary>
    public void ShowStillRunningHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _tray.ShowBalloonTip(5000, L.T("Tray_StillRunning"), L.T("Tray_StillRunningText"), Forms.ToolTipIcon.Info);
    }

    public string StatusText
    {
        get
        {
            if (AppState.RunningProfile is { } running) return L.F("Tray_Archiving", running.Name);
            var profiles = AppState.Profiles.All.Where(p => p.Schedule != ScheduleKind.Manual).ToList();
            if (profiles.Count == 0) return L.T("Tray_NoAutomatic");
            var failing = profiles.Count(p => p.LastError is not null);
            var next = profiles.Select(p => AppState.Scheduler.NextRunUtc(p)).Where(n => n is not null).Min();
            var text = next is { } n ? L.F("Tray_Next", n <= DateTime.UtcNow ? L.T("Tray_Now") : L.F("Tray_At", n.ToLocalTime())) : "";
            if (failing > 0) text = L.F("Tray_Failing", failing) + " · " + text;
            return text;
        }
    }

    void UpdateStatus()
    {
        var status = StatusText;
        _statusItem.Text = status;
        _runAllItem.Enabled = AppState.RunningProfile is null;
        var tip = "MailVault Home – " + status;
        _tray.Text = tip.Length > 127 ? tip[..127] : tip; // Windows begrenzt den Tooltip auf 127 Zeichen
    }

    /// <summary>Beim Beenden: laufenden Abruf abbrechen und kurz warten, damit das Archiv sauber geschlossen wird.</summary>
    public void Stop(TimeSpan wait)
    {
        _timer.Stop();
        _cts.Cancel();
        try { _current.Wait(wait); } catch { /* Abbruch */ }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(10));
        AppState.RunStateChanged -= UpdateStatus;
        AppState.Scheduler.RunFinished -= OnRunFinished;
        _tray.Visible = false;
        _tray.Dispose();
    }

    /// <summary>Programmsymbol: Tresor-Umschlag in der Akzentfarbe (ohne externe Bilddatei).</summary>
    public static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new SolidBrush(System.Drawing.Color.FromArgb(0x1F, 0x6F, 0xC5));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90); path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90); path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var pen = new Pen(System.Drawing.Color.White, 2.2f) { LineJoin = LineJoin.Round };
            g.DrawRectangle(pen, 6, 9, 20, 14);
            g.DrawLines(pen, new[] { new PointF(6, 9), new PointF(16, 17), new PointF(26, 9) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
