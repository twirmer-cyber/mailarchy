# Sicherheit / Security

**Deutsch:** Bitte melden Sie Sicherheitslücken nicht als öffentliches Issue, sondern vertraulich per E-Mail an
**mailvault@zerze.de** – mit Beschreibung, betroffener Version und wenn möglich Schritten zum Nachvollziehen.
Sie erhalten eine Antwort, sobald die Meldung gelesen ist; eine Korrektur wird als signiertes Update verteilt.

**English:** Please do not report security issues as public GitHub issues. Send them privately to **mailvault@zerze.de**
with a description, the affected version and, if possible, steps to reproduce. Fixes ship as signed updates.

Besonders interessiert / of particular interest:
- alles, was ein Postfach verändern könnte (MailVault darf nur lesen) / anything that could modify a mailbox (MailVault must only read),
- Umgehung der Update-Signatur / bypassing the update signature,
- Schwächen im Archivschutz (`src/MailVault.Core/KeyVault.cs`, `MailArchive.cs`) / weaknesses in archive encryption,
- Zugriff über den Webzugriff ohne Berechtigung / unauthorised access through the web access feature.

Das Sicherheitsmodell ist in [docs/ENTWICKLUNG.md](docs/ENTWICKLUNG.md#3-sicherheitsmodell) beschrieben.
