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
  -Model qwen3-coder-next `
  -Continuous
```

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

## Einstein-Kampagne

Die Kampagne untersucht anerkannte exakte, effektive, semiklassische und
perturbative Modelle zu Einsteins Feldgleichungen, Quantenfeldern in gekrümmter
Raumzeit, schwarzer-Loch-Thermodynamik und Niederenergie-Grenzen der
Stringtheorie. Der Agent muss Resultate symbolisch und numerisch prüfen,
Gültigkeitsbereiche und offene Fragen dauerhaft dokumentieren und zu jedem Fall
eine fachlich sinnvolle Grafik oder Simulation samt reproduzierbaren Daten
erzeugen. Er darf Plot- und Simulationsverfahren selbst auswählen und in
späteren Iterationen umbauen. Wissentlich inkonsistente Eingabedaten sind nicht
Teil der Kampagne. Jeder unabhängig als `verified` bestätigte Fall erhält
zusätzlich ein eigenes ausführliches Markdown-Dokument unter `solutions/` mit
Herleitung, Annahmen, Gültigkeitsbereich, Prüfresiduen und Reproduktionsschritten.

Beim Start öffnet sich automatisch eine lokale Browseransicht auf Loopback. Sie
beobachtet `visualizations/`, `simulation_data/` und `solutions/` und aktualisiert
Plots, Simulationen, Zwischenmetriken und Lösungsstände sekündlich ohne
Browsercache. Lange Rechnungen schreiben ihren atomaren Fortschritt nach
`simulation_data/live_progress.json` und aktualisieren einen Live-Plot. Mit der
Umgebungsvariable `GO_AI_LIVE_DASHBOARD=0` kann das automatische Öffnen für einen
unbeaufsichtigten Lauf deaktiviert werden. Der Dauermodus läuft bis `Ctrl+C`.
