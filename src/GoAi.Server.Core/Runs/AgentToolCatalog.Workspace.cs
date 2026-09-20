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
            "Starte eine im ausgewählten Workspace erstellte Anwendung zur visuellen Prüfung: relative HTML/PDF-Datei in eigenem Browserfenster oder eine kompilierte EXE. Der Prozess bleibt für die Sichtprüfung geöffnet. Anschließend image.input windows/capture und media.analyze verwenden. Keine Shellbefehle.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.ImageInput,
            "Bild analysieren, Schritt 1: Lade ein lokales Workspace-Bild (operation=file, path) oder erfasse das zur Aufgabe gehörende Anwendungsfenster. windows liefert aktuelle windowIds; capture benötigt eine solche windowId. Anschließend zwingend media.analyze mit der erhaltenen uploadId und konkreter Prüffrage aufrufen. Erst dessen Befunde rechtfertigen visuelle Änderungen; nach Änderungen erneut erfassen und vergleichen.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["file","windows","capture"]},"path":{"type":"string"},"windowId":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Blender,
            "Blender: info prüft die lokale Installation. Erstelle oder bearbeite ein bpy-Python-Skript mit coding.write/edit im gewählten Workspace; run führt dessen durch expectedSha256 bestätigten Stand mit Blender aus. Speichere .blend-Dateien und Renderbilder mit relativen Workspace-Pfaden. Vollständiger Rückkopplungsablauf: Auftrag verstehen, Szene mit Objekten, Materialien, Licht und Kamera erstellen, rendern, das tatsächliche Renderbild mit image.input und media.analyze (ausgewähltes DeepSeek-Vision-Modell) prüfen, konkrete Abweichungen erkennen, Szene korrigieren, erneut rendern und prüfen. Begrenze automatische Korrekturschleifen auf höchstens zwei. Vorhandene Projekte und .blend-Dateien nicht ungefragt überschreiben. Melde verständlich fehlendes Blender, ungültige Skripte, Prozessfehler, Zeitüberschreitungen, Abbruch, fehlende Renderbilder und Fehler der Bildanalyse; erfolgreiche Skriptausführung allein ist keine visuelle Prüfung. open öffnet eine fertige .blend in der Blender-Oberfläche.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["info","run","open"]},"path":{"type":"string"},"expectedSha256":{"type":"string"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":3600}},"required":["operation"],"additionalProperties":false}
            """)),
    ];
}
