using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MailVault.Setup;

/// <summary>
/// Setup-Entpacker: Paket aus der eigenen Ressource in %TEMP% entpacken, "MailVault.exe --install" starten
/// (fragt nach, kopiert nach %LOCALAPPDATA%\Programs\MailVault, legt die Startmenü-Verknüpfung an und startet
/// die installierte Version), warten, Temp-Ordner wieder löschen. Archiv und Einstellungen bleiben unberührt.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Nur zum Prüfen beim Veröffentlichen: Paket entpacken, nichts installieren.
        if (args.Length == 2 && args[0] == "/verify-extract") { Extract(args[1]); return File.Exists(Path.Combine(args[1], "MailVault.exe")) ? 0 : 3; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var temp = Path.Combine(Path.GetTempPath(), "MailArchy-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var form = new PrepareForm();
            Exception? error = null;
            form.Shown += async (_, _) =>
            {
                try { await Task.Run(() => Extract(temp)); }
                catch (Exception ex) { error = ex; }
                form.Close();
            };
            Application.Run(form);
            if (error is not null) throw error;

            var exe = Path.Combine(temp, "MailVault.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException(Msg.Broken);
            using var p = Process.Start(new ProcessStartInfo(exe, "--install") { UseShellExecute = false, WorkingDirectory = temp })!;
            p.WaitForExit();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Msg.Failed, ex.Message), "MailArchy Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void Extract(string target)
    {
        using var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidDataException(Msg.Broken);
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Msg.Broken); // Zip-Slip
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    sealed class PrepareForm : Form
    {
        public PrepareForm()
        {
            Text = "MailArchy Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(380, 96);
            Font = SystemFonts.MessageBoxFont;
            Controls.Add(new Label { Text = Msg.Preparing, AutoSize = false, Left = 20, Top = 18, Width = 340, Height = 22 });
            Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Left = 20, Top = 50, Width = 340, Height = 16, MarqueeAnimationSpeed = 30 });
        }
    }

    /// <summary>Die wenigen Texte des Entpackers in den vier Sprachen der App.</summary>
    static class Msg
    {
        static readonly string Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        static string Pick(string de, string en, string fr, string es) => Lang switch { "de" => de, "fr" => fr, "es" => es, _ => en };
        public static string Preparing => Pick("MailArchy wird vorbereitet …", "Preparing MailArchy …", "Préparation de MailArchy …", "Preparando MailArchy …");
        public static string Broken => Pick("Die Setup-Datei ist unvollständig. Bitte erneut herunterladen.", "The setup file is incomplete. Please download it again.",
            "Le fichier d'installation est incomplet. Veuillez le télécharger à nouveau.", "El archivo de instalación está incompleto. Descárguelo de nuevo.");
        public static string Failed => Pick("Die Installation konnte nicht gestartet werden:\n{0}", "Setup could not be started:\n{0}",
            "L'installation n'a pas pu démarrer :\n{0}", "No se pudo iniciar la instalación:\n{0}");
    }
}
