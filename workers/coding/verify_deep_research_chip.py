"""Opt-in live Deep Research gate for the chip's General and Coding requests.

Run only after the coordinating agent releases the shared gateway. Uses genuine
model inference and SearXNG/search/fetch, isolated sessions and a temporary Coding
workspace. No client tool is executed and no service or model is restarted.
Importing this module performs no requests. Records full SSE and a JSON report.
"""
import argparse
import json
from pathlib import Path
import re
import tempfile
import time
import traceback
import urllib.parse
import uuid

from verify_run_steering import Gateway, answer_text
from verify_stop_session_cache import select_reasoning, run_failure


PROMPT = (
    "Recherchiere auf Deutsch knapp den Unterschied zwischen Python pathlib.Path.exists() "
    "und Path.is_file(): Was liefern beide für eine vorhandene Datei, ein vorhandenes "
    "Verzeichnis und einen nicht existierenden Pfad? Prüfe zwei offizielle Quellen, "
    "bevorzugt die Python-Dokumentation zu pathlib und os.path, durch Websuche und Abruf. "
    "Prüfe diese vom Nutzer angegebenen Originalquellen: https://docs.python.org/3/library/pathlib.html "
    "und https://docs.python.org/3/library/os.path.html . "
    "Antworte in höchstens 180 Wörtern mit einer kleinen Vergleichstabelle und zwei "
    "anklickbaren Quellenlinks. Keine lokalen Dateien, keine Terminalbefehle und keine "
    "Client-Werkzeuge verwenden. Erfinde keine erfolgreichen Abrufe."
)
TOOLS = {"web.search", "web.fetch", "web.deepResearch"}
WORKING_STATE_TOOLS = {"coding.updatePlan", "coding.readOutput", "coding.searchRunEvidence"}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def canonical_url(value):
    if not isinstance(value, str):
        return None
    parsed = urllib.parse.urlsplit(value.rstrip(".,);]"))
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        return None
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc.lower(), parsed.path.rstrip("/"), parsed.query, ""))


def verify_case(case, model):
    events = case["events"]
    require(case["snapshot"]["state"] == "completed", run_failure(case["snapshot"], events))
    completed = [item["data"] for item in events if item["type"] == "server_tool.completed"]
    searches = [item for item in completed if item.get("tool") == "web.search"
                and item.get("success") is not False and item.get("result", {}).get("results")]
    require(searches, "Keine erfolgreiche echte SearXNG-Suche mit Treffern protokolliert")
    for item in searches:
        result = item["result"]
        require(result.get("provider") == "searxng" and result.get("isFallback") is False,
                "Die erfolgreiche Suche muss provider=searxng und isFallback=false ausweisen")
    fetches = [item for item in completed if item.get("tool") == "web.fetch"
               and item.get("success") is not False and item.get("result", {}).get("success") is not False
               and item.get("result", {}).get("found") is not False]
    source_urls = {canonical_url(item.get("result", {}).get("url")) for item in fetches}
    source_urls.discard(None)
    official_sources = sorted(url for url in source_urls if urllib.parse.urlsplit(url).hostname == "docs.python.org")
    require(len(official_sources) >= 2, "Zwei unterschiedliche erfolgreiche Originalabrufe von docs.python.org fehlen")
    stages = {item["data"].get("state") for item in events if item["type"] == "model.generation"}
    if case["mode"] == "general":
        require("webResearchSearchPlanning" in stages, "General hat die geplante Recherchepipeline nicht durchlaufen")
        require("webResearchSourceSelection" in stages, "General hat keine Quellen ausgewählt")
    else:
        research = [item for item in completed if item.get("tool") == "web.deepResearch"]
        require(research, "Coding hat web.deepResearch nicht ausgeführt")
        result = research[-1].get("result", {})
        require(research[-1].get("success") is not False and result.get("success") is True,
                "Coding Deep Research meldet eine unvollständige Recherche")
        require(result.get("provider") == "searxng" and result.get("isFallback") is False,
                "Coding Deep Research nutzt nicht ausschließlich SearXNG")
        require(len(result.get("plan", [])) >= 2 and result.get("findings"),
                "Coding-Ergebnis enthält keinen Rechercheplan oder keine belegten Aussagen")
        require(len(result.get("sources", [])) >= 2, "Coding-Ergebnis enthält weniger als zwei Quellen")
    answer = answer_text(events)
    require(bool(answer.strip()) and "exists" in answer and "is_file" in answer,
            "Die fertige Antwort erklärt die beiden geprüften APIs nicht")
    citations = {canonical_url(url) for url in re.findall(r"https?://[^\s<>\"\)]+", answer)}
    citations.discard(None)
    verified_citations = sorted(citations.intersection(official_sources))
    require(len(verified_citations) >= 2, "Die Endantwort verlinkt nicht zwei tatsächlich abgerufene offizielle Quellen")
    selected = [item["data"].get("modelId") for item in events if item["type"] == "model.selected"]
    require(selected and all(value == model for value in selected), "Der Lauf verwendet nicht das ausgewählte Modell")
    case.update(answer=answer, searchCount=len(searches), fetchCount=len(fetches),
                officialSources=official_sources, verifiedCitations=verified_citations,
                researchStages=sorted(stage for stage in stages if stage), passed=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--mode", choices=("general", "coding", "both"), default="both")
    parser.add_argument("--reasoning", help="Explicit advertised reasoning effort; defaults to the model's advertised default")
    parser.add_argument("--timeout", type=int, default=900, help="Maximum seconds per mode")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 30 <= args.timeout <= 3600:
        parser.error("--timeout muss zwischen 30 und 3600 Sekunden liegen")
    report = {"passed": False, "model": args.model, "cases": [],
              "scope": "Real gateway/model research via the chip RunRequest flag; no desktop clicks or client tools."}
    args.output.parent.mkdir(parents=True, exist_ok=True)

    def save():
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    try:
        for mode in ("general", "coding") if args.mode == "both" else (args.mode,):
            started = time.monotonic()
            gateway = Gateway(args.base, started + args.timeout)
            active, workspace, terminal = None, None, False
            case = {"mode": mode, "passed": False, "events": [], "requests": gateway.requests,
                    "sessionId": "deep-research-chip-probe-" + uuid.uuid4().hex}
            report["cases"].append(case)
            sse_path = args.output.with_name(args.output.stem + "-" + mode + ".sse.jsonl")
            case["sseFile"] = str(sse_path.resolve())
            try:
                gpu = gateway.json("GET", "/v1/gpu/status")
                require(not gpu.get("activeWorkloads") and not gpu.get("queueLength", 0),
                        "Gateway ist belegt; erst den laufenden Auftrag beenden")
                models = gateway.json("GET", "/v1/models/status")["models"]
                effort = select_reasoning(models, args.model, mode)
                if args.reasoning is not None:
                    selected = next((item for item in models if item["id"] == args.model and item.get("role") == mode), None)
                    selected = selected or next(item for item in models if item["id"] == args.model)
                    supported = selected.get("reasoningEfforts") or []
                    require(args.reasoning in supported,
                            "Die gewünschte Reasoning-Stufe wird nicht vom Modell angeboten: " + args.reasoning)
                    effort = args.reasoning
                case["reasoningEffort"] = effort
                body = {"protocolVersion": "1.0", "mode": mode, "sessionId": case["sessionId"],
                        "messages": [{"role": "user", "content": [{"type": "text", "text": PROMPT}]}],
                        "clientCapabilities": ["coding"] if mode == "coding" else [],
                        "allowedServerTools": sorted(TOOLS), "deepResearch": True,
                        "preferredGeneralModelId": args.model, "preferredCodingModelId": args.model,
                        "reasoningEffort": effort,
                        "limits": {"maximumOutputTokens": 4096, "timeoutSeconds": args.timeout}}
                if mode == "coding":
                    workspace = tempfile.mkdtemp(prefix="go-deep-research-probe-")
                    body["codingOptions"] = {"workspacePath": workspace, "useWorkingState": True,
                                             "continueSessionContext": False}
                    case["workspace"] = workspace
                case["request"] = body
                receipt = gateway.json("POST", "/v1/runs", body,
                                       {"Idempotency-Key": "deep-research-probe-" + uuid.uuid4().hex})
                active = receipt["runId"]
                case["runId"] = active
                path = "/v1/runs/" + urllib.parse.quote(active, safe="")
                print(json.dumps({"mode": mode, "runId": active, "stage": "started"}), flush=True)
                save()
                with sse_path.open("w", encoding="utf-8") as stream:
                    for event in gateway.events(path + "/events"):
                        case["events"].append(event)
                        stream.write(json.dumps(event, ensure_ascii=False) + "\n")
                        stream.flush()
                        require(event.get("runId") == active, "SSE hat einen fremden Lauf geliefert")
                        require(event["type"] != "client_tool.proposed", "Unerwartetes Client-Werkzeug; wird nicht ausgeführt")
                        if event["type"] == "server_tool.started":
                            tool = event.get("data", {}).get("tool")
                            require(tool in TOOLS or mode == "coding" and tool in WORKING_STATE_TOOLS,
                                    "Unerwartetes Serverwerkzeug: " + str(tool))
                        if event["type"] in ("run.completed", "run.failed", "run.cancelled"):
                            terminal = True
                        if event["type"] in ("server_tool.completed", "run.failed", "run.completed"):
                            save()
                case["snapshot"] = gateway.json("GET", path)
                verify_case(case, args.model)
                print(json.dumps({"mode": mode, "runId": active, "stage": "passed",
                                  "sources": case["officialSources"]}), flush=True)
            except Exception as error:
                case.update(error=str(error), traceback=traceback.format_exc(), answer=answer_text(case["events"]))
                raise
            finally:
                if active and not terminal:
                    try:
                        gateway.json("POST", "/v1/runs/" + urllib.parse.quote(active, safe="") + "/cancel", cleanup=True)
                        case["cleanupCancelledRun"] = active
                    except Exception as error:
                        case["cleanupError"] = str(error)
                if workspace:
                    try:
                        Path(workspace).rmdir()  # Only our empty fixture; never recursive cleanup.
                    except OSError as error:
                        case["workspaceRetained"] = str(error)
                case["elapsedSeconds"] = round(time.monotonic() - started, 3)
                save()
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        save()


if __name__ == "__main__":
    main()
