# MailArchy – Hinweise für die Entwicklung

C# .NET 8: `src/MailVault.Core` (Archiv, Quellen, Zeitplan, KI, Updates), `src/MailVault.App` (WPF), `src/MailVault.Web` (Webzugriff), `tests/MailVault.Tests` (xUnit). Oberfläche und Kommentare auf Deutsch.

## Immer beachten
- **Name:** Das Programm heißt seit 0.8.0 **MailArchy** (bis 0.7 MailVault). Technische Namen bleiben bewusst `MailVault` – `MailVault.exe`, `MailVault.Core.dll`, Projekte/Namespaces, `%LOCALAPPDATA%\MailVault`, `%APPDATA%\MailVault`, `Programs\MailVault`, `MAILVAULT_*`, Verschlüsselungs-Entropie, Autostart-Wert –, sonst brechen Updates älterer Installationen oder gespeicherte Kennwörter. Sichtbare Texte immer „MailArchy“. mailvault.zerze.de liefert `/updates/` und `/download/` dauerhaft weiter aus (`build/server/mailarchy.zerze.de.nginx.conf`).
- **Update-Fähigkeit:** Jede Änderung muss über die eingebaute Update-Funktion einspielbar sein – Regeln in `docs/UPDATES.md` (Archivformat-Migrationen mit Test, JSON-Abwärtskompatibilität, Benutzerdateien nie im Programmordner, Veröffentlichung mit `build\release.ps1` auf mailarchy.zerze.de (signierte Update-Liste, Änderungen in build/notes/<version>.json in vier Sprachen), verständliche deutsche Commit-Betreffzeilen = Änderungsliste beim Benutzer).
- **Mehrsprachig:** Oberfläche in Deutsch, Englisch, Französisch, Spanisch; neue Texte immer in alle vier Ressourcen (siehe `docs/UPDATES.md` → Texte).
- **Webseite** `site/` (mailarchy.zerze.de): keine fremden Ressourcen, Rechtstexte aktuell halten, Bildschirmfotos nur mit Demo-Daten.
- **Postfächer nur lesen:** Nichts einbauen, was auf dem Server löscht, verschiebt oder markiert (`ReadOnlySourceTests`, `ImapReadOnlyTests`, `Pop3ReadOnlyTests` sichern das ab).
- **Gestaltung:** nach `DESIGN.md` (ruhiger Archiv-Look, Farbe nur mit Bedeutung, keine Verläufe/Emoji-Symbole). Produktkontext in `PRODUCT.md`.
- **Tests:** `dotnet test tests\MailVault.Tests` vor jedem Commit grün halten.

## Bauen und Starten
- `dotnet build`, `dotnet run --project src\MailVault.App`
- Läuft die App des Benutzers aus `bin\Debug`, sind die DLLs gesperrt: zum Testen nach einen eigenen Ordner bauen (`-o <ordner>`) und dort `portable.txt` + `settings.json` mit eigenem Datenordner anlegen, damit das echte Archiv unberührt bleibt.
