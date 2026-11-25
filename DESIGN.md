# MailArchy – Gestaltung

Leitbild: **ein gut geführtes Archiv.** Ruhig, ordentlich, papiernah. Die Oberfläche tritt hinter die E-Mails zurück; Farbe bedeutet immer etwas (Aktion, Auswahl, Zustand) und ist nie Dekoration. Kein „KI-Look“: keine Verläufe, kein Glas, keine leuchtenden Akzente, keine Emoji als Symbole, keine farbigen Seitenstreifen, keine Dachzeilen über Überschriften.

## Farben (Tokens)

| Token | Hell | Dunkel | Verwendung |
|---|---|---|---|
| `paper` | `#FAF9F7` | `#1B1C1E` | Inhaltsfläche |
| `shelf` | `#F1F0EC` | `#232427` | Seitenleiste, Werkzeugleisten (zweite neutrale Ebene) |
| `surface` | `#FFFFFF` | `#26272A` | Eingabefelder, Lesebereich, Listen |
| `line` | `#E2E0DA` | `#36383C` | Trennlinien, Rahmen (1 px) |
| `ink` | `#1F2328` | `#E8E6E2` | Text |
| `ink-2` | `#5C626C` | `#A4A8AF` | Sekundärtext (Kontrast ≥ 4,5:1 auf paper) |
| `accent` | `#2C5B8E` | `#86AEDB` | Primäraktion, Auswahl, Fokus, Links |
| `accent-hover` | `#234B76` | `#A3C3E6` | |
| `accent-soft` | `#E7EDF4` | `#2A3644` | Auswahlhintergrund |
| `ok` | `#2F6B3A` | `#7FBF8A` | Erfolg |
| `warn` | `#8A5A00` auf `#FBF4E4` | `#E0B35A` auf `#3A3122` | Hinweise, die Handeln erfordern |
| `danger` | `#A8322D` | `#F08A84` | Fehler, Löschen |

Sekundärtext auf farbigen Flächen wird aus deren Farbton abgeleitet, nie grau.

## Schrift
- Desktop: Segoe UI Variable (Fallback Segoe UI). Web: `system-ui, "Segoe UI", Roboto, sans-serif`.
- Eine Familie. Größen 12 / 13 / 14 (Text) / 16 / 20 / 24 (Seitentitel). Gewichte 400 und 600; 700 nicht verwenden.
- Datum, Größe, Anzahl: Ziffern mit fester Breite (`tabular-nums`).
- Überschriften ohne Dachzeile; mehr Abstand über als unter einer Überschrift.

## Form
- Radius 4 px (Bedienelemente), 6 px (Flächen). Keine Pillenformen außer Filter-Chips.
- Schatten nur für schwebende Elemente (Menüs, Popover): `0 4px 12px rgba(0,0,0,.08)`. Flächen werden durch Linien und die zweite neutrale Ebene getrennt, nicht durch Schatten.
- Karten sparsam: eine Fläche pro Thema, nie Karten in Karten, keine Kachelraster aus Symbol + Überschrift + Text als Seitenstruktur.

## Symbole
Desktop: Segoe Fluent Icons / MDL2 Assets, eine Strichstärke. Web: eigene Inline-SVGs, 1,5 px Strich, 16 px. Keine Unicode-Zeichen oder Emoji als Symbole (✉, ★, ✓ …).

## Bewegung
Nur zur Zustandsanzeige, 150–200 ms, ease-out. Keine Lade-Choreografie, keine pulsierenden Punkte.

## Muster
- **Nachrichtenliste:** zweizeilig in schmalen Ansichten (Absendername fett, Datum rechts; Betreff darunter), Tabelle in breiten Ansichten. Absender als Name, Adresse im Tooltip.
- **Suche:** ein Suchfeld mit Syntax (`von:`, `an:`, `betreff:`, `anhang:ja`, `jahr:`, `nach:`, `vor:`), darunter Filter-Chips (Zeitraum, Anhang, Sortierung). Ergebnisse erscheinen beim Tippen (verzögert).
- **KI-Antworten:** sehen aus wie ein Dokument, nicht wie ein Chat-Spielzeug. Belege als hochgestellte Ziffern [1], darunter „Quellen“ mit Datum · Absender · Betreff. Statuszeile in Klartext („Durchsucht das Archiv: rechnung telekom“).
- **Leere Zustände** erklären den nächsten Schritt.
- **Hinweise** (warn) nur, wenn der Nutzer etwas tun muss; sonst ruhiger Sekundärtext.

## Umsetzung Desktop (WPF)
- **Farben:** `Themes/Light.xaml` und `Themes/Dark.xaml` (gleiche Schlüssel), Bedienelemente in `Themes/Styles.xaml`. Pinsel immer als `DynamicResource`, nie fest verdrahtete Farben in Ansichten. `ThemeManager` folgt der Windows-Einstellung (Verwaltung → Archiv → Darstellung: wie Windows / hell / dunkel) und verwendet im Windows-Kontrastdesign die Systemfarben.
- **Zusätzliche Tokens:** `line-strong` (#CFCCC4 / #4A4D52) für Rahmen von Bedienelementen, `input-edge` (#8C9097 / #7D8188) als Unterkante von Eingabefeldern (≥ 3:1), `hover`, `pressed`, `on-accent`. Auswahl dunkel: `#304459`.
- **Stile:** `PrimaryButton` (eine Hauptaktion pro Bereich), `DangerButton` (Löschen: roter Text, keine rote Fläche), `QuietButton`/`IconButton` (Symbolknöpfe brauchen `AutomationProperties.Name`), `OptionButton` (Auswahlzeilen statt Kacheln), `Chip`/`ChipRadio`. Reiter sind unterstrichen, nicht als Karteikarten.
- **Fokus:** 2 px Akzentrahmen (`FocusRing`), Eingabefelder mit 2 px Akzentkontur ohne Verschieben. Strg+F / F3 springen von überall in die Suche.
- **Platzhalter:** `views:Field.Placeholder="…"` statt übergelegter TextBlocks; jedes Feld ohne sichtbare verknüpfte Beschriftung bekommt `AutomationProperties.Name`.
- **E-Mail-Inhalte** werden auch im Dunkelmodus auf hellem Grund gezeigt (so gestaltet vom Absender); selbst erzeugte Seiten (KI, Netz) folgen `prefers-color-scheme`.
