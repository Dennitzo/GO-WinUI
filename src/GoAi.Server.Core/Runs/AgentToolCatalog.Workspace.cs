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
            "Starte eine im ausgewählten Workspace erstellte Anwendung zur visuellen Prüfung: relative HTML/PDF-Datei in eigenem Browserfenster oder eine kompilierte EXE. Anschließend image.input windows/capture und media.analyze verwenden.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.ImageInput,
            "Lade ein lokales Workspace-Bild (operation=file, path) oder erfasse das zur Aufgabe gehörende Anwendungsfenster. windows liefert aktuelle windowIds; capture benötigt eine solche windowId. Anschließend media.analyze mit der erhaltenen uploadId aufrufen.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["file","windows","capture"]},"path":{"type":"string"},"windowId":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
    ];
}
