# Coding-Agent-Livetests

Alle Live-Szenarien verwenden dieselbe Agentenlaufzeit, zeigen Programmstarts in
einem sichtbaren PowerShell-Fenster und schreiben parallel ein fortlaufendes
JSONL-Protokoll. `latest.json` im jeweiligen Szenarioordner verweist immer auf
den aktuellen beziehungsweise letzten Lauf.

## Start

```powershell
.\windows\run-coding-agent-live-test.ps1 `
  -Scenario Einstein `
  -Workspace 'C:\Users\AMD\Documents\GitHub\QwenCoderEinstein-Test' `
  -Model openai/gpt-oss-120b `
  -Continuous
```

Als Coding-Modell sind `ud` (DeepSeek V4 Flash), `qwen3-coder-next` und
`openai/gpt-oss-120b` zulässig. GO verwendet für alle drei denselben
Workspace-, Tool-, Diff- und Verifikationsvertrag.

Weitere Szenarien sind `Coding`, `Excel` und `Physics`. Ein vorhandener
Physik-Workspace wird mit `-ContinueExisting` weiterverwendet. Mit
`-CurrentWindow` läuft der Test im aktuellen statt in einem neuen Fenster.

## Protokolle

- Live-JSONL: `artifacts\coding-live-tests\<Szenario>\*.jsonl`
- Verweis auf letzten Lauf: `artifacts\coding-live-tests\<Szenario>\latest.json`
- Testresultat: `artifacts\coding-live-tests\test-results\*.trx`

Das JSONL-Protokoll enthält Modellereignisse, Toolvorschläge, Laufzeiten,
Programm-Ausgaben, Fehler, Workspace-Revisionen und Abschlusszustände. Große
Dateiinhalte werden nicht dupliziert; dafür werden Länge und SHA-256 gespeichert.

## Einstein-Workflow

Der Workflow untersucht anerkannte exakte, effektive, semiklassische und
perturbative Modelle zu Einsteins Feldgleichungen, Quantenfeldern in gekrümmter
Raumzeit, schwarzer-Loch-Thermodynamik und Niederenergie-Grenzen der
Stringtheorie. Der Agent muss Resultate symbolisch und numerisch prüfen,
Gültigkeitsbereiche und offene Fragen dauerhaft dokumentieren und zu jedem Fall
eine fachlich sinnvolle Grafik oder Simulation samt reproduzierbaren Daten
erzeugen. Er darf Plot- und Simulationsverfahren selbst auswählen und in
späteren Iterationen umbauen. Wissentlich inkonsistente Eingabedaten sind nicht
Teil des Workflows. Jeder unabhängig als `verified` bestätigte Fall erhält
zusätzlich ein eigenes ausführliches Markdown-Dokument unter `solutions/` mit
Herleitung, Annahmen, Gültigkeitsbereich, Prüfresiduen und Reproduktionsschritten.

Jeder direkte Livetest schreibt Aufgaben, Agentenantworten und Fehler zusätzlich
als strukturierte AI-Nachrichten nach `.go-workflow/chat-messages.jsonl` im
Workspace. Wird derselbe Workflow später in GO geladen, importiert die Sitzung
diese Einträge genau einmal in den Chat.

Lange Rechnungen aktualisieren `simulation_data/live_progress.json` und
`visualizations/live_progress.png` atomar. Im GO-Workflow wird jeder neue stabile
PNG-Inhaltsstand genau einmal als normale AI-Nachricht mit Bildartefakt
veröffentlicht. Verifizierte Dateien aus `solutions/` und der aktuelle
Aufgabenstand erscheinen ebenfalls als statische AI-Nachrichten. Es gibt weder
eine eingebettete Sonderansicht noch einen externen Browser. Der Dauermodus läuft
bis `Ctrl+C`; in GO übernimmt der zentrale Prompt-Stop-Button den Abbruch.
