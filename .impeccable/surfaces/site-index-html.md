---
version: 1
slug: "site-index-html"
primary_target: "site/index.html"
related_targets: []
---

Scope: site/index.html – öffentliche Download-Seite mailvault.zerze.de (Onepager). Modus: Persuade.
Publikum: Privatleute und Familien, die ihre E-Mails dauerhaft und lokal sichern wollen; kommen über Empfehlung oder Suche.
Aufgabe: verstehen, was MailVault ist, Vertrauen fassen (lokal, nur lesend, offener Quelltext, signierte Updates), Setup herunterladen; optional mitentwickeln oder per PayPal unterstützen.
Beleg: echte Oberfläche mit Demo-Daten (gekennzeichnet), Versprechen aus PRODUCT.md, öffentlicher Quelltext ({{REPO_URL}}), Versionsangaben + SHA-256.
Einschränkungen: keine Fremd-Ressourcen, CSP ohne style-Attribute/-Blöcke, keine Cookies, kein Tracking; vier Sprachen (de/en/fr/es); Rechtstexte deutsch maßgeblich; Name/Anschrift des Betreibers nur im Rechtsteil.

## Direction contract
THESIS: Die Seite ist eine Fallblattanzeige: Das Archiv meldet Treffer wie ein Bahnhof seine Abfahrten. Statt Hero-Verlauf mit Funktionskarten klappt die Überschrift Buchstabe für Buchstabe auf, und eine Anzeigetafel blättert Suchtreffer durch.
OWN-WORLD: Tafel-Anthrazit (#15171A, Klappen #22252A mit Mittelfuge) besitzt Kopf, Suche und Schluss; Signalgelb (#F5C400) als einzige Farbe für Hauptaktion, Treffer-Markierung und das Zusagen-Plakat (gelbes Fahrplanplakat, schwarze Schrift, Haarlinien); dazwischen Bahnsteig-Weiß (#F4F4F1)/Dunkel (#101113). Barlow Semi Condensed 600 für Klappen und Überschriften, Barlow 400/600 für Text, Chivo Mono nur für Prüfsumme/Suchsyntax. Radius 3 px, Haarlinien, keine Karten.
STORY: In Sekunden: kostenloses, offenes E-Mail-Archiv für Windows, Setup-Knopf sofort. Glaubt: bleibt auf dem PC, liest Postfächer nur, Quelltext offen und ehrlich mit KI gebaut. Tut: herunterladen, ggf. auf GitHub mitmachen oder unterstützen.
FIRST VIEWPORT: Volle Breite Anzeigetafel: oben Marke, Navigation, Sprach-Klappen; darunter die H1 in großen Klappen (3 Zeilen), darunter Satz + gelber Setup-Knopf mit Dateiname/Größe/System und kleiner ZIP-/Prüfsummen-Link; rechts unten beginnt die Treffertafel und ragt in die nächste Sektion.
FORM: Fallblattanzeiger (Bahnhof), Platz 3 der eigenen Liste; seed:1920ebaad17b828a. Aufwertung aus Drum-Machine (abgelehnt): eine Uhr – zu jedem Zeitpunkt bewegt sich genau eine Zeile.
Signatur-Interaktion: Überschrift klappt beim Laden einmal auf; Treffertafel tippt Beispielanfragen und blättert Treffer Zeile für Zeile um; bei reduzierter Bewegung alles statisch.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
