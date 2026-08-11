# Einheitlicher Windows-Build

Öffentlicher Einstiegspunkt ist `windows\build.ps1`. Der Standardbuild stellt
wieder her, baut in Release, führt Tests aus, veröffentlicht den win-x64-
Single-file-Build und führt die Smoke-Checks aus. BricsCAD wird dafür nicht
benötigt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

Das primäre Artefakt liegt unter `artifacts\portable\win-x64\GO.exe`. Der
Single-file-Flavor extrahiert WinUI-, Web- und native SQLite-Inhalte beim Start;
`-SkipPublish` überspringt Veröffentlichung und Smoke-Test. Ein zusätzlicher
self-contained Ordnerbuild kann direkt über `windows\publish.ps1 -Mode Folder`
erzeugt werden.

Das BricsCAD-V26-Plugin ist ein separates Artefakt und wird nur explizit gebaut:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build-bricscad-plugin.ps1
```

Alternativ aktiviert `build.ps1 -IncludeBricsCadPlugin` diesen Zusatzschritt. Eine
Installation kann mit `-BricsCadInstallDir` oder `BRICSCAD_V26_DIR` angegeben
werden. `GOBricsCad.dll` und ihre GO-Protokoll-DLL liegen anschließend getrennt von
der App unter `artifacts\windows\bricscad-v26` und zusätzlich als ZIP vor.
