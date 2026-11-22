---
version: 1
slug: "site-index-html"
primary_target: "site/index.html"
related_targets: []
---

Scope: site/index.html – öffentliche Download-Seite mailvault.zerze.de (Onepager). Modus: Persuade.
Publikum: Privatleute und Familien, die ihre E-Mails dauerhaft und lokal sichern wollen; kommen über Empfehlung oder Suche. Viele wissen nicht, dass ihre E-Mails nur beim Anbieter liegen.
Aufgabe: verstehen, was MailVault ist und warum ein Postfach kein Archiv ist, Vertrauen fassen (lokal, nur lesend, offener Quelltext, signierte Updates), Setup herunterladen; optional mitentwickeln oder per PayPal unterstützen.
Beleg: echte Oberfläche mit Demo-Daten (gekennzeichnet), Versprechen aus PRODUCT.md, öffentlicher Quelltext ({{REPO_URL}}), Versionsangaben + SHA-256.
Einschränkungen: keine Fremd-Ressourcen, CSP ohne style-Attribute/-Blöcke, keine Cookies, kein Tracking; vier Sprachen (de/en/fr/es); Rechtstexte deutsch maßgeblich; Name/Anschrift des Betreibers nur im Rechtsteil; keine Erwähnung anderer Archivprodukte.
Ton (Wunsch des Betreibers): sachlich, nutzenorientiert, erklärend; keine verspielten Überschriften, keine Angstmache, keine erfundenen Zahlen.

## Direction contract
THESIS: Ein helles, klares Büro-Dokument statt Werbeseite: Die Seite erklärt nüchtern, warum ein Postfach kein Archiv ist, und zeigt das Archiv bei der Arbeit (Suchvorführung, echte Oberfläche). Keine dunklen Bänder, keine Farbflächen, kein Kachelraster.
OWN-WORLD: Weißes Papier (#FFFFFF) und hellgraue Ablage (#F4F5F7) im Wechsel, Haarlinien #DDE0E5/#C3C8D0, Tinte #1A1F26, Tintenblau #1F4E8C als einzige Aktionsfarbe (Download-Knopf, Links, Fokus, Sprachwahl), Textmarker-Gelb nur für Suchtreffer. Barlow 400/600 für Text und Überschriften; H1 groß und eng gesetzt (-0.035em), zweite Zeile tintenblau, Buchstaben rollen beim Laden kurz durch (keine Kacheln), Chivo Mono nur für Suchsyntax/Prüfsumme. Knöpfe einheitlich 52 px hoch, Radius 10 px (Haupt = Tintenblau gefüllt, Neben = weiß mit Rand), nebeneinander in einer Knopfreihe mit genau einer Infozeile darunter; Karten Radius 12 px, nur als Tabellen-/Fensterrahmen.
STORY: Sofort: kostenloses E-Mail-Archiv für Windows, Setup-Knopf. Versteht: Postfächer verlieren Mails (Fehlgriff, Synchronisierung, Anbieter, Hack) – MailVault hält eine unabhängige Kopie und liest nur. Glaubt: offener Quelltext, ehrlich mit KI gebaut. Tut: herunterladen, ggf. mitmachen oder unterstützen.
FIRST VIEWPORT: Weißer Kopf mit Haarlinie; H1 „Kostenloses E-Mail-Archiv für Windows“ auf hellen Klappen; darunter Lead + tintenblauer Setup-Knopf (Dateiname, Größe) mit System-/Versionszeile und kleinem ZIP-/Prüfsummen-Link; rechts ein helles Programmfenster mit Suchvorführung, das in den grauen Abschnitt „Warum ein Archiv?“ hineinragt.
FORM: Fallblattanzeiger, auf Wunsch des Betreibers zum hellen Büro-Look verfeinert („heller, mehr Büro, klarer, nicht so viel Gelb“); seed:1920ebaad17b828a. Eine Uhr – zu jedem Zeitpunkt bewegt sich genau eine Zeile.
Signatur-Interaktion: H1 klappt beim Laden kurz auf; Suchvorführung tippt Beispielanfragen und blättert Treffer Zeile für Zeile um (Pause-Knopf); bei reduzierter Bewegung statisch.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
