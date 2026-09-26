# Build- und Betriebsskripte

## GO-WinUI-Client

Der vollständige Clientbuild führt Restore, Release-Build, Tests, win-x64-Single-file-Publish und Portable-Smoke aus:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```


## Native Modelle und Docker-Gateway

General, Coding, Vision und Embedding laufen unter Windows mit der vorhandenen Unsloth-Installation von
`llama-server.exe`. Die GGUF-Dateien werden direkt aus dem lokalen Hugging-Face-Cache gelesen. Docker betreibt
das GO-Gateway sowie die Sprach-, Bild- und Recherche-Worker. Der Gatewayzugriff auf die native Modellruntime
erfolgt über `http://host.docker.internal:8081`; der GO-Client verbindet sich mit dem Gateway auf Port 8080.

Die portable `GO.exe` startet die native Laufzeit bei aktivierter AI-Verbindung automatisch, wenn der
konfigurierte Gateway auf diesem PC läuft. Die erforderlichen Hilfsdateien sind in der EXE enthalten;
der Start funktioniert auch außerhalb des Repository-Verzeichnisses und nach einem Windows-Neustart.
Die Modellkataloge werden beim Öffnen der Einstellungen geladen. Ein bereits laufender Server wird wiederverwendet.

Alle folgenden Befehle werden im Repository-Verzeichnis ausgeführt. Benötigt werden Docker Desktop,
die für GO verwendete .NET-SDK-Version und die installierte Unsloth-Runtime mit Python und llama.cpp.

| Einstellung | Standard |
|---|---|
| Native Modellablage | `%USERPROFILE%\.cache\huggingface\hub` |
| Native Serverdatei | `%USERPROFILE%\.unsloth\llama.cpp\build\bin\Release\llama-server.exe` |
| Unsloth-Python | `%USERPROFILE%\.unsloth\studio\unsloth_studio\Scripts\python.exe` |
| Native Prozessdaten und Logs | `%USERPROFILE%\.go-winui\native-runtime` |
| Stackdaten (`DataRoot`) | `%PROGRAMDATA%\GO-AI-Stack` |
| Sprach- und Bildmodelle (`ModelRoot`) | `<DataRoot>\Models` |
| Compose-Umgebung | `<DataRoot>\stack.env` |

`-NativeModelRoot` wählt bei den Stackskripten einen anderen Cacheordner. Diesen Wert beim Bauen, Starten,
Deployen und Aktualisieren konsistent übergeben. `-ModelRoot` bezeichnet bei diesen Skripten die separaten
Worker-Modelle. Beim nativen Manager bezeichnet `-ModelRoot` hingegen die GGUF-Ablage; dort ist
`-NativeModelRoot` als Alias verfügbar.

Der Manager heißt aus Kompatibilitätsgründen weiterhin `manage-coding-llama.ps1` und verwaltet alle vier
Modellrollen. Textmodelle können unabhängig im Dropdown **General AI Modell** und **Coding AI Modell**
gewählt werden. Vision benötigt ein passendes `mmproj`-GGUF; Embedding-Modelle werden als eigene Rolle erkannt.
Die nativen Kontextfenster werden aus den GGUF-Metadaten ermittelt und an den verfügbaren GPU-Speicher angepasst.

## Vorhandene Modelle übernehmen

`migrate-local-models-to-unsloth.ps1` übernimmt vorhandene, im
[Modellmanifest](../deploy/go-ai/models.manifest.json) verzeichnete Dateien in die Unsloth-Ablage. Der Quellordner
muss die Manifeststruktur `<Herausgeber>\<Repository>\<Datei>` enthalten. Quelle und Ziel müssen auf demselben
Windows-Laufwerk liegen; symbolische Verknüpfungen und andere Reparse Points werden abgewiesen.

Zunächst den vollständigen Prüfplan erzeugen; dabei bleiben die Modelldateien unverändert:

```powershell
.\windows\migrate-local-models-to-unsloth.ps1 -SourceRoot 'C:\AI\Modelle'
```

Das Skript prüft Dateigrößen, SHA-256 und NTFS-Dateiidentitäten und schreibt standardmäßig den Bericht
`artifacts\coding-validation\model-migration.json`. Unbekannte Dateien, abweichende Hashes oder vorhandene
Zieldateien brechen die Prüfung vor dem ersten Verschieben ab. Prozesse, die Quelldateien geöffnet halten,
müssen vor dem Verschieben beendet sein.

Den geprüften Bestand anschließend übernehmen:

```powershell
.\windows\migrate-local-models-to-unsloth.ps1 -SourceRoot 'C:\AI\Modelle' -Apply
```

`-DestinationRoot`, `-ManifestPath` und `-ReportPath` überschreiben die jeweiligen Standards. Die Übernahme
verschiebt Dateien innerhalb des Laufwerks ohne erneuten Download oder Überschreiben. Die Ablage folgt
`models--<Herausgeber>--<Repository>\snapshots\<Revision>\<Datei>`; `refs\main` enthält die genaue Revision ohne
Zeilenumbruch. Bei einem Fehler versucht das Skript, bereits verschobene Dateien an ihre ursprünglichen
Pfade zurückzusetzen, und hält den Status im Bericht fest.

## Modelle herunterladen

Für einen neuen Modellbestand lädt das allgemeine Downloadskript gepinnte GGUFs sowie die separaten
Worker-Modelle und prüft deren Integrität:

```powershell
.\windows\download-ai-models.ps1
```

`-SkipWorkerModels` beschränkt den Lauf auf native GGUFs; `-SkipLlmModels` lädt nur die Worker-Modelle.
Native Downloads landen im HF-Snapshotlayout, unvollständige Downloads im Staging-Verzeichnis. Die
Snapshotreferenz wird erst veröffentlicht, wenn alle zugehörigen Dateien einschließlich Shards und
Projektoren erfolgreich geprüft wurden. Bereits vorhandene Dateien werden geprüft und weiterverwendet.

Qwen3-Coder-Next Q8_0 ist ein separater, optionaler Download von vier Shards mit zusammen rund 79 GiB:

```powershell
.\windows\download-qwen3-coder-next-q8.ps1 -PlanOnly
.\windows\download-qwen3-coder-next-q8.ps1
```

`-PlanOnly` zeigt Pfade, Größen und Prüfsummen ohne Dateisystemänderungen. Nach dem Download die
Modellliste in den GO-Einstellungen aktualisieren. Upstream-Herausgebernamen in Manifest und Cache
bezeichnen die Herkunft der GGUF-Dateien.

## Bauen, Starten und Stoppen

Der Serverbuild führt die Servertests und Compose-Prüfung aus und baut anschließend die Docker-Images:

```powershell
.\windows\build-ai-stack.ps1
```

Er startet oder aktualisiert noch keine laufenden Container. Das Deployment prüft den lokalen Modellbestand,
bereitet Stackdaten und Firewall vor und baut und startet standardmäßig den Stack:

```powershell
.\windows\deploy-ai-stack.ps1
```

Für die Firewallregel ist eine administrative PowerShell erforderlich. `-SkipBuild` und `-SkipStart`
überspringen die jeweiligen Schritte. Die Integritätsprüfung berücksichtigt vorhandene gepinnte Dateien;
es müssen nicht alle historischen Modellalternativen aus dem Manifest installiert sein. Erforderlich sind
vollständige native Modelle für General, Vision und Embedding sowie die Worker-Ressourcen.
`-LegacyDatabasePath` übernimmt bei noch fehlender Zieldatenbank eine bestehende SQLite-Datenbank und
legt eine Sicherung unter `<DataRoot>\migration-backups` an.

Für den laufenden Betrieb:

```powershell
.\windows\start-ai-stack.ps1
.\windows\manage-coding-llama.ps1 -Action Status
.\windows\smoke-ai-stack.ps1
.\windows\smoke-ai-stack.ps1 -IncludeInference
.\windows\stop-ai-stack.ps1
```

Start lädt den nativen Modellmanager und startet die Docker-Dienste. Stop beendet die Compose-Dienste
und den von GO verwalteten nativen Prozess. Die Statusabfrage prüft den Manager und den lokalen
Modellkatalog. Smoke prüft Gateway, Modellrollen, Coding-Katalog und die veröffentlichten Docker-Ports;
`-IncludeInference` führt zusätzlich einen General-Testprompt aus.

Der native Manager kann auch einzeln mit `-Action Start`, `-Action Status` oder `-Action Stop` verwendet
werden. Abweichende Installationen lassen sich dort über `-BinaryPath` und `-PythonPath` angeben.
Für geänderte Runtimepfade zuerst den verwalteten Prozess stoppen und anschließend erneut starten.

`update-ai-stack.ps1` baut die Images mit Servertests neu und ruft danach den Stackstart auf. `-Pull`
aktualisiert dabei die Basisimages. Ein bereits laufender nativer Manager wird mit seiner bisherigen
Konfiguration weiterverwendet.

## Weitere aktive Hilfsskripte

| Skript | Zweck |
|---|---|
| `remove-obsolete-artifacts.ps1` | regenerierbare Alt-Builds und frühere Modelltestausgaben entfernen; Diagnose-Traces behalten |
| `remove-legacy-ai-server-autostart.ps1` | Autostart der früheren Windows-Server-App entfernen |
| `remove-legacy-ai-server-installation.ps1` | bereits migrierte frühere Serverinstallation bereinigen; aktive Docker-Daten und Migrationsbackup erhalten |
| `common.ps1` | gemeinsame Pfad-, Cache-, Compose- und .NET-Funktionen bereitstellen |

Weitere Informationen zu Gateway, Werkzeugen und Protokoll stehen in [GO-AI-SERVER.md](../GO-AI-SERVER.md).
