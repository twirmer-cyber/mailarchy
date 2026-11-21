# MailVault Home

Ein privates E-Mail-Archiv für Windows (.NET 8, WPF): E-Mails aus Postfächern und Dateien in ein lokales,
durchsuchbares Archiv übernehmen. Download und Informationen: <https://mailvault.zerze.de>.

## Funktionen

| Funktion | Umsetzung |
|---|---|
| E-Mail-Server via IMAP/POP3 | `ImapSource` / `Pop3Source` (MailKit). Duplikatprüfung schon über die Kopfzeilen, ohne Download |
| Automatische Einrichtung | `AutoConfig`: Anbieter-Konfiguration, Mozilla-Anbieterdatenbank (nur die Domain wird übertragen), MX- und SRV-Einträge, Verbindungstest. Hinweise auf App-Passwörter bzw. OAuth-Pflicht |
| Mozilla Thunderbird | `ThunderbirdSource`: findet die Profile, liest MBOX-Ordner und `.sbd`-Unterordner, überspringt gelöschte Nachrichten |
| Microsoft Outlook (PST/OST) | `OutlookPstSource` (XstReader.Api, MS-PL): liest .pst/.ost direkt, ohne Outlook. Übernimmt die originalen Internet-Kopfzeilen (Duplikate mit IMAP-Kopien werden erkannt), sonst Kopfzeilen aus den MAPI-Eigenschaften. HTML/Text/RTF, Anhänge, eingebettete Bilder, angehängte E-Mails, S/MIME. Kontakte, Termine, Aufgaben und Notizen werden übersprungen |
| E-Mail-Dateien (.eml), MBOX | `EmlFolderSource`, `MboxSource` |
| Archivierungsprofile | `ProfileStore` (`profiles.json`), Kennwörter per Windows-DPAPI geschützt |
| Ordner ein-/ausschließen, Papierkorb/Junk/Entwürfe überspringen, „nur neuer als“ | `ImportJob.FolderFilter`, Datumsfilter |
| Postfach nur lesen | Es gibt keine Lösch-, Verschiebe- oder Markierfunktion. IMAP-Ordner werden schreibgeschützt geöffnet, der Gelesen-Status bleibt unverändert, POP3 sendet nie DELE. Ein Test verhindert, dass sich das ändert |
| Volltextsuche inkl. Anhänge, erweiterte Suche | SQLite FTS5 ohne zweite Textkopie (Wortanfänge, Umlaute egal, „Phrase“ exakt, ODER, -Ausschluss): Betreff, Von/An, Text, Anhangnamen, Anhanginhalte (TXT/HTML/PDF/DOCX/XLSX/PPTX/ODF), Datum, Größe, Priorität, Ordner |
| Kompakte Speicherung | Anhänge und Bilder werden nur einmal gespeichert (SHA-256, byte-genaue Wiederherstellung). Bereits komprimierte Dateien bleiben roh, alles andere zstd. Nachrichtentexte mit einem je Archiv gelernten Wörterbuch |
| Archivordnerbaum | Quelle → Postfachordner, mit Umbenennen, Löschen, Exportieren |
| E-Mails exportieren | EML-Dateien oder MBOX (mboxrd), wahlweise mit Ordnerstruktur |
| Archivschutz mit Wiederherstellungsschlüssel | AES-256 für die gesamte Datenbank (SQLite3 Multiple Ciphers im SQLCipher-4-Modus). DB-Schlüssel mit PBKDF2-SHA256 (600k) und AES-GCM verpackt, einmal per Kennwort, einmal per Wiederherstellungsschlüssel |
| Sichere Anzeige | WebView2 ohne JavaScript, CSP blockiert externe Inhalte, `cid:`-Bilder werden eingebettet |
| Portable Version | Liegt `portable.txt` neben der EXE, werden Daten und Einstellungen dort gespeichert |
| Automatischer Abruf | Zeitplan pro Profil (alle n Minuten / täglich um …), `ArchiveScheduler`: verpasste Termine werden nachgeholt, immer nur ein Lauf gleichzeitig |
| Autostart / Infobereich | Eintrag unter HKCU…Run mit `--background`; Symbol im Infobereich (Öffnen, Jetzt abrufen, Beenden), Schließen = weiterlaufen, nur eine Instanz |
| Benachrichtigung bei Problemen | E-Mail über SMTP (Postausgangsserver per AutoConfig), erst ab n Fehlschlägen in Folge, Erinnerung höchstens alle n Stunden, Entwarnung bei Erfolg. Verständliche Fehlertexte (`ErrorText`) |
| Webzugriff im Heimnetz | `MailVault.Web` (ASP.NET Core/Kestrel im Programm): Browser-Oberfläche für Handy/Tablet/PC, Benutzer mit Freigabe einzelner Postfächer, PBKDF2-Kennwörter, Sitzungs-Cookies, Anmeldebremse, CSRF-Schutz, Mail-Anzeige in Sandbox mit CSP |
| KI-Assistent (Claude) | `AiAssistant` + `AiArchiveTools`: Claude durchsucht das Archiv über lokale Werkzeuge (suchen, lesen, Kontaktstatistik, Verlauf, Zusammenhänge) und belegt Antworten mit anklickbaren E-Mails. Über 30 Beispiel-Fragen (`AiPromptLibrary`). Eigener API-Schlüssel (DPAPI-verschlüsselt), Modellwahl, Lesekontingent |
| Wer kennt wen | `ContactNetwork`: Kontaktnetz aus Von/An/Cc, lokal berechnet, interaktive Grafik; optional Deutung der Gruppen durch Claude (nur Namen, Adressen, Zahlen) |

Nicht im MVP enthalten: Microsoft 365/Exchange (online), Gmail-OAuth, Export nach Outlook und IMAP-Export.

## Installieren und aktualisieren

Auf dem PC wird nichts weiter benötigt (kein .NET, kein Git, keine Administratorrechte).

1. Auf <https://mailvault.zerze.de> **`MailVault-Setup-x.y.z.exe`** herunterladen und starten. Windows SmartScreen warnt beim ersten Mal, weil die Datei nicht code-signiert ist: „Weitere Informationen → Trotzdem ausführen“.
2. MailVault wird nach `%LOCALAPPDATA%ProgramsMailVault` installiert, bekommt einen Startmenü-Eintrag und startet. Alternativ gibt es das ZIP zum Entpacken (mit `Installieren.cmd` oder portabel mit `portable.txt`).

**Updates kommen automatisch von mailvault.zerze.de:** MailVault lädt regelmäßig die signierte Update-Liste (ECDSA P-256). Gibt es eine neuere Version, wird sie heruntergeladen, Signatur und Prüfsumme werden kontrolliert, und sie wird installiert, sobald MailVault gerade nicht benutzt wird. Archiv und Einstellungen bleiben unverändert; startet die neue Version nicht, wird die alte automatisch wiederhergestellt.

## Offen gebaut

MailVault ist „vibe-coded“: gemeinsam mit Claude (Anthropic) entwickelt. Der Quellcode ist öffentlich, damit jeder nachprüfen kann, dass Postfächer nur gelesen werden und nichts das Gerät verlässt. Fehler und Ideen gern als Issue, Verbesserungen als Pull Request. Sicherheitslücken bitte vertraulich melden (siehe [SECURITY.md](SECURITY.md)).

Alles für Entwickler – Aufbau, Sicherheitsmodell, Schlüssel, Veröffentlichung, Server: **[docs/ENTWICKLUNG.md](docs/ENTWICKLUNG.md)**. Update-Regeln: [docs/UPDATES.md](docs/UPDATES.md).

## Bauen

Voraussetzung ist das .NET 8 SDK.

```powershell
dotnet test  tests/MailVault.Tests
dotnet run   --project src/MailVault.App
dotnet publish src/MailVault.App -c Release -r win-x64 --self-contained -o publish
```

## Aufbau

```
src/MailVault.Core   Archiv (MailArchive), Suche, Quellen, Import, Export, Archivschutz – ohne UI, testbar
src/MailVault.App    WPF-Oberfläche (MVVM mit CommunityToolkit.Mvvm)
tests/MailVault.Tests xUnit-Tests für Core
```

Im Archivordner liegen `archive.db` (SQLite, bei Archivschutz verschlüsselt), bei Archivschutz zusätzlich `archive.keys.json`,
und `profiles.json`. Zur Datensicherung kopiert man den gesamten Ordner bei geschlossener Anwendung.

## Tests

`dotnet test tests/MailVault.Tests` – über 200 Tests, u. a.:

- **Nur lesen:** Test-IMAP- und Test-POP3-Server im Prozess protokollieren jeden Befehl. Geprüft wird, dass MailVault nur EXAMINE, `UID SEARCH`, `BODY.PEEK`, LIST, TOP und RETR sendet. Nie gesendet werden SELECT, STORE, EXPUNGE, COPY, MOVE oder DELE. Flags und Inhalt des Postfachs müssen nach dem Lauf bytegleich sein. Kontrolltests zeigen, dass die Test-Server eine Veränderung tatsächlich bemerken würden.
- **Zeitplan und Benachrichtigung:** simulierte Uhr, Sommer-/Winterzeit, Schwelle, Erinnerung, Entwarnung, Versandfehler. Dazu ein Ende-zu-Ende-Test: falsches Kennwort → Benachrichtigung über den Test-SMTP-Server → Kennwort korrigiert → Entwarnung.
- **Archivschutz:** Schlüsseldatei doppelt und prüfbar, Notfallblatt, neuer Wiederherstellungsschlüssel, Aufräumen nach einem Absturz mitten im Ver- oder Entschlüsseln (`KeySafetyTests`).
- **Webzugriff:** Rechte pro Postfach, kein Zugriff auf fremde Nachrichten über die ID, Anmeldebremse, CSRF, CSP.
- **KI-Werkzeuge:** Ordnerbeschränkung, Lesekontingent, Zusammenhänge, Kontaktnetz. Ein Live-Test läuft nur mit `ANTHROPIC_API_KEY` und `MAILVAULT_LIVE_AI=1`.

## Speicherbedarf

Gemessen mit `StorageBenchmark` (`MAILVAULT_BENCH_CORPUS` = Ordner mit .mbox-Dateien): 511 echte Mailinglisten-Mails
(Apache httpd/tomcat, 5,2 MB) plus ein reproduzierbarer Büro-Mix (260 Mails, 23,4 MB) mit Signaturlogos, PDF-Antwortketten und
mehrfach weitergeleiteter Excel-Datei.

| Stand | Archivdatei | davon nur Mailinglisten |
|---|---|---|
| Vorher (Brotli „schnell“, Textkopie im Index) | 135 % der Rohdaten | – |
| Jetzt (Single-Instance, zstd + Wörterbuch, Index ohne Textkopie) | 12,8 % | 55 % (davon 13 Punkte Suchindex) |

Reine Textmails lassen sich weniger stark verkleinern, weil Kopfzeilen viele nicht komprimierbare Werte enthalten (DKIM-Signaturen,
Message-IDs). Die großen Einsparungen kommen von doppelten Anhängen und Bildern.
