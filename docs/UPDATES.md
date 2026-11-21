# Updates – Ablauf und Regeln für die Entwicklung

## Veröffentlichen
1. Version in `Directory.Build.props` erhöhen (Patch für Fehlerbehebungen, Minor für Funktionen). Vorabversionen heißen `0.6.0-beta.1`.
2. Änderungen für die Benutzer in `build/notes/<version>.json` eintragen, in allen vier Sprachen:
   `{"de":["Suche findet jetzt auch …"],"en":["…"],"fr":["…"],"es":["…"]}`. Fehlt die Datei, nimmt das Skript die deutschen Commit-Betreffzeilen.
3. Committen, Tag `v<version>` setzen (`git tag v0.5.0`), dann:
   `powershell -ExecutionPolicy Bypass -File build\release.ps1 -Deploy`
   (Server-Zugang aus `build\deploy.local.json`, siehe `docs/ENTWICKLUNG.md` → Veröffentlichen; die Datei ist von Git ausgeschlossen)
   - führt die Tests aus (Abbruch bei Fehlern),
   - baut MailVault eigenständig lauffähig (`--self-contained`, win-x64, kein .NET beim Benutzer) als `MailVault-<version>-win-x64.zip` mit `Installieren.cmd`,
   - baut daraus die Setup-Datei `MailVault-Setup-<version>.exe` (`tools\MailVault.Setup`, .NET Framework 4.8, Paket als Ressource) und prüft sie mit `/verify-extract` (entpackt nur, installiert nichts),
   - kopiert die Webseite aus `site\` nach `dist\site` und holt die bisherigen Update-Listen vom Server,
   - trägt die Version mit `tools\MailVault.Release feed` in `updates/beta.json` und (fertige Versionen) `updates/stable.json` ein, legt das Paket unter `download/` ab und **signiert** beide Listen (`.sig`),
   - setzt Version, Größe, Datum, SHA-256, Setup-Link und Repository-Adresse in die Webseite ein,
   - lädt per `pscp` (mit `-PasswordFile`, Host-Schlüssel fest geprüft) oder `scp` (SSH-Schlüssel) hoch. Reihenfolge: zuerst die Pakete, dann die Webseite, zuletzt die Update-Listen (ein Client sieht eine Version erst, wenn ihr Paket schon da ist).
4. Ohne `-Deploy` bleibt alles in `dist\` zum Prüfen. Nur die Webseite neu: `-SiteOnly -Deploy`.

### Signaturschlüssel
- Erzeugt einmalig mit `dotnet run --project tools\MailVault.Release -- keygen`.
- Der private Schlüssel liegt in `%APPDATA%\MailVault-Release\update-signing-key.txt`, **nie im Repository**. Sicher aufbewahren (z. B. Passwortmanager).
- Ohne ihn kann keine installierte MailVault-Version mehr aktualisiert werden.
- Der öffentliche Schlüssel steht in `UpdateFeed.PublicKey`; das Werkzeug verweigert das Signieren mit einem Schlüssel, der nicht dazu passt.
- Einen Schlüssel wechselt man nur mit einer Übergangsversion, die beide Schlüssel akzeptiert.

## So funktioniert das Update beim Benutzer
1. **Installation:**
   - `MailVault-Setup-<version>.exe` von mailvault.zerze.de starten: entpackt das Paket nach `%TEMP%`, ruft `MailVault.exe --install` auf und löscht den Temp-Ordner danach. Alternativ das ZIP entpacken und `Installieren.cmd` starten.
   - Kopiert nach `%LOCALAPPDATA%\Programs\MailVault`, legt `install.json` (Kanal) und eine Startmenü-Verknüpfung an. Ein vorhandener Autostart-Eintrag wird umgestellt.
   - Aus einer nicht installierten Kopie geht das auch unter Verwaltung → Updates → „MailVault installieren“.
2. **Suche** (`UpdateManager.CheckAsync`):
   - `GET https://mailvault.zerze.de/updates/stable.json` (bzw. `beta.json` bei „Auch Vorabversionen“) plus `….json.sig`.
   - Ohne gültige ECDSA-Signatur (P-256, SHA-256) gegen `UpdateFeed.PublicKey` wird nichts angeboten.
   - Nur `https` (unverschlüsselt nur zu diesem PC, für Tests).
   - Verglichen wird die Versionsnummer (SemVer). Angezeigt werden die Änderungen aller übersprungenen Versionen in der Programmsprache (sonst Englisch, sonst Deutsch).
3. **Automatik** (`UpdateService`): 30 s nach dem Start und danach alle 4 Stunden. Mit „automatisch installieren“ wird das Paket sofort geladen und eingespielt, sobald:
   - kein Abruf läuft,
   - kein Dialog offen ist,
   - und das Fenster geschlossen ist (Infobereich) oder 10 Minuten keine Eingabe kam.
   Sonst gibt es nur einen Hinweis im Infobereich.
4. **Herunterladen** (`DownloadAsync`):
   - Paketadresse aus der signierten Liste; relative Adressen gelten zur Liste.
   - Der SHA-256 muss zur Angabe in der signierten Liste passen, sonst wird nichts installiert.
   - Entpackt wird nach `%LOCALAPPDATA%\MailVault\update\staging`.
   - Die Programmversion im Paket muss der angekündigten entsprechen; Benutzerdateien im Paket werden entfernt.
5. **Einspielen** (`WriteApplyScript`):
   - Ein PowerShell-Skript wartet auf das Programmende und sichert die alten Programmdateien nach `%LOCALAPPDATA%\MailVault\backup\…` (die letzten drei bleiben).
   - Es spiegelt die neuen Dateien in den Programmordner und startet MailVault mit `--updated` (im Infobereich mit `--background`).
   - Läuft die neue Version nach 12 Sekunden nicht, wird die alte wiederhergestellt und mit `--update-failed` gestartet.
   - Protokoll: `backup\update.log`.
6. **Nie angefasst werden:** das Archiv, `settings.json`, `install.json`, `portable.txt` und `Data\` im Programmordner (portable Nutzung).
7. **Datenschutz:** Die Abfrage überträgt nur das Übliche eines Webabrufs (IP-Adresse, Browserkennung mit Programmversion), keine Kennung und nichts aus dem Archiv; siehe Datenschutzerklärung auf der Webseite.

Zum Testen ohne Server:
- `MAILVAULT_UPDATE_URL` gibt einen anderen Update-Server an (Basisadresse mit `stable.json`).
- `MAILVAULT_INSTALL_DIR` gibt einen anderen Installationsordner an.
- Die Tests in `UpdateTests` signieren mit einem eigenen Testschlüssel.

## Webseite
- `site/` ist der Onepager für mailvault.zerze.de: `index.html` (Deutsch, funktioniert ohne JavaScript), `i18n.js` (en/fr/es), `main.js` (Sprachwahl, Fallblatt-Animation, Suchvorführung; liest bewusst **nicht** `updates/stable.json`, Versionsangaben kommen nur aus den Platzhaltern) und `style.css`.
- Schriften liegen in `site/schriften`; die Seite lädt nichts von fremden Servern.
- Bildschirmfotos `site/bilder/app-{light,dark}-{de,en,fr,es}.png` entstehen mit erfundenen Demo-Daten, nie mit echten E-Mails.
- Platzhalter `{{VERSION}}`, `{{ZIP}}`, `{{SIZE_MB}}`, `{{SHA256}}`, `{{DATE}}`, `{{SETUP}}`, `{{SETUP_MB}}`, `{{REPO_URL}}` setzt `build\release.ps1` ein.
- Der Name des Betreibers steht nur in Impressum und Datenschutzerklärung (gesetzliches Minimum), sonst nirgends auf der Seite.
- Impressum und Datenschutzerklärung stehen unten auf der Seite; bei neuen Diensten (z. B. Kontaktformular, Statistik) **vorher** die Datenschutzerklärung anpassen.

## Regeln für jede Änderung (Entwicklung)
- **Änderungsliste:** Für jede Veröffentlichung `build/notes/<version>.json` in vier Sprachen pflegen. Commit-Betreffzeilen weiterhin auf Deutsch und verständlich (Ersatz, falls die Datei fehlt).
- **Archivformat (`MailArchive.SchemaVersion`):**
  - Jede Änderung an Tabellen erhöht die Version.
  - Die Migration in `EnsureSchema` muss alte Archive ohne Datenverlust umstellen.
  - Vor der Umstellung sichert MailVault die Archivdatei automatisch (`archive.db.vor-update-formatN.bak`).
  - Eine ältere Programmversion verweigert neuere Archive mit verständlicher Meldung und verändert sie nicht. Das ist wichtig für den Rückfall nach einem fehlgeschlagenen Update.
  - Für jede Migration gibt es einen Test, der ein Archiv im alten Format anlegt und öffnet (Vorbild: `StorageTests.Archive_from_version_1_is_upgraded`).
- **Einstellungen, Profile, install.json (JSON):**
  - Neue Eigenschaften brauchen sinnvolle Standardwerte.
  - Nichts umbenennen oder entfernen, ohne die alten Namen weiter zu lesen. Beispiele: Kanal `main` gilt als `beta`; `RepoUrl` aus der GitHub-Zeit wird gelesen und ignoriert.
  - Alte Dateien müssen weiter laden (Vorbild: `SchedulerTests.Old_settings_file_without_new_sections_loads_with_defaults`, `UpdateTests.Old_install_json_from_github_updates_still_loads`).
- **Neue Dateien, die der Benutzer besitzt,** gehören ins Datenverzeichnis oder nach `%APPDATA%\MailVault`, nie in den Programmordner. Sonst überschreibt sie das nächste Update. Ausnahmen trägt man in `UpdateManager.PreservedNames` ein.
- **Texte:** Neue Oberflächentexte gehören in die Ressourcen (App: `Resources/Strings*.resx`, Kern: `Resources/CoreStrings*.resx`, Web: `wwwroot/i18n.js`), immer in allen vier Sprachen. `LocalizationTests` prüft Vollständigkeit und Platzhalter.
- **Das Paket muss auf einem frischen Rechner laufen:** nur NuGet-Pakete, keine lokalen Pfade; `build\release.ps1` muss ohne Fehler durchlaufen.
- **Die Update-Funktion selbst** nur abwärtskompatibel ändern: Eine installierte ältere Version muss die neue Liste noch lesen und das Paket einspielen können.
  - Feldnamen in `stable.json` nicht umbenennen.
  - Asset-Name `MailVault-<version>-win-x64.zip`.
  - `MailVault.exe` und `MailVault.Core.dll` liegen im Wurzelordner des ZIP.
- **Vor einer Veröffentlichung:** `dotnet test tests\MailVault.Tests` muss grün sein (das Skript prüft es ebenfalls).
- **Nie** Löschen/Verändern auf Postfächern einbauen (siehe PRODUCT.md, Versprechen 1).

## Server (einmalig eingerichtet am 21.11.2026)
- nginx: `build/server/mailvault.zerze.de.nginx.conf` → `/etc/nginx/sites-enabled/mailvault.zerze.de.conf`, Webroot `/var/www/mailvault.zerze.de`.
- Zertifikat: Let's Encrypt per Webroot (`/var/www/mailvault-acme`), Erneuerung über `certbot.timer`.
- Protokolle: `/var/log/nginx/mailvault/`, `build/server/mailvault.logrotate` → `/etc/logrotate.d/mailvault` (7 Tage, wie in der Datenschutzerklärung).
