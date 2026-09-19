# Workspace-Werkzeuge: Dokumente, Blender und visuelle Prüfung

## Bedienung

General und Coding erhalten dieselben verfügbaren Fachwerkzeuge. Lokale Datei-,
Blender- und Startwerkzeuge setzen einen ausgewählten, bestehenden Workspace
voraus. BricsCAD setzt eine verbundene BricsCAD-Instanz voraus. Der kompakte
Werkzeugselektor enthält Namen und Aufgabenbeschreibungen; die vollständigen
Schemas werden anschließend gezielt bereitgestellt.

- **Dokument erstellen** startet `document.agent`, einen eigenen persistenten
  Dokumenten-Agenten mit gemeinsamem Ausgangskontext, eigenen Werkzeugbelegen und
  einer deutschen Fachanweisung. Der aufrufende Agent wartet auf das Ergebnis.
  DOCX/PDF/Markdown/Text nutzen die vorhandenen versionierten Dokumentartefakte.
  Andere Formate benötigen passende Bibliotheken bzw. Programme im Workspace;
  daraus folgt keine Zusage für jedes proprietäre Dateiformat.
- **Blender** nutzt die installierte Blender-Anwendung. `info` prüft die
  Installation; `run` führt ein zuvor geschriebenes und per SHA-256 bestätigtes
  Python-Skript aus; `open` öffnet die `.blend`-Datei sichtbar. Szenen und Render
  gehören in den ausgewählten Workspace. Subagenten verwenden ihre bestehende
  isolierte Arbeitskopie und veröffentlichen nur zugewiesene Dateien.
- **Bild analysieren** kombiniert `image.input` (lokales Bild oder Aufnahme eines
  ausdrücklich zur Aufgabe gehörenden Fensters) mit `media.analyze`. Das Hochladen
  allein gilt nicht als Bildanalyse. `workspace.open` startet erzeugte HTML/PDF-
  Vorschauen oder eine native EXE für eine anschließende Sichtprüfung.

Die Werkzeuggrenzen prüfen Pfade, Schemas, Dateistände und Schreibbereiche. Wie
bei den bestehenden Coding-Terminalwerkzeugen ist die Arbeitskopie keine
Betriebssystem-Sandbox für beliebige Python-Skripte.

## Reproduzierbare Modelltests

`WorkspaceToolsLiveTests` prüft echte General- und Coding-Läufe mit lokalem Modell,
nativen Clientwerkzeugen und dem laufenden GO-Gateway. Es nutzt eine getrennte
Testdatenbank und verändert keine bestehenden Benutzersitzungen.

Die vollständige Matrix mit getrennten Protokollen lässt sich auch über
`powershell -NoProfile -File windows/test-workspace-tools.ps1` ausführen.

```powershell
$env:GO_WORKSPACE_TOOLS_LIVE = '1'
$env:GO_WORKSPACE_LIVE_EVIDENCE = "$PWD/artifacts/validation/workspace-live"
dotnet test tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release `
  --filter 'FullyQualifiedName~WorkspaceToolsLiveTests'
```

Optional grenzen `GO_WORKSPACE_LIVE_MODE=General|Coding` und
`GO_WORKSPACE_LIVE_SCENARIO=document|blender|visual` den Lauf ein. Ohne diese Filter
werden alle sechs Kombinationen nacheinander geprüft. Nicht parallel zu anderen
GO-Builds, Modellläufen oder Tests starten. Ohne Opt-in startet dieser Test keine
Programme und keine Modellinferenz.

Jeder Durchlauf bewahrt Prompt, Ereignisse, Werkzeugargumente/-ergebnisse und
`passed` in einer eigenen `acceptance.json` auf. Die erzeugten Dateien liegen
daneben. Der Dokumententest liest die tatsächlich exportierte DOCX-Datei erneut
ein. Der Blender-Test verlangt Szene, Render, Bildanalyse und Öffnen der Szene.
Der visuelle Test verlangt zwei Fensteraufnahmen, Bildanalyse und eine geänderte
Anwendung; im Coding-Modus zusätzlich eigene Aufnahme und Analyse des Subagenten.

Beispiel für einen normalen visuellen Arbeitsauftrag:

> Starte die in diesem Workspace erstellte Anwendung. Erfasse genau ihr Fenster
> und untersuche den Screenshot mit Bild analysieren auf abgeschnittene Inhalte,
> Abstände und Lesbarkeit. Behebe die tatsächlich sichtbaren Fehler im Projekt.
> Starte die aktualisierte Fassung und prüfe einen neuen Screenshot. Belege die
> Änderungen mit den beiden Bildanalysen. Ein paralleler Subagent darf einen
> eigenen, nicht überschneidenden Bereich bearbeiten und dieselben Bildwerkzeuge
> für seine Prüfung verwenden.

## Quellen und Grenzen der Recherche

Die zunächst blockierte lokale SearXNG-Suche wurde im selben Arbeitsauftrag
repariert. Über die korrigierte Engine-Auswahl wurde anschließend die offizielle
OpenAI-Beschreibung [Architectural visualization with Astra](https://developers.openai.com/blog/architectural-visualization-with-astra)
vom 04.09.2026 gefunden und gelesen: Astra erstellt editierbare Szenen mit `bpy`,
führt Skripte über Blenders ausführbare Datei im Hintergrund mit `--python` aus
und nutzt Render sowie die Blender-Oberfläche zur wiederholten Sichtprüfung.
Diese dokumentierte Vorgehensweise entspricht der hier implementierten Anbindung.

Die CLI-Optionen wurden zusätzlich gegen die Hilfe der lokal installierten
Blender-Version 5.2 und den
[offiziellen Blender-Quellcode](https://github.com/blender/blender/blob/main/source/creator/creator_args.cc)
geprüft. Ein fremder Blender-MCP-Server ist nicht erforderlich.

Die Suchreparatur ergänzt explizite lokale Engine-Gruppen, gemeinsame Cooldowns
auch für allgemeine Anfragen, maximal eine Wiederholung ohne Sprachfilter,
einen Mindestabstand zwischen Suchanfragen und einen 45-Sekunden-Cache für
identische erfolgreiche Suchen. `site:`-Ergebnisse müssen tatsächlich zur
angefragten Domain gehören. Es wird kein anderer Suchdienst außerhalb der
lokalen SearXNG-Instanz als Fallback verwendet. Einzelne Anbieter können weiterhin
extern gesperrt sein; diese werden ausgelassen statt fortlaufend erneut gefragt.

## Prüfstand

Am 19.09.2026 bestanden: 668 Client-, 658 Server-, 163 Web- und 56 native
Cache-Tests (`artifacts/validation/agent-context/summary.json`). Der Release-Build
der vollständigen Solution hatte keine Fehler oder Warnungen. Der Portable-Build
bestand den Starttest einschließlich Prüfung aller 43 eingebetteten Web-Dateien.
Die Prüfungen wurden nach der letzten General-Werkzeugfreigabe erneut erfolgreich
ausgeführt. Das abschließende, ebenfalls startgeprüfte Paket liegt unter
`artifacts/portable/workspace-tools-win-x64/GO.exe`.

Die gestartete Portable-Oberfläche zeigt die einheitlichen, ausgerichteten
Pluszeichen und entfernt die bereits vorhandene leere Projektgruppe durch
Migration 37. Die zwei vorhandenen Sitzungen und 20 Nachrichten blieben erhalten;
der SHA-256 des vollständigen Nachrichtenbestands ist vor und nach dem Upgrade
identisch (`workspace-profile-before.json`, `workspace-profile-after.json`).

Die produktive lokale Suchinstanz lieferte jeweils fünf passende Treffer für
Blender-API-, Python-TaskGroup- und OpenAI-Blender-Anfragen. Alle Antworten melden
`provider: searxng`, `isFallback: false`; die Domain-Einschränkungen wurden
eingehalten. Die identische OpenAI-Abfrage benötigte beim ersten Aufruf 673 ms,
aus dem kurzen Cache 6 ms. Belege: `searxng-production-live.json` und
`searxng-production-cache.json` im Validierungsordner.

Ein weiterer Produktionstest traf tatsächlich auf Rate-Limits von Google CSE
und Brave. Die folgende Abfrage ließ beide Anbieter aus und lieferte über Bing
zwei passende Blender-Dokumentationsseiten. Sechs gleichzeitige identische
Anfragen teilten dieselbe Antwort (490–493 ms); keine wurde als Fehler oder
externer Fallback beantwortet (`searxng-production-concurrent.json`). Eine
gewünschte Höchstzahl an Treffern ist dabei keine garantierte Mindestzahl.

Die langen Modellabnahmen wurden auf Wunsch des Nutzers beendet; keine weiteren
Läufe wurden gestartet. Der tatsächliche Stand:

| Modellabnahme | Ergebnis |
|---|---|
| General / Dokument | Bestanden: eigener Dokumenten-Agent, Erstellen, Lesen, Ergänzen, erneutes Lesen und Prüfung des exportierten DOCX. `General-document-GO57f4c3157f` |
| General / Blender | Bestanden: Szene, Render, Vision-Analyse und Öffnen. `General-blender-GOd9da811c8f` |
| Coding / Dokument | Die Werkzeugfolge wurde abgeschlossen; die ursprüngliche Testforderung eines untergeordneten statt übergeordneten Leseaufrufs scheiterte. Diese zu strenge Assertion wurde korrigiert, aber noch nicht erneut live ausgeführt. `Coding-document-GObf23a2b838` |
| General / visuelle Änderung | Nicht bestanden: Abbruch durch das damalige 25-Minuten-Zeitlimit. Rotes FEHLER-Fenster aufgenommen und korrekt analysiert, danach auf Grün/BEREIT geändert, erneut geöffnet und aufgenommen. Zweite Analyse und vollständiger Abschluss nicht belegt. `General-visual-GOa2ab2c96cc` |
| Coding / Blender und visuelle Änderung | Noch nicht live ausgeführt. Werkzeugfreigaben und Verträge sind automatisiert geprüft. |

Alle Fallordner liegen unter `artifacts/validation/workspace-live` und enthalten
unveränderte `acceptance.json`-Belege. Das Zeitlimit neuer Fälle beträgt 45 Minuten.
Ein zusätzlicher früher General-Visual-Start scheiterte direkt nach dem App-Neustart
an der noch nicht erreichbaren Modelllaufzeit; der oben dokumentierte Lauf wurde
erst nach positiver Laufzeitprüfung gestartet.

Die zusätzliche manuelle Browser-Fensterkontrolle wurde vom Computer-Use-Werkzeug
beendet, weil es die Browser-URL nicht zuverlässig bestimmen konnte. Danach wurden
keine weiteren manuellen UI-Aktionen darüber ausgeführt. Die App-eigenen
Bildwerkzeuge des bereits gestarteten Tests lieferten die oben genannten Belege.

Die konkrete Übergabe für die lokale AI einschließlich Weiterarbeits-Prompt,
Dateipfaden, geprüftem Blender-Beispiel und offenen Abnahmen steht in
[`BLENDER-TOOL-HANDOFF.md`](BLENDER-TOOL-HANDOFF.md).
