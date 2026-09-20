# Pikachu-Sitzung: Analyse und abgeleitete Änderungen am Blender-Werkzeug

Stand: 20.09.2026. Sitzung „Blender 3D Modell" (Client-Sitzung
`69810f75-a88b-4628-8d13-5359bf28ae36`, Workspace `C:\Users\AMD\Documents\GitHub\Pikachu`).
Die Sitzung selbst wurde nicht verändert; die Befunde wurden in Werkzeug, Baukasten,
Anleitung und Prompts übersetzt, damit die lokale AI Korrekturen und Verständnis selbst
erarbeitet.

## Ablauf der Sitzung

| Zeit (UTC) | Lauf | Ereignis |
|---|---|---|
| 10:41 | `run-1519b001…` | Erster Prompt mit Blender-Chip, Reasoning `none`. 84 Werkzeugschritte, vier Etappen (`01_blockout` bis `04_wangen_schwanz`), vier Renderrunden mit je drei Ansichten und Vision-Prüfung, zwei Umlenkungen. |
| 12:33 | | Nach einer Vision-Prüfung wurde der Sitzungscache aus der Datei wiederhergestellt (155.620 Token); die folgende Modellrunde verarbeitete 3.732 neue Token mit nur 18–32 Token/s. |
| 12:35 | | Stopp durch den Nutzer während der Prompt-Verarbeitung. Der native Slot beendete den Batch erst 55 s später; die Snapshot-Sicherung meldete „slot not idle". |
| 12:37 | `run-98ab9e22…` | Folgeprompt (Coding-Chip, Reasoning `on`): 164.371 Prompt-Token, **0 wiederverwendete Token**, 25 Token/s. Nach vier Minuten abgebrochen. |
| 12:41 | `run-2879b194…` | Wiederholung mit Blender-Chip, wieder 0 wiederverwendete Token, nach 90 s abgebrochen. |

Die gespeicherten nativen Prompts beider Läufe sind bis zur letzten Nachricht des
gestoppten Laufs byteidentisch (184 Nachrichten, Vergleich aus `coding_session_contexts`
und `run_checkpoints`). Der Cacheverlust ist damit nicht durch geänderte Nachrichten
erklärbar; die messbaren Ursachen und Absicherungen stehen unten.

## Sichtbare Modellfehler (Render `04_wangen_schwanz`)

Vergleich der Renderbilder mit der Referenz `references/pikachu_front.png`:

1. **Ohrspitzen versetzt.** Gelber Kegelstumpf und schwarze Spitze waren getrennte
   Objekte mit geratenen Positionen und Rotationen; in der Seitenansicht liegen sie
   nicht auf einer Achse.
2. **Wangen verschwinden im Kopf.** Die abgeflachten Kugeln wurden 1,5 cm unter die
   Kopfoberfläche gesetzt; bei 0,75 cm halber Dicke liegt fast der gesamte Patch im
   Kopf, sichtbar bleibt nur eine Sichel.
3. **Brauner Schwanzschlauch.** Der Schwanz wurde als `tube_curve` (rundes Rohr)
   gebaut, obwohl ein Blitzschwanz eine flache Zickzackplatte ist. Die Basis blieb
   als brauner Balken stehen.
4. **Rückenstreifen fehlen.** Es gab keinen Baustein für aufgesetzte Flächen.
5. **Körper unförmig.** Rumpf und Kopf sind zwei gestapelte Kugeln („Schneemann"),
   keine durchgehende Birnensilhouette.
6. **Vision zu großzügig.** Die Vision-Antworten bewerteten „Körper birnenförmig
   erfüllt", obwohl das Bild klar abweicht, und lieferten keine Änderungsanweisungen
   mit Richtung und Betrag. Referenz und Render wurden nie im selben Aufruf verglichen.
7. **Verlorene Runden.** Vier `image.input`-Aufrufe scheiterten, weil Downloads mit
   `workingDirectory` und einem projektrelativen Pfad in `pikachu_model/pikachu_model/…`
   landeten.

## Umsetzung

### Baukasten (`go_blender.py`)

- `lathe(name, profile, …)`: Rotationskörper aus `[(radius, z), …]` für Rumpf und
  Kopf in einem Stück.
- `cone_between(name, start, end, start_radius, end_radius, …)` und
  `split_point(start, end, fraction)`: Kegel und Kegelstümpfe exakt zwischen zwei
  Weltpunkten; farbige Spitzen bleiben fluchtend.
- `point_on_sphere(center, radius, azimuth, elevation)` und
  `surface_patch(name, point, normal, radius, thickness, scale, sink, …)`: Wangen,
  Augen und Streifen liegen sichtbar auf der Oberfläche.
- `flat_polygon(name, outline, thickness, plane, …)`: flache Platten aus einem
  2D-Umriss, etwa der Blitzschwanz.
- `mirror_x(obj)`: unabhängige Spiegelkopie für Links/Rechts.

### Werkzeugvertrag

- `blender.execute stage` akzeptiert `views`, `resolution` und `samples` und rendert
  die neue Revision sofort nach `<Revision>_renders/`; `images` und `renderCompleted`
  stehen im Ergebnis. Jede Etappe hat damit Prüfbilder ohne zweiten Aufruf.
- `media.analyze` akzeptiert `referenceUploadIds` (bis zu sechs Bilder). Referenzen
  und Render gehen in einem Vision-Aufruf an das Modell; die Anweisung verlangt einen
  Vergleich Bauteil für Bauteil mit Änderungsanweisungen (Richtung, Achse, Betrag
  relativ zu einem Bezugsmaß).
- Die Vision-Systemregel (`GeneralAgentPolicies.VisionAnalysisSystemPrompt`) verlangt
  lange, geometrisch genaue Beschreibungen, trennt Beobachtung, Schlussfolgerung und
  Unsicherheit und beendet jede Prüfung mit einer nummerierten Verbesserungsliste.
  Ein Kriterium gilt erst als erfüllt, wenn kein Unterschied mehr benannt wird.
- `scaffold` legt `references/` an und gibt `referencesPath` zurück; die Anleitung
  erklärt die Pfadregel für `coding.command`-Downloads.

### Anleitung und Prompts

`MODELING_GUIDE.md`, die Projekt-README, `BlenderAuthoringGuide` (System- und
Werkzeugbeschreibung) und die Ergebnisanweisungen von `stage`, `render` und
`scaffold` beschreiben die Figurenmethode (Lathe-Silhouette, Kegelsegmente auf einer
Linie, Oberflächenpatches, flache Platten), den Referenzvergleich je Etappe und die
Ableitung der nächsten Korrektur aus der priorisierten Änderungsliste.

## Tokenwiederverwendung nach Stopp

Messungen gegen die laufende native Runtime (DeepSeek-V4-Flash-Vision, IQ1_S):

| Szenario | Wiederverwendete Token |
|---|---|
| Gleicher Prompt erneut | 3.282 von 3.286 |
| Folgeprompt, Reasoning `none` → `on` (ein Assistant-Zug) | 3.282 von 3.297 |
| Abbruch während der Prompt-Verarbeitung, gleicher Prompt erneut | 12.282 von 12.286 |
| Wiederherstellung aus Datei, Fortsetzung mit Reasoning `on`, Abbruch, erneut | 15.232 von 15.236 |

Der Abbruch selbst und die Wiederherstellung erhalten den Prefix. Mit dem
Original-Template rendert DeepSeek jeden **historischen** Assistant-Zug abhängig vom
aktuellen Thinking-Schalter (`</think>` gegenüber `<think>…</think>`): Ein
Reasoning-Wechsel verschiebt damit alle Token ab dem ersten Assistant-Zug, bei langen
Sitzungen also praktisch den gesamten Kontext. Das Template wird jetzt so angepasst,
dass historische Züge nach ihrem gespeicherten `reasoning_content` gerendert werden
(`workers/coding/catalog.py`, `DEEPSEEK_HISTORY_BY_CONTENT`); nur der Generierungsprompt
folgt dem aktuellen Schalter. Der Jinja-Test
`test_installed_deepseek_history_prefix_is_identical_across_reasoning_switches` belegt
das mit dem installierten Template.

Die unterbrochene Snapshot-Sicherung wartet jetzt bis zu 90 s auf das Ende des
laufenden nativen Batches (`INTERRUPTED_SAVE_IDLE_SECONDS`), statt nach 10 s
aufzugeben und nur den Stand vor dem Stopp auf der Platte zu lassen. Das
Prüfskript `verify_stop_session_cache.py` besitzt dafür `--switch-reasoning`.

Der Blender-Chip wird außerdem als eigene persistente Sitzungsaktion gespeichert
(`PersistentToolAction.Blender`, Spalte `persistent_tool_variant`), damit ein
Folgeprompt nicht mehr als Coding-Prompt ohne Blender-Anweisung gesendet wird.

## Prompt-Verarbeitung und GPU-Auslastung

Während der Sitzung lief die Prompt-Verarbeitung mit 17–37 Token/s, auf dem frisch
gestarteten Prozess dagegen mit 216–267 Token/s bei Neuprompts und Inkrementen auf
12k–30k Token Cache; beide GPUs sind dabei aktiv (Layer-Split, SM-Spitzen 98–99 %).
Die Ursache der Verlangsamung im zweistündigen Prozess ist aus den Logs nicht
eindeutig bestimmbar; die Vergleichsmessungen zu Split-Modi stehen in
`GO-AI-SERVER.md`.
