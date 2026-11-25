# MailArchy – Handbuch für die Entwicklung

Alles Wichtige an einem Ort: Aufbau, Sicherheitsmodell, Schlüssel, Veröffentlichung, Server, Notfälle.
Einzelheiten zum Update-Mechanismus stehen in [UPDATES.md](UPDATES.md), Produktziele in [../PRODUCT.md](../PRODUCT.md),
Gestaltung in [../DESIGN.md](../DESIGN.md), Arbeitsregeln für KI-Assistenten in [../CLAUDE.md](../CLAUDE.md).

Stand: 0.6.0 (November 2026).

---

## 1. Auf einen Blick

| | |
|---|---|
| Produkt | Privates E-Mail-Archiv für Windows 10/11 (x64), kostenlos, quelloffen |
| Technik | C# / .NET 8, WPF (MVVM, CommunityToolkit.Mvvm), SQLite3 Multiple Ciphers, MailKit/MimeKit, ASP.NET Core (Webzugriff), WebView2 |
| Sprachen | Deutsch, Englisch, Französisch, Spanisch (App, Webzugriff, Webseite) |
| Webseite / Updates | <https://mailarchy.zerze.de> (statisch, nginx) – Setup, ZIP, signierte Update-Listen |
| Quellcode | öffentliches Git-Repository (Adresse: `$RepoUrl` in `build/release.ps1`) |
| Entstehung | „vibe-coded“: gemeinsam mit Claude (Anthropic) entwickelt; jede Änderung wird getestet und geprüft |

**Drei Versprechen, die nie gebrochen werden dürfen** (Tests sichern sie ab):
1. Postfächer werden nur gelesen – nichts löschen, verschieben, markieren (`ReadOnlySourceTests`, `ImapReadOnlyTests`, `Pop3ReadOnlyTests`).
2. Das Archiv bleibt auf dem PC des Benutzers. Keine Telemetrie, keine Konten. Die KI läuft nur mit dem eigenen Schlüssel des Benutzers und nur auf seine Anfrage.
3. Updates verlieren keine Daten (Formatmigrationen mit Sicherung und Test, siehe UPDATES.md).

---

## 2. Projektaufbau

```
src/MailVault.Core     Archiv, Suche, Quellen, Import/Export, Zeitplan, Archivschutz, KI, Updates – ohne UI, testbar
  KeyVault.cs          Schlüsseldatei, Notfallblatt (EmergencySheet), Ableitung/Verpackung des DB-Schlüssels
  MailArchive.cs       SQLite/SQLCipher-Archiv, Schema + Migration, Ver-/Entschlüsseln, Absturz-Aufräumen
  PasswordStrength.cs  Offline-Einschätzung der Kennwortstärke
  Secret.cs            DPAPI für Postfach-Kennwörter und API-Schlüssel
  Updates/             UpdateFeed (Signaturprüfung), UpdateManager, Installer
src/MailVault.App      WPF-Oberfläche (Views, ViewModels, Dialogs, Themes, Localization)
src/MailVault.Web      Webzugriff im Heimnetz (Kestrel im Programmprozess)
tests/MailVault.Tests  xUnit, ~200 Tests (Kultur fest de-DE per Modul-Initialisierer)
tools/MailVault.Release  keygen / feed / verify – signiert die Update-Listen
tools/MailVault.Setup    Setup-EXE (.NET Framework 4.8, Paket als Ressource)
build/                 release.ps1, Installieren.cmd, notes/<version>.json, server/ (nginx, logrotate)
site/                  Onepager mailarchy.zerze.de (statisch, keine fremden Ressourcen)
docs/                  dieses Handbuch, UPDATES.md
```

Bauen und Testen:

```powershell
dotnet build
dotnet test tests\MailVault.Tests        # vor jedem Commit grün
dotnet run --project src\MailVault.App
```

Läuft die eigene MailArchy-Installation aus `bin\Debug`, sind die DLLs gesperrt: zum Testen mit `-o <ordner>` bauen und dort
`portable.txt` + `settings.json` mit eigenem Datenordner anlegen, damit das echte Archiv unberührt bleibt.

### Wo liegt was beim Benutzer

| Was | Ort |
|---|---|
| Programm (installiert) | `%LOCALAPPDATA%\Programs\MailVault` (keine Adminrechte) |
| Einstellungen | `%APPDATA%\MailVault\settings.json` (portabel: neben der EXE) |
| Archiv | frei wählbar, Standard `Dokumente\MailVault` (portabel: `Data\` neben der EXE) |
| Archivdateien | `archive.db` (+ `-wal`/`-shm`), bei Schutz `archive.keys.json` + `archive.keys.json.backup` |
| Profile, Web-Benutzer | `profiles.json`, `webusers.json` im Datenordner (Kennwörter DPAPI bzw. PBKDF2) |
| OAuth-Anmeldungen | `oauth-tokens.json` im Datenordner (nur Refresh-Tokens, DPAPI) |
| Arbeitskopie beim Archiv-Einbinden | `.einbinden-<id>\` im Datenordner – wird danach bzw. beim nächsten Start gelöscht |
| Update-Zwischenstand, Sicherungen | `%LOCALAPPDATA%\MailVault\update\`, `…\backup\` |
| Geöffnete Anhänge | `%TEMP%\MailVault\<guid>\` – wird beim Start und Beenden geleert |

---

## 3. Sicherheitsmodell

### 3.1 Archivschutz (Verschlüsselung)

```
Kennwort ──PBKDF2-SHA256, 600 000 Runden, Salz 16 B──► KEK₁ ──AES-256-GCM──┐
                                                                            ├─► DB-Schlüssel (256 Bit, zufällig)
Wiederherstellungsschlüssel ──PBKDF2 (gleich)──► KEK₂ ──AES-256-GCM─────────┘          │
                                                                                         ▼
                                     archive.db: SQLCipher-4-kompatibel (AES-256-CBC, HMAC-SHA512 je Seite,
                                     PBKDF2-HMAC-SHA512 256 000 Runden auf den DB-Schlüssel)
```

- **DB-Schlüssel:** 32 Zufallsbytes (`RandomNumberGenerator`), als 64 Hex-Zeichen an `PRAGMA key` übergeben. Er verlässt den Prozess nie.
- **Schlüsseldatei** `archive.keys.json`: enthält nur Salze und die zwei verpackten Kopien (Nonce ‖ Tag ‖ Chiffrat). Allein nutzlos,
  aber **lebenswichtig** – ohne sie öffnet auch das richtige Kennwort das Archiv nicht.
  - wird doppelt geschrieben (`.backup`), jeweils über `.tmp` mit `Flush(true)` → kein halber Zustand nach Stromausfall;
  - beim Laden geprüft (Version, Runden 100 000 … 10 000 000, Base64-Längen); beschädigt → Sicherungskopie;
  - beide unbrauchbar → klare Meldung „Schlüsseldatei beschädigt“ statt „Kennwort falsch“.
- **Wiederherstellungsschlüssel:** 30 Zeichen aus 32 (A–Z ohne I/O, 2–9) = 150 Bit, Gruppen à 5. Wird nur einmal angezeigt, nirgends gespeichert.
  Eingabe tolerant (Groß/Klein, Bindestriche/Leerzeichen egal).
- **Kennwort ändern** verpackt nur den DB-Schlüssel neu (schnell, kein Neuverschlüsseln).
- **Neuer Wiederherstellungsschlüssel** (Verwaltung → Archiv): erfordert das Kennwort, macht den alten sofort ungültig.
- **Notfallblatt** (`EmergencySheet`): Textdatei/Ausdruck mit Wiederherstellungsschlüssel, Anleitung und der Schlüsseldatei als
  Base64-Block (`-----BEGIN MAILVAULT KEYFILE-----`). Damit ist das Archiv auch nach Verlust von Kennwort **und** Schlüsseldatei zu öffnen.
  Beim Start erkennt MailArchy „Archiv verschlüsselt, Schlüsseldatei fehlt“ (`IsKeyFileMissing`) und bietet die Wiederherstellung an;
  `RestoreKeyFile` übernimmt die Datei nur, wenn sie diese Archivdatei tatsächlich öffnet.
- **Ver-/Entschlüsseln absturzsicher** (`ReEncrypt` + `RecoverInterruptedOperation`):
  1. verpackter Schlüssel zuerst als `archive.keys.json.pending` auf die Platte,
  2. Kopie in `archive.db.new` mit neuem Schlüssel, Commit,
  3. `archive.db` → `.bak`, `.new` → `archive.db`, Schlüsseldatei endgültig, `.bak` mit Nullen überschreiben und löschen.
  Beim nächsten Öffnen wird jeder Zwischenzustand erkannt und zu Ende geführt. Tests: `KeySafetyTests`.
- **Kennwortstärke:** `PasswordStrength` schätzt die Entropie (Zeichenvorrat × effektive Länge, Abzug für Folgen/Wiederholungen,
  Liste von Allerweltskennwörtern). Mindestlänge 8; „schwach“ ist erlaubt, aber nur nach ausdrücklicher Rückfrage.
- **Zwischenablage:** Der Wiederherstellungsschlüssel wird mit `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory=0`, `CanUploadToCloudClipboard=0` kopiert und nach 60 s gelöscht.

**Grenzen (ehrlich kommuniziert, auch in der App unter „So schützt MailArchy Ihr Archiv“):**
- Exporte sind unverschlüsselt. Geöffnete Anhänge liegen kurz entschlüsselt in `%TEMP%`.
- Während MailArchy läuft, ist das Archiv entsperrt; der DB-Schlüssel steht im Speicher (.NET-Strings lassen sich nicht sicher löschen).
- Auf SSDs ist das spurlose Löschen der früher unverschlüsselten Datei nicht garantiert → Empfehlung BitLocker.
- Kein automatisches Sperren nach Inaktivität (Idee für später).

### 3.2 Weitere Geheimnisse

| Geheimnis | Schutz |
|---|---|
| Postfach-Kennwörter, SMTP-Kennwort, KI-API-Schlüssel | Windows-DPAPI (`Secret`, `CurrentUser`, Zweck-Entropie `MailArchy.<zweck>.v1`) |
| OAuth-Refresh-Tokens (Microsoft/Google) | DPAPI (Zweck `OAuth`) in `oauth-tokens.json`; Zugriffstokens nur im Speicher. Anmeldung im Browser mit PKCE + state über Loopback – Details in [OAUTH.md](OAUTH.md) |
| OAuth-Client-IDs des Herausgebers | `build/oauth-clients.local.json` (von Git ignoriert), beim Bauen eingebettet – siehe 4.3 |
| Web-Benutzer | PBKDF2-SHA256-Hash mit Salz, Dummy-Hash gegen Zeitmessung, Anmeldebremse je IP/Benutzer |
| Update-Signaturschlüssel | **nur beim Entwickler**, siehe 4.1 |

### 3.3 Webzugriff
Kestrel im Programmprozess, standardmäßig nur `127.0.0.1`; „Heimnetz“ bindet an alle Adressen. Sitzungs-Cookie `HttpOnly`, `SameSite=Strict`,
CSRF-Schutz, Mailanzeige in Sandbox mit CSP, Anfragegröße begrenzt. **Kein TLS** – nur für vertrauenswürdige Netze gedacht.

### 3.4 KI
Nur mit eigenem Anthropic-API-Schlüssel und auf Anfrage. Claude bekommt Archivinhalte ausschließlich über lokale Werkzeuge
(`AiArchiveTools`) und nur so viel, wie das Lesekontingent erlaubt. Nichts wird im Hintergrund gesendet.

### 3.5 Anzeige fremder E-Mails
WebView2 ohne JavaScript, CSP blockiert externe Inhalte (Schalter „externe Inhalte blockieren“), gefährliche Anhangtypen nur nach Rückfrage.

---

## 4. Schlüssel des Entwicklers – sichern!

### 4.1 Update-Signaturschlüssel (der eigentliche „Masterkey“)
- Datei: `%APPDATA%\MailVault-Release\update-signing-key.txt` (ECDSA P-256, PKCS#8, Base64). **Nie ins Repository.**
- Öffentlicher Teil: `UpdateFeed.PublicKey` in `src/MailVault.Core/Updates/UpdateManager.cs`.
- Wer ihn hat, kann allen installierten MailArchys ein Update unterschieben. Wer ihn verliert, kann keine installierte Version mehr aktualisieren.
- **Sichern:** Inhalt in den Passwortmanager (eigener Eintrag „MailArchy Update-Signaturschlüssel“) **und** offline (USB-Stick im Tresor
  oder Ausdruck). Nicht in Cloud-Ordnern im Klartext, nicht per E-Mail.
- **Verlust:** neuen Schlüssel erzeugen (`keygen`), neuen öffentlichen Schlüssel einbauen, Benutzer müssen einmal manuell das Setup
  installieren (auf der Webseite erklären).
- **Kompromittierung (jemand anderes hat ihn):** sofort Updates stoppen (Update-Listen auf dem Server leeren), neuen Schlüssel, Version mit neuem
  öffentlichen Schlüssel als Setup verteilen, auf der Webseite warnen.
- **Planmäßiger Wechsel:** eine Übergangsversion, die beide Schlüssel akzeptiert, dann den alten abschalten.

### 4.2 Server-Zugang
- `build/deploy.local.json` (von Git ausgeschlossen) mit `Target`, `PasswordFile`, `HostKey`. Die Passwortdatei liegt außerhalb des Repositorys.
- Empfehlung: auf SSH-Schlüssel umstellen und Kennwort-Login für root abschalten; dann `PasswordFile` weglassen (`scp` mit Schlüssel).
- Host-Schlüssel wird beim Hochladen fest geprüft – ändert er sich unerwartet, bricht `pscp` ab (gewollt).

### 4.3 Was nie ins öffentliche Repository darf
Signaturschlüssel, Server-IP/Benutzer/Kennwörter, `deploy.local.json`, `oauth-clients.local.json` (OAuth-Client-IDs/-Secret), echte E-Mails, Archive (`*.db`), `settings.json`, `profiles.json`,
`webusers.json`, private E-Mail-Adressen in Commits. `.gitignore` deckt das ab; vor jedem Push trotzdem `git status` und `git diff --cached` ansehen.
Commit-Autor fürs öffentliche Repository: `MailArchy <mailarchy@zerze.de>` (`git config user.email mailarchy@zerze.de` im Repository).

---

## 5. Veröffentlichen

Voraussetzungen: .NET 8 SDK, PuTTY (`pscp`) oder OpenSSH, Signaturschlüssel, `build/deploy.local.json`,
`build/oauth-clients.local.json` (sonst fehlt die Microsoft-/Google-Anmeldung, siehe [OAUTH.md](OAUTH.md)).

1. Version in `Directory.Build.props` erhöhen.
2. `build/notes/<version>.json` in vier Sprachen.
3. Committen, `git tag v<version>`.
4. `powershell -ExecutionPolicy Bypass -File build\release.ps1 -Deploy`
   - Tests → Paket (self-contained ZIP) → Update-Listen holen, eintragen, signieren, prüfen → Setup-EXE bauen und Probe-Entpacken →
     Platzhalter in der Webseite → Upload (Pakete, Seite, zuletzt Update-Listen).
   - Ohne `-Deploy` bleibt alles in `dist\` zum Ansehen. Nur Webseite: `-SiteOnly -Deploy`.
5. Push ins öffentliche Repository (`git push`, `git push --tags`).
6. Kontrolle: `https://mailarchy.zerze.de` (Version, Download), `…/updates/stable.json`; in einer installierten älteren Version „Nach Updates suchen“.

**SmartScreen:** Setup und ZIP sind nicht code-signiert; Windows warnt beim ersten Start („Weitere Informationen → Trotzdem ausführen“).
Abhilfe wäre ein Code-Signing-Zertifikat (OV/EV) – kostenpflichtig, bewusst noch nicht gemacht.

---

## 6. Server mailarchy.zerze.de

| | |
|---|---|
| Hoster | netcup (Debian 13), gleicher Server wie andere zerze-Seiten |
| nginx | `build/server/mailarchy.zerze.de.nginx.conf` → `/etc/nginx/sites-enabled/mailarchy.zerze.de.conf` |
| Webroot | `/var/www/mailarchy.zerze.de` (`download/`, `updates/`, Seite) |
| TLS | Let's Encrypt per Webroot `/var/www/mailvault-acme`, Erneuerung `certbot.timer` |
| Protokolle | `/var/log/nginx/mailvault/`, `/etc/logrotate.d/mailvault`: täglich, **7 Tage** (so steht es in der Datenschutzerklärung) |
| Kopfzeilen | HSTS, CSP (`default-src 'self'`, keine Inline-Styles), nosniff, Referrer-Policy, Permissions-Policy |

Prüfen nach Änderungen: `nginx -t && systemctl reload nginx`, `curl -I https://mailarchy.zerze.de/`, `logrotate -d /etc/logrotate.d/mailvault`.

---

## 7. Webseite
- `site/index.html` (Deutsch, funktioniert ohne JavaScript), `i18n.js` (en/fr/es), `main.js` (Fallblatt-Animation, Suchvorführung,
  Sprachwahl; respektiert `prefers-reduced-motion`), `style.css`, Schriften Barlow / Chivo Mono (OFL) lokal.
- Keine fremden Ressourcen, keine Cookies, kein Tracking. CSP verbietet `style=`-Attribute und `<style>`-Blöcke.
- Name/Anschrift des Betreibers **nur** in Impressum und Datenschutzerklärung (gesetzliches Minimum nach § 5 DDG / DSGVO).
- Bildschirmfotos nur mit erfundenen Demo-Daten. Gestaltungsvertrag: `.impeccable/surfaces/site-index-html.md`.
- Neue Dienste (Kontaktformular, Statistik …) → **vorher** Datenschutzerklärung anpassen.

---

## 8. Mitentwickeln (öffentliches Repository)
- Issues für Fehler und Ideen, Pull Requests willkommen. Jede Änderung: Tests grün, Texte in vier Sprachen, Read-only-Tests unangetastet.
- Sicherheitslücken bitte nicht als öffentliches Issue, sondern an mailarchy@zerze.de (siehe `SECURITY.md`).
- Commit-Betreffzeilen auf Deutsch und verständlich – sie werden zur Änderungsliste beim Benutzer, falls `build/notes` fehlt.

---

## 9. Notfall-Spickzettel

| Situation | Vorgehen |
|---|---|
| Benutzer hat Kennwort vergessen | Anmeldung → „Kennwort vergessen?“ → Wiederherstellungsschlüssel → neues Kennwort |
| Schlüsseldatei gelöscht | MailArchy erkennt es beim Start → „Notfallblatt wählen…“ → Schlüssel → neues Kennwort. Oder `archive.keys.json.backup` umbenennen |
| Kennwort **und** Wiederherstellungsschlüssel weg | Keine Rettung möglich – so gewollt. Es gibt keine Hintertür |
| Update kaputt beim Benutzer | Automatischer Rückfall nach 12 s; Protokoll `%LOCALAPPDATA%\MailVault\backup\update.log` |
| Fehlerhafte Version veröffentlicht | Neue Patch-Version veröffentlichen (Listen werden nie rückwärts geändert, Clients springen nur vorwärts) |
| Signaturschlüssel verloren/geleakt | Abschnitt 4.1 |
| Zertifikat abgelaufen | `certbot renew --dry-run`, ACME-Location in der nginx-Konfiguration prüfen |
| Server neu aufgesetzt | Abschnitt 6 nachbauen, dann `release.ps1 -Deploy` (lädt die Listen vom alten Stand – vorher aus `dist\site\updates` sichern!) |
