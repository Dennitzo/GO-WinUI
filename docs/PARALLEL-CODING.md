# Paralleles Coding auf zwei lokalen GPUs

In den Einstellungen zuerst das Coding-Modell und darunter ein paralleles Coding-Modell auswählen. „Keine“ belässt den Einzelagenten. Dasselbe Modell darf in beiden Feldern gewählt werden. Beim Coding-Start richtet GO zwei native llama.cpp-Instanzen ein: Hauptagent auf physischer GPU0, Subagent auf physischer GPU1. Jede Instanz behält ihr eigenes vollständiges Kontextbudget. Reicht der Speicher nicht, wird der Fehler gemeldet; GO verteilt eine Paarinstanz nicht stillschweigend auf beide GPUs.

Der Hauptagent kann `coding.agentStart` mit einem begrenzten Auftrag und optionalen exakten relativen `writePaths` nutzen, selbst parallel weiterarbeiten und anschließend `coding.agentWait` aufrufen. Pro Lauf arbeitet höchstens ein Subagent gleichzeitig. Er erhält einen eigenen Kontext und keine gesamte Unterhaltung. `coding.agentCancel` beendet ihn; globales Stoppen beendet beide. Der Hauptagent wartet vor dem Abschluss auf ausstehende Ergebnisse.

Ohne `writePaths` ist der Subagent lesend. Mit zugewiesenen Dateien darf er diese lesen, schreiben und bearbeiten. Terminal und weitere Unterdelegation werden ihm serverseitig nicht angeboten. Der Hauptagent übernimmt Integration und Terminalprüfungen. Er darf währenddessen andere Dateien bearbeiten; zugewiesene Dateien und allgemeine Terminalbefehle warten bis zum Zusammenführen. Werkzeuge, Ergebnisse, Agentenzustand und offene Vorschläge werden gespeichert, sodass bereits bestätigte Änderungen bei Wiederaufnahme nicht erneut ausgeführt werden.

`coding.read` liest standardmäßig die ganze Textdatei; explizite Zeilenbereiche bleiben möglich. Die frische Dateiausgabe wird während der nächsten Modellrunde nicht gekürzt. Bei knappem Kontext wird vorherige Arbeit verdichtet. Eine einzelne Datei, die selbst das Modellfenster überschreitet, erzeugt einen ausdrücklichen Kontextfehler statt einer scheinbar vollständigen Teilausgabe. Dann muss das Modell einen expliziten Bereich wählen.

Die Aufgabenverteilung orientiert sich am Hauptagent-/Subagent-Muster aus der [offiziellen Codex-Dokumentation](https://learn.chatgpt.com/docs/agent-configuration/subagents). Diese beschreibt keine lokale llama.cpp-GPU-Verteilung; die physische Zuordnung und getrennten Inferenzspuren sind GO-spezifisch.

## Funktionale Prüfung

- Native Zuordnung und gleichzeitige Generierung: `artifacts/parallel-coding-20260915`.
- Gateway-/Dateiwerkzeug-Akzeptanz: `GO_AI_PARALLEL_LIVE=1`, Test `CodingParallelLiveTests` in `GoWinUI.Tests`. Verwendet einen isolierten temporären Workspace.
- Reproduzierbare Prüfungen: `ParallelCodingTests`, `ParallelRuntimeResidencyTests`, `FullFileReadContextTests`, `CodingContextCompactorTests`.
- Keine Geschwindigkeitsvergleiche oder zeitbasierten Leistungsanforderungen.
