# Claude Science-Modus und Forschungs-Sandbox

## Ziel

Die Sidebar erhält drei getrennte Arbeitsansichten: **ChatGPT** (bisher `General`), **Codex** (bisher `Coding`) und **Claude Science**. Die ersten beiden Namen sind reine Produktbeschriftungen; bestehende Sitzungswerte und Protokolle `general` und `coding` bleiben dadurch stabil. Claude Science wird als eigener Sitzungstyp mit eigener Forschungsoberfläche gebaut. Das existierende Deep-Research-Werkzeug bleibt die Recherche- und Schlussfolgerungs-Engine, erscheint im Claude-Science-Modus aber nicht mehr als auswählbarer Chat-Chip.

Die Ausführungsumgebung wechselt in diesem Modus von einem frei zugewiesenen Workspace zu einer vom Assistenten verwalteten, isolierten Sandbox je Forschungsvorhaben. Forschungsergebnisse, Code, Datenbezüge, Laufumgebungen und Manuskripte werden miteinander verknüpft und reproduzierbar versioniert.

## Analyse der Claude-Science-Referenz

Anthropic beschreibt Claude Science als Forschungs-App um bestehende Claude-Modelle herum, nicht als eigenes Modell. Die öffentlich beschriebene Arbeitsweise verbindet Literatur und Datenbankabfragen, Analysen auf eigener Recheninfrastruktur, nachvollziehbaren Code und Umgebungen, wiederverwendbare Domänen-Skills sowie persistente Python-/R-Sitzungen. Ergebnisse wie Tabellen, Abbildungen und Notizen sollen ihre Konversation, ihren Code und ihre Ausführungsumgebung mitführen. Spezialisierte Renderer werden für wissenschaftliche PDFs, Sequenzen, Proteine/Strukturen und chemische Moleküle genannt. Eine Prüfschicht soll falsche Quellenangaben, unbelegte Zahlen und Abbildungen ohne Übereinstimmung zum Quellcode markieren. Manuskripte können neben der Analyse mit Markdown-/LaTeX-Vorschau entstehen. Rechenläufe reichen laut Produktbeschreibung vom Laptop und lokalen GPUs bis zu HPC/SSH bzw. Modal. Die genannten Domänenbeispiele umfassen Einzelzell-RNA-seq, Evolution/Phylogenie, Proteinstrukturen und Cheminformatik.

Diese Punkte sind Produktbeschreibungen von Anthropic und keine unabhängige Leistungsprüfung. Der Plan übernimmt die Funktionsziele, aber jede Fähigkeit erhält GO-eigene Sicherheitsgrenzen und messbare Abnahmekriterien. Referenz: [Claude Science (Betaversion)](https://claude.com/de/product/claude-science).

## Ist-Zustand in GO-WinUI

Deep Research bleibt unter der vorhandenen Action `builtin.web/deep-research` und den Werkzeugnamen `research.deep` / `web.deepResearch`. Schon vorhanden sind Forschungsprofile, Problemverständnis und Planfortschritt, persistente Literatur-/Beleg- und Hypothesendaten, Experimente und Verifikationen, wissenschaftliche Toolchain-Aufrufe, Resultatberichte und Exporte. Diese Fähigkeiten bilden den Unterbau und werden nicht als zweites Deep-Research-System dupliziert.

Heute ist die ausführbare wissenschaftliche Toolchain an den Coding-Modus und dessen zugeordneten Workspace gebunden. Der lokale Executor legt eine `environment.lock.json` an, aber sein Status weist Netzwerkisolation derzeit ausdrücklich als nicht durchgesetzt aus. Ein Windows Job Object begrenzt Prozesslebensdauer und Prozessbaum, stellt für sich allein aber keine Dateisystem-, Netzwerk- oder Kernel-Isolation dar. Claude Science darf deshalb erst nach Einführung der unten beschriebenen Sandbox unbeschränkten Experimentcode starten.

## Produkt- und Sitzungsmodell

### Modi

- `ChatGPT` zeigt weiterhin ausschließlich bisherige General-Sitzungen.
- `Codex` zeigt weiterhin ausschließlich bisherige Coding-Sitzungen.
- `Claude Science` zeigt ausschließlich Forschungsvorhaben und wissenschaftliche Sitzungen.
- Die sichtbaren Labels ändern keine persistenten oder öffentlichen Kennungen für General/Coding.
- Claude Science erhält einen neuen internen Wert `ChatMode.ClaudeScience` und explizite, getrennte Sitzungs- und Projektfilter. Bestehende Projekte und Sitzungen werden weder kopiert noch automatisch umklassifiziert.
- Moduswechsel speichert den Entwurf und öffnet die letzte Sitzung des Zielmodus. Ein neues Forschungsvorhaben erzeugt einen isolierten Projektcontainer; es benötigt keinen vom Nutzer ausgewählten Coding-Workspace.

### Claude-Science-Oberfläche

Der Modus wird als Forschungsarbeitsplatz mit drei Bereichen umgesetzt:

1. **Forschungsnavigation links:** Vorhaben, Protokolle, Literatur, Datensätze, Experimente, Manuskripte, Skills und archivierte Läufe. Suche und Sortierung gelten nur für Claude-Science-Daten.
2. **Arbeitsfläche in der Mitte:** umschaltbare Ansichten `Übersicht`, `Recherche`, `Daten`, `Analyse`, `Abbildungen`, `Manuskript` und `Prüfung`. Die Auswahl bleibt je Sitzung erhalten. Ein Forschungsgraph zeigt Fragen, Hypothesen, Quellen, Experimente, Befunde und Widersprüche mit Status und Herkunft.
3. **Agenten-/Chat-Seitenleiste rechts:** Deep-Research-Aktivität, kurze Entscheidungspunkte, Fortschritt, Quellen, ausgeführte Befehle und Rückfragen. Streamingtext, Toolschritte und Rechenstatus bleiben getrennte Ereignisse, damit Statusanzeige nicht pro Token flackert.

Auf schmalen Fenstern werden Bereiche als Tabs mit wiederherstellbarem Navigationszustand angezeigt. Forschungsansichten müssen im nativen WebView und im LAN-Browser dieselbe Sitzungsrevision, Sandbox- und Artefaktzustände darstellen.

### Einstieg und Steuerung

Der neue Forschungsdialog fragt in einem kleinen Protokollformular nach Frage/Ziel, Fachgebiet (optional), Datenquellen/Anhängen, gewünschtem Ergebnis, Rechenbudget und Modus `nur recherchieren` oder `recherchieren und rechnen`. Das Modell darf die Fachdomäne selbst klassifizieren und unbekannte Disziplinen vorschlagen. Vor riskanten, kostenintensiven oder externen Schritten zeigt es eine knappe Begründung und fragt gezielt nach. Nutzer können jeden Lauf pausieren, fortsetzen, abbrechen, verzweigen oder aus einem gespeicherten Checkpoint wiederherstellen.

## Forschungsablauf

1. **Auftrag verstehen:** Originalfrage, operationalisierte Frage, Domäne, Begriffe, Annahmen, Variablen, Datenbedarf, Erfolgskriterien und Mehrdeutigkeiten strukturiert festhalten. Falsche Voraussetzungen und nicht beantwortbare Fragen markieren.
2. **Protokoll festlegen:** Forschungsprofil automatisch vorschlagen (Web, Evidenzsynthese, Systematic/Scoping Review, Literaturupdate, Replikation, offenes Problem oder Mathematik). Einschluss-/Ausschlussregeln, Sprachraum, Zeitfenster, Prüfstandard und Grenzen versionieren.
3. **Dynamischen Forschungsgraph aufbauen:** Teilfragen, Definitionen, Literaturabfragen, Hypothesen, Ableitungen, Experimente, Gegenbeispiele, Verifikation und offene Fragen als Knoten speichern. Abhängigkeiten, Belege, Widersprüche und Herkunft als Kanten erhalten. Erfolgskriterien nicht ohne Nutzerzustimmung ändern.
4. **Suchen und Quellen auflösen:** bestehende SearXNG-Integration sowie strukturierte Quellenconnectoren (Crossref, OpenAlex, PubMed/PMC, arXiv, DataCite) verwenden. DOI/PMID/arXiv-ID priorisiert deduplizieren; Korrekturen, Rücknahmen, Preprint und Publikation als verknüpfte Versionen behalten. Fehlgeschlagene Suchen diagnostizieren und umformulieren statt identisch wiederholen.
5. **Evidenz extrahieren:** nur geöffnete, überprüfte Inhalte zitieren; exakte Fundstelle, Seite/Abschnitt, Hash, Abrufzeit und Evidenzqualität aufnehmen. Abstract-only und indirekte Belege sichtbar kennzeichnen. Nutzerdateien, rechtmäßige offene Volltexte und freigegebene Connectoren verwenden.
6. **Lösungen und Hypothesen entwickeln:** Literaturbefund, Modellschluss und eigene Hypothese unterscheiden. Für neue Fragestellungen Wissenslücke präzisieren, mehrere Kandidaten bilden, Vorhersagen ableiten und Gegenargumente bzw. Gegenbeispiele suchen.
7. **Analysieren und rechnen:** Code nur innerhalb der Sandbox ausführen. Experimente mit fixierten Eingabe-Hashes, Parametern, Seeds, Softwareversionen, Ressourcen und vollständigen Logs reproduzierbar machen.
8. **Prüfen:** Quellenprüfer, Claim-Evidence-Prüfung, Zahlen-/Tabellenprüfung, Abgleich von Abbildung mit ausführbarem Code, alternative Herleitung und geeignete numerische/formale Prüfungen ausführen. Status `Verified` nur bei erfüllten vorab festgelegten Kriterien setzen.
9. **Bericht und Manuskript erstellen:** Ergebnisse mit Quellen, Methoden, Limitationen und offenen Fragen als interaktiven Forschungsbericht und editierbares Manuskript darstellen. Markdown und LaTeX erhalten Live-Vorschau; Zitate und Abbildungen referenzieren die gespeicherten Forschungsobjekte.
10. **Checkpoint und Reproduktion:** nach jeder sinnvollen Phase Snapshot erzeugen; App-, Gateway- und Modellneustart dürfen den Auftrag mit vollständigem Graph-, Notebook-, Kernel- und Dateistand fortsetzen.

Die KI-Arbeit bleibt seriell über das konfigurierte lokale Modell. DeepSeek Vision erhält jeweils genau definierte wissenschaftliche Bild-/PDF-Ausschnitte und prüft lesbare Formeln, Tabellen, Diagramme, molekulare/strukturelle Darstellungen und vom Modell erzeugte Abbildungen. Vision-Ausgaben werden als Prüfhinweis gespeichert, nicht als alleiniger Nachweis.

## Sandbox statt Workspace

### Isolationsmodell

Jedes Claude-Science-Vorhaben erhält einen persistenten, vom Nutzerprofil verwalteten Ordner, zum Beispiel:

```text
%LOCALAPPDATA%/<ProductIdentity>/ResearchSandbox/<projectId>/
  inputs/       schreibgeschützte, gehashte Eingabekopien
  work/         veränderbare Analyseskripte und Zwischenergebnisse
  artifacts/    Abbildungen, Tabellen, Modelle und Berichte
  notebooks/    reproduzierbare Notebooks
  manuscripts/  Markdown-/LaTeX-Dokumente
  env/          gesperrte Python-/R-Umgebung
  runs/         stdout, stderr, Provenienz und Ressourcenmetriken
  snapshots/    atomare Checkpoints und Undo-Punkte
```

Dieser Ort ist nicht der frei beschreibbare Benutzer-Workspace. Dateien gelangen nur durch explizites Anhängen/Kopieren oder einen bestätigten Export hinein; Originale bleiben unverändert. Links/Junctions, Reparse Points und Pfad-Escapes werden abgewiesen. Sandbox-IDs sind an Projekt-ID, Owner, Formatversion und Integritätsmanifest gebunden.

### Ausführungsstufen

- **Stufe 1, sicherer MVP:** dedizierter `ResearchRunner`-Container unter Docker/WSL2, falls verfügbar, mit nicht privilegiertem Benutzer, schreibgeschütztem Runtime-Dateisystem, nur projektbezogenen Mounts, deaktiviertem Netzwerk, CPU-/RAM-/GPU-/Prozess-/Platten-/Zeitlimits und Job-Abbruch. Eine begrenzte native Fallback-Ausführung ist nur für ausdrücklich freigegebene, nicht ausführbare Analyse/Rendering-Aufgaben erlaubt; sie darf nicht als Sandbox bezeichnet werden.
- **Abhängigkeiten:** kein freier Internetzugriff aus Experimentcode. Ein separater Broker lädt erlaubte Pakete aus konfigurierten Quellen, prüft Hash/Signatur/Lizenz, erstellt Lockdatei und übergibt den Installationscache schreibgeschützt an den Runner. Paketinstallation ist benutzerlokal/projektbezogen und verändert keine System-Python- oder R-Installation.
- **Kernel:** langlebige Python- und später R-Kernel laufen je Forschungsvorhaben in eigenem Containerprozess. Jeder Request erhält Run-ID, Kernelgeneration, Speichergrenze und Timeout. Nach Absturz wird Kernelzustand aus Skripten, Notebooks, Eingabe-Hashes und Checkpoint neu hergestellt; In-Memory-Objekte allein gelten nicht als dauerhafte Quelle.
- **Datenzugriff:** nur Sandbox-Dateien und explizit zugelassene Connectoren. Datenbanken, Netzwerk, lokale Pfade und Cluster sind capability-basiert. Vor erstmaligem Remote- oder kostenpflichtigem Zugriff werden Ziel, Datenumfang und Auftrag angezeigt und bestätigt.
- **Cluster/GPU:** erst nach lokalem MVP. Eigene Connectoren mit kurzlebigen Credentials, SSH-Host-Allowlist, signierten Jobvorlagen, Ressourcenquoten und abfragbarem Stop/Status. Keine beliebigen Shell-Kommandos auf Remotehosts. Externe Dienste wie Modal sind optional und standardmäßig aus.
- **Audit und Undo:** unveränderliches Runmanifest, Vorher-/Nachher-Hashes, Software-/Container-Digest, Datenherkunft, Limits und Modell/Promptversion. Mutation in atomaren Change-Sets; Restore prüft Hashkonflikte und überschreibt keine später manuell angepasste Datei.

Vor Aktivierung von Python/R-Experimenten muss die Windows- und Containerkonfiguration echte Datei- und Netzwerkgrenzen nachweisen. Ein bloßes `networkIsolation=false` im Manifest ist kein Sicherheitsnachweis. Fehlt Docker/WSL2 oder eine gleichwertig nachgewiesene Isolation, bleibt die Ausführung deaktiviert und die Oberfläche bietet Recherche, Lesen, Vorschau und Planung.

## Wissenschaftliche Arbeitsflächen

- **Recherche:** Suchläufe, Connectorstatus, Dubletten, Screening, Ein-/Ausschlussgründe, Literaturmatrix, Zitationen und PRISMA-Flussdaten.
- **Daten:** Eingabedateien mit Hash/Format/Größe, Tabellen-/Schemaansicht, Variablenkatalog, Einheiten, fehlende Werte, Qualitätsprotokoll und Datenzugriffsrechte.
- **Analyse:** Skripteditor/Notebook, persistenter Kernelstatus, Umgebungs-Lock, Parameter, Seeds, Logs und Experimentvergleich. Ein Run darf keine parallele unprotokollierte Mutation am selben Kernel zulassen.
- **Abbildungen und wissenschaftliche Renderer:** Zoombare Grafik/Tabellen, Code- und Datenlink, Versionierung, Annotationen mit Folgeauftrag, native PDF-Vorschau. Erweiterbare Renderer für FASTA/Alignment/Genomtracks, Proteinstrukturen, SMILES/MOL, Molekül-2D-Ansicht und später 3D-Strukturansicht.
- **Manuskript:** Markdown/LaTeX-Editor mit Literaturreferenzen, Abbildungs-/Tabellenverzeichnis, Live-PDF-Vorschau, Änderungsvergleich und Export.
- **Prüfung:** Behauptung, Status, Belegstellen, Gegenbelege, Methoden, Zahl-/Code-/Abbildungschecks, Reviewer-Kommentare und offene Probleme.
- **Artefakte:** jedes Ergebnis ist ein versioniertes Objekt mit `artifactId`, `projectId`, `runId`, Inhaltshash, Erzeugercode, Eingaben, Toolchain, Zeit und Vorversion. Chats und Artefakte referenzieren einander, bleiben aber getrennt exportierbar.

## Datenmodell und Schnittstellen

### Modus und Sitzungen

- `ChatMode.ClaudeScience` ergänzen; `General` und `Coding` intern unverändert lassen.
- DB-Migration muss `chat_sessions.chat_mode`, `chat_session_groups.chat_mode`, Modustrigger und Sitzungsindizes um `claudescience` erweitern. Wegen vorhandener SQLite-`CHECK`-Constraints ist dies als transaktionale Tabellenmigration mit Sicherung, Erhalt von Nachrichten/Anhängen/Artefakten/Runreferenzen, FTS-Rebuild und `foreign_key_check` zu planen.
- `AppSettings` erhält `ActiveScienceSessionId`; Moduswechsel/Deep Links/LAN-WebView setzen und lesen passende Modussitzung.
- Gruppen und Forschungsvorhaben werden getrennt von Workspacegruppen abgefragt; Modusfilter gilt auch für Suche, Zähler, Löschen und Wiederherstellen.
- Science-Prompts senden den Forschungsmodus und Sandbox-ID über versionierte Contracts. Servervalidierung lehnt wissenschaftliche Ausführungswerkzeuge aus General/Coding ab, wenn keine passende Sandboxcapability vorliegt.

### Forschungsdaten

SQLite bleibt die Wahrheit für Forschungsprojekte, Graphrevisionen, Protokolle, Suchläufe, Werke, Evidenz, Hypothesen, Claims, Experimente, Verifikationen, Reports, Checkpoints, Artefakte und Auditereignisse. Binärartefakte werden content-addressed gespeichert; große Eingangsdaten bleiben projektlokal und werden nur mit Metadaten/Hash indexiert. Schema enthält explizite Version, Aufbewahrungsregeln und Migrationen.

### Ereignisse

Alle Forschungsereignisse tragen `projectId`, `sessionId`, `runId`, `revision`, `occurredAt`, `sandboxId` und `correlationId`. Ereignisrevisionen verhindern, dass verspätete WebView-/LAN-Ereignisse einen neueren Zustand überschreiben. Streaming umfasst mindestens `research.plan.updated`, `research.search.completed`, `research.evidence.added`, `research.experiment.started/progress/completed`, `research.artifact.created`, `research.verification.completed`, `sandbox.state.changed`, `research.checkpoint.created` und `research.report.completed`.

## Umsetzung in Etappen

1. **Modusvertrag:** interne `ClaudeScience`-Sitzungsidentität, Settingspointer, Deep Links, Modusfilter und Migration/Restore definieren; ChatGPT/Codex sichtbare Namen von internen General/Coding-Werten entkoppeln.
2. **Persistenzsicherheit:** produktive DB-Kopie migrieren, Transaktions-/Rollback-/FK-/FTS-Verhalten nachweisen, portable Builds mit alten Datenbanken testen.
3. **Forschungsvorhaben:** separates Repository/API für Projektcontainer, Protokollversionen, Forschungsgraph und Checkpoints; Sitzungen mit Projekt statt Workspace gruppieren.
4. **Sandbox-Spike:** Runner-Container und Ressourcenquoten auf dieser Windows-Installation verifizieren. Tests gegen Dateilesen außerhalb des Mounts, Symlinks, Netzwerk/DNS, Prozessbaum, CPU/RAM/Platte, Abbruch und Gatewayneustart müssen bestehen, bevor Codeausführung freigeschaltet wird.
5. **Research-UI:** dedizierte Oberfläche mit Navigation, Forschungsgraph, Arbeitsflächen, Chat/Runstatus und responsivem Layout erstellen; vorhandene WebView und LAN-Clients über denselben versionierten Snapshot/Ereignisstrom bedienen.
6. **Deep Research einbetten:** bestehende `builtin.web/deep-research`-Orchestrierung in den Modus verlagern, Profile und Ergebnisse als Projektzustand sichtbar machen; kein zweiter Recherchepfad und kein zusätzlicher Chip.
7. **Quellen und Evidenz:** Connectoren, Auflösung, Screening, Seitenbelege, Korrektur-/Retraktionsstatus und Zitierprüfung abrunden.
8. **Python-Sandbox:** reproduzierbarer Python-Kernel, Paketsperren, Notebook, Experimente, Plot-Renderer, Änderungsverlauf und Undo implementieren.
9. **Prüfer:** bibliographische Prüfung, Behauptung/Beleg-Abgleich, Zahlen-/Tabellenprüfung und Abbildungs-Code-Prüfung als getrennte, protokollierte Prüfungen hinzufügen.
10. **Manuskript und Export:** Markdown/LaTeX-Vorschau, Referenzen, PDF/DOCX/BibTeX, kompletter Audit-/Reproduktionsbundle.
11. **R und Domänenrenderer:** R-Kernel sowie wissenschaftliche Dateitypen schrittweise nach separaten Sicherheits- und Golden-File-Suiten ergänzen.
12. **Remote/HPC:** erst nach erfolgreichem lokalen Betrieb optionale, eng begrenzte Cluster- und GPU-Connectoren ergänzen.
13. **DeepSeek-Abnahme:** repräsentative Literatursynthese, offene mathematische Frage, reproduzierbare Datenauswertung mit Abbildung und PDF-/Tabellen-Vision-Prüfung mit DeepSeek/DeepSeek Vision durchlaufen. Quellen, tatsächlich verarbeitete Bildseiten, Sandboxgrenzen, Ausgaben und Wiederaufnahme nach Neustart prüfen.

## Test- und Abnahmematrix

- **Modusgrenzen:** ChatGPT, Codex und Claude Science zeigen nur ihre Sitzungen; Projektzuordnung, aktiver Pointer und Deep Link überleben Neustart und LAN-Verbindung.
- **Migration:** alte General-/Coding-Sitzungen, verschachtelte Gruppen, Attachments, FTS, Artefakte und Runhistorie bleiben byte-/referenzkonsistent; Upgrade und Rollback funktionieren mit Portable-Datenbank.
- **Sandbox:** Path traversal, Junction, Symlink, nicht erlaubter Hostpfad, Netzwerkzugriff, unzulässiger Prozess, Ressourcenüberschreitung, Abbruch, kaputter Kernel und Gatewayverlust werden abgewiesen oder sicher wiederhergestellt.
- **Reproduzierbarkeit:** gleiche Inputs, Toolchain und Seeds ergeben dokumentierte gleiche bzw. toleranzkonforme Ergebnisse; geänderte Eingaben/Abhängigkeiten erzeugen neue Provenienzrevision.
- **Deep Research:** mehrdeutige Frage löst gezielte Rückfrage aus; Quellen-Fundstelle stimmt; zurückgezogene Studie wird markiert; Gegenbeleg senkt Claim-Status; ungelöste Frage wird nicht als bewiesen ausgegeben.
- **Abbildungen/Vision:** Vision vergleicht gerenderte Abbildung mit gespeicherten Daten und Code; Tabellen-/Formel-OCR wird gegen maschinenlesbare Quelle geprüft. Kein Screenshot allein gilt als Korrektheitsbeweis.
- **Manuskript:** Zitate, Referenzen, Tabellen, Abbildungen, PDF-Vorschau und Export enthalten dieselben revisionsgebundenen Forschungsobjekte.
- **Last und Wiederaufnahme:** lange Läufe checkpointen, pausieren, stoppen und nach Desktop-/Gateway-/Containerneustart fortsetzen; Grenzen und Quoten bleiben durchgesetzt.
- **UI:** Tastaturbedienung, Screenreader, Dark/Light, schmale Fenster, große Literaturmengen, LAN-Liveupdates, Konfliktzustände und leere/fehlgeschlagene Läufe prüfen.
- **Akzeptanz:** Kein Python/R-Lauf wird als Sandboxlauf akzeptiert, bis Isolationstests grün sind; kein Befund erhält `Verified`, wenn die im Protokoll festgelegten Kriterien fehlen.

## Nicht-Ziele des ersten Releases

- Allgemeiner beliebiger Dateisystemzugriff oder Nutzung des Coding-Workspaces aus Claude Science.
- Beliebige Internetzugriffe aus ausgeführtem Analysecode.
- Automatische Änderungen an Originaldaten oder systemweiten Python-/R-Installationen.
- Ungeprüfte medizinische/klinische Handlungsempfehlungen.
- Vollständige HPC-Steuerung, hunderte GPUs, interaktive Molekülmodellierung und alle denkbaren Fachgebiete in einem ersten Release.
- Behauptung, Modellplausibilität oder Vision allein stelle einen wissenschaftlichen Beweis dar.

## Empfohlene Release-Gates

**Gate A:** dritter Modus, sichere SQLite-Migration und leere Forschungsoberfläche.  
**Gate B:** vollständig gesperrte lokale Sandbox mit Python und Provenienz.  
**Gate C:** Deep Research, Quellen-/Claim-Prüfung, Artefakte und Manuskript im End-to-End-Lauf.  
**Gate D:** R, Fachrenderer sowie optionale Remote/HPC-Connectoren.

Jedes Gate erhält eine Live-Abnahme mit dem installierten DeepSeek-Modell. Die erste Forschungsprobe erfolgt erst nach expliziter Freigabe durch den Nutzer.
