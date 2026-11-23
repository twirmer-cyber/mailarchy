# OAuth-Anmeldung für Microsoft und Google einrichten

MailVault kann sich bei **Outlook.com, Hotmail, Microsoft 365** und **Gmail / Google Workspace** ohne Kennwort bzw. App-Passwort anmelden.
Die Anmeldung läuft im Standardbrowser des Benutzers; MailVault bekommt nur ein Zugriffstoken für IMAP/POP3, nie das Kennwort des Kontos.

Damit das funktioniert, braucht MailVault bei beiden Anbietern eine **App-Registrierung**. Sie gehört dem Herausgeber (also dir) und wird
einmal angelegt. Die daraus entstehenden Client-IDs werden beim Bauen in die App eingebettet, stehen aber **nicht** im öffentlichen Repository.

> Ohne Registrierung bleibt alles wie bisher: Die Auswahl „Anmeldung“ im Profil zeigt dann nur „Kennwort bzw. App-Passwort“.

---

## 1. Microsoft (Outlook.com, Hotmail, Microsoft 365)

Voraussetzung: ein Microsoft-Entra-Verzeichnis (Mandant). Mit einem reinen Privatkonto (outlook.com) kann man seit 2024 keine
App-Registrierungen mehr ohne Verzeichnis anlegen. Kostenlos geht es mit einem **kostenlosen Azure-Konto** (azure.microsoft.com/free;
Kreditkarte wird nur zur Identitätsprüfung verlangt, die App-Registrierung selbst kostet nichts) oder einem vorhandenen Microsoft-365-Mandanten.

1. <https://entra.microsoft.com> öffnen → **Anwendungen → App-Registrierungen → Neue Registrierung**.
2. Ausfüllen:
   - **Name:** `MailVault`
   - **Unterstützte Kontotypen:** *Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten*
     (das ist die Option „Multitenant + persönliche Konten“; nur so funktionieren Outlook.com **und** Firmenkonten)
   - **Umleitungs-URI:** Plattform **Öffentlicher Client/nativ (mobil und Desktop)**, Wert `http://localhost`
     (genau so, ohne Port; Microsoft akzeptiert dann jeden lokalen Port, den MailVault wählt)
3. **Registrieren**. Auf der Übersichtsseite die **Anwendungs-ID (Client-ID)** kopieren – das ist der einzige Wert, den MailVault braucht.
   Ein Client-Geheimnis wird **nicht** angelegt (Desktop-Apps können keins geheim halten; PKCE ersetzt es).
4. **API-Berechtigungen → Berechtigung hinzufügen → Microsoft Graph → Delegierte Berechtigungen**:
   `IMAP.AccessAsUser.All`, `POP.AccessAsUser.All`, `offline_access`, `openid`, `email`, `profile`.
   Eine Administratorzustimmung ist für Privatkonten nicht nötig. In Firmen kann der IT-Admin verlangen, dass er zustimmt.
5. Optional, aber empfehlenswert: **Branding & Eigenschaften** → Startseite `https://mailvault.zerze.de`,
   Datenschutzerklärung `https://mailvault.zerze.de/#datenschutz`. Ohne **Herausgeberüberprüfung** (benötigt eine kostenlose
   Microsoft-Partner-ID) zeigt der Zustimmungsdialog „nicht überprüft“; die Anmeldung funktioniert trotzdem.

Hinweis für Benutzer: Bei Firmenkonten muss IMAP bzw. POP3 für das Postfach erlaubt sein (Exchange Online, Standard ist „an“).

---

## 2. Google (Gmail, Google Workspace)

1. <https://console.cloud.google.com> → oben **Projekt auswählen → Neues Projekt** → Name `MailVault`.
2. **APIs & Dienste → Bibliothek → „Gmail API“ → Aktivieren** (damit die Gmail-Berechtigung auswählbar ist; MailVault nutzt weiter IMAP).
3. **Google Auth Platform** (früher „OAuth-Zustimmungsbildschirm“):
   - **Branding:** App-Name `MailVault`, Support-E-Mail `mailvault@zerze.de`, Startseite `https://mailvault.zerze.de`,
     Datenschutzerklärung `https://mailvault.zerze.de/#datenschutz`, autorisierte Domain `zerze.de`, Kontakt des Entwicklers.
   - **Zielgruppe:** *Extern*. Zum Ausprobieren im Status **Testen** lassen und unter *Testnutzer* die eigenen Gmail-Adressen eintragen.
   - **Datenzugriff → Bereiche hinzufügen:** `https://mail.google.com/`, `openid`, `.../auth/userinfo.email`.
4. **Clients → Client erstellen** → Anwendungstyp **Desktop-App** → Name `MailVault Desktop` → **Client-ID** und **Clientschlüssel**
   kopieren. (Bei Desktop-Apps ist der Clientschlüssel laut Google nicht geheim; er kommt trotzdem nicht ins öffentliche Repository.)

### Wichtig: Grenzen bei Google

`https://mail.google.com/` ist für IMAP/POP3 die **einzige** mögliche Berechtigung und gilt bei Google als *eingeschränkt* (restricted):

| Status | Folge |
|---|---|
| **Testen** | Nur eingetragene Testnutzer (max. 100). **Refresh-Tokens laufen nach 7 Tagen ab** – danach muss man sich im Profil neu anmelden. Gut zum Ausprobieren, nicht für den Alltag. |
| **In Produktion, nicht überprüft** | Jeder kann sich anmelden, sieht aber den Hinweis „Google hat diese App nicht überprüft“ (über „Erweitert → Weiter zu MailVault“ geht es weiter). Höchstens 100 Benutzer insgesamt. Tokens laufen nicht nach 7 Tagen ab. |
| **In Produktion, überprüft** | Kein Warnhinweis, keine Obergrenze. Verlangt Googles App-Überprüfung **und** eine jährliche Sicherheitsprüfung (CASA) durch ein zugelassenes Labor – kostet Zeit und je nach Labor Geld. |

Empfehlung: zunächst **In Produktion, nicht überprüft** veröffentlichen. Die Alternative mit App-Passwort bleibt für Gmail bestehen.
Für die Überprüfung verlangt Google u. a., dass die Datenschutzerklärung beschreibt, wie Google-Nutzerdaten verwendet werden
(bei MailVault: nur lokal auf dem PC des Benutzers, keine Übertragung an den Herausgeber) – den Abschnitt in `site/index.html` dann ergänzen.

---

## 3. Client-IDs in MailVault eintragen

Datei **`build/oauth-clients.local.json`** anlegen (von Git ignoriert, wird beim Bauen in `MailVault.Core.dll` eingebettet):

```json
{
  "microsoft": { "clientId": "00000000-0000-0000-0000-000000000000" },
  "google":    { "clientId": "1234567890-abc.apps.googleusercontent.com", "clientSecret": "GOCSPX-..." }
}
```

Ein Anbieter ohne Eintrag bleibt ausgeblendet. Zum Ausprobieren ohne Neubau geht auch eine `oauth-clients.json` neben der `MailVault.exe`
oder die Umgebungsvariable `MAILVAULT_OAUTH_CLIENTS` mit dem Pfad zu so einer Datei.

`build\release.ps1` warnt, wenn die Datei fehlt – eine Veröffentlichung ohne sie hätte keine OAuth-Anmeldung.

---

## 4. So funktioniert es technisch

- **Ablauf** (RFC 8252, „OAuth für native Apps“): MailVault öffnet einen nur lokal erreichbaren Port (`127.0.0.1` und `::1`), startet den
  Standardbrowser mit der Anmeldeseite des Anbieters und wartet auf den Rücksprung `http://localhost:<port>/?code=…&state=…`.
  Der Code wird mit **PKCE** (RFC 7636, S256) gegen Tokens getauscht; ein zufälliger `state` verhindert untergeschobene Antworten.
  Zeitlimit 5 Minuten, jederzeit abbrechbar.
- **Speicherung:** Nur das Refresh-Token, verschlüsselt mit Windows-DPAPI (Benutzerkonto) in `oauth-tokens.json` im Datenordner.
  Das Zugriffstoken (ca. 1 Stunde gültig) liegt nur im Arbeitsspeicher. Neue Refresh-Tokens (Microsoft gibt sie bei jeder Erneuerung aus)
  werden sofort gespeichert. Datenordner auf einen anderen PC kopiert → dort neu anmelden (wie bei gespeicherten Kennwörtern).
- **Anmeldung am Server:** SASL `XOAUTH2` (MailKit `SaslMechanismOAuth2`). Die Nur-Lesen-Zusage gilt unverändert: Ordner werden mit
  `EXAMINE` geöffnet, Nachrichten mit `BODY.PEEK` geladen, bei POP3 nie `DELE` (Tests `OAuthTests.Imap_…`/`Pop3_…`).
- **Fehler:** Wird der Zugriff widerrufen oder das Kontokennwort geändert, meldet der Anbieter `invalid_grant`; das Profil zeigt dann
  „Anmeldung abgelaufen – bitte im Profil neu anmelden“, und die Fehler-Benachrichtigung des Zeitplans greift wie bei anderen Fehlern.
- **Code:** `src/MailVault.Core/OAuth/` (`OAuthProviders`, `OAuthClient`, `OAuthService`/`OAuthTokenStore`), Anmeldung in
  `Sources/ServerSources.cs`, Oberfläche im Profileditor. Tests: `tests/MailVault.Tests/OAuthTests.cs` (Schein-Token-Endpunkt,
  echter Loopback-Rücksprung, Schein-IMAP/POP3-Server mit XOAUTH2).

## 5. Echt testen (nach der Registrierung)

1. `build/oauth-clients.local.json` anlegen, `dotnet build`, MailVault in einem eigenen Ordner mit eigenem Datenordner starten
   (siehe `CLAUDE.md`, damit das echte Archiv unberührt bleibt).
2. Verwaltung → E-Mails archivieren → IMAP-Postfach → Adresse eingeben → Anmeldung „Microsoft-Konto“ bzw. „Google-Konto“ → anmelden.
3. „Verbindung testen und Ordner laden“, dann „Speichern und starten“.
4. Gegenprobe: Im Konto den Zugriff entziehen (Microsoft: account.live.com/consent/Manage, Google: myaccount.google.com/permissions)
   → nächster Abruf meldet „Anmeldung abgelaufen“.
