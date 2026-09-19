using GoAi.Contracts;

namespace GoAi.Server.Core.Runs;

public sealed partial class AgentToolCatalog
{
    private static IEnumerable<AgentToolSpec> WorkspaceToolSpecs() =>
    [
        Server("speech.synthesize", "Vorlesen oder Audiodatei erstellen: Synthetisiere den angegebenen Text als Audioartefakt. Verwende dies selbstständig, wenn die Aufgabe eine gesprochene Ausgabe verlangt.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":20000}},"required":["text"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Open,
            "Starte eine im ausgewählten Workspace erstellte Anwendung zur visuellen Prüfung: relative HTML/PDF-Datei in eigenem Browserfenster oder eine kompilierte EXE. Der Prozess bleibt für die Sichtprüfung geöffnet. Anschließend image.input windows/capture und media.analyze verwenden. Keine Shellbefehle; Subagenten öffnen nur Anwendungen innerhalb ihres zugewiesenen Bereichs.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.ImageInput,
            "Bild analysieren, Schritt 1: Lade ein lokales Workspace-Bild (operation=file, path) oder erfasse das zur Aufgabe gehörende Anwendungsfenster. windows liefert aktuelle windowIds; capture benötigt eine solche windowId. Anschließend zwingend media.analyze mit der erhaltenen uploadId und konkreter Prüffrage aufrufen. Erst dessen Befunde rechtfertigen visuelle Änderungen; nach Änderungen erneut erfassen und vergleichen.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["file","windows","capture"]},"path":{"type":"string"},"windowId":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Blender,
            "Blender: info prüft die lokale Installation. Erstelle oder bearbeite ein bpy-Python-Skript mit coding.write/edit im gewählten Workspace; run führt dessen durch expectedSha256 bestätigten Stand mit Blender aus. Speichere .blend-Dateien und Renderbilder mit relativen Workspace-Pfaden, lies bestehende Szenen mit bpy.data.libraries oder bpy.ops.wm.open_mainfile. Prüfe Renderbilder mit image.input und media.analyze. open öffnet eine fertige .blend in der Blender-Oberfläche. Subagent-run verwendet seine isolierte Kopie und übernimmt ausschließlich zugewiesene Dateien; open führt der Hauptagent aus.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["info","run","open"]},"path":{"type":"string"},"expectedSha256":{"type":"string"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":3600}},"required":["operation"],"additionalProperties":false}
            """)),
        Server(WorkspaceTools.DocumentAgent,
            "Dokument erstellen: Beauftrage einen eigenen Dokumenten-Agenten zum Lesen, Bearbeiten oder Erstellen von Dokumenten. task enthält Auftrag, Format und Prüfkriterien. Er nutzt document.read/create für versionierte DOCX/PDF/Markdown/Text-Artefakte und verfügbare Workspace-Werkzeuge für weitere Formate (Tabellen, Präsentationen, HTML, CSV usw.). writePaths weist ihm exklusive relative Dateien/Verzeichnisse für Workspace-Ausgaben zu. Dieser Aufruf wartet auf sein Ergebnis samt Werkzeugbelegen. Keine rekursive Delegation.",
            ToolRiskClass.LocalMutation, Parse("""
            {"type":"object","properties":{"task":{"type":"string","minLength":1,"maxLength":16000},"writePaths":{"type":"array","maxItems":32,"items":{"type":"string"}}},"required":["task"],"additionalProperties":false}
            """)),
    ];
}
