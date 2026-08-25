# Build- und Betriebsskripte

## GO-WinUI-Client

Der vollständige Clientbuild führt Restore, Release-Build, Tests, win-x64-Single-file-Publish und Portable-Smoke aus:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

Das Ergebnis liegt unter `artifacts\portable\win-x64\GO.exe`. BricsCAD wird nur mit
`-IncludeBricsCadPlugin` oder über `build-bricscad-plugin.ps1` zusätzlich gebaut.

## Docker-basierter AI-Stack

| Skript | Zweck |
|---|---|
| `download-ai-models.ps1` | gepinnte Modelldateien herunterladen und prüfen |
| `download-qwen3-coder-next-q8.ps1` | Qwen3-Coder-Next Q8_0 revisions- und hashgeprüft für den Docker-Coding-Preset herunterladen |
| `remove-obsolete-ai-models.ps1` | alte LM-Studio-, GO-AI-Server- und gpt-oss-20b-Modellbestände nach Sicherheitsprüfung entfernen |
| `remove-obsolete-artifacts.ps1` | regenerierbare Alt-Builds und frühere Modelltestausgaben entfernen; Diagnose-Traces behalten |
| `remove-legacy-ai-server-autostart.ps1` | globale Verknüpfung und Registryanzeige der früheren Windows-Server-App entfernen |
| `remove-legacy-ai-server-installation.ps1` | migrierte Windows-Server-Installation samt Altdaten sicher entfernen; Docker-Daten und Migrationsbackup behalten |
| `build-ai-stack.ps1` | Servertests und Docker-Images bauen |
| `deploy-ai-stack.ps1` | Verzeichnisse, Modellbestand, optionale DB-Migration und Firewall vorbereiten |
| `start-ai-stack.ps1` | `docker compose up -d` |
| `stop-ai-stack.ps1` | `docker compose down` und VRAM freigeben |
| `update-ai-stack.ps1` | Images neu bauen und Stack kontrolliert aktualisieren |
| `smoke-ai-stack.ps1` | Gateway, private Dienste, Portbelegung und optional Inferenz prüfen |

Das Deployment benötigt für die Firewallregel einmalig eine administrative PowerShell. Es gibt kein Windows-
Serverartefakt und keinen LM-Studio-Katalog. Einzelheiten stehen in [GO-AI-SERVER.md](../GO-AI-SERVER.md).
