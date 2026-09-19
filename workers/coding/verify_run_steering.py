"""Live Gateway/SSE steering probe; run only after the coordinating agent releases it.

Example: python workers/coding/verify_run_steering.py --mode both --scenario natural
Uses the real gateway/model, one fresh session and exactly one POST /runs per mode.
No client tool is executed; an unexpected tool request fails the test. A failed
probe cancels only its own run. Reports include the complete received event log.
Python standard library only. Importing this module sends no requests.
"""

import argparse
import json
from pathlib import Path
import socket
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


MODEL = "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896"
TERMINAL = {"completed", "failed", "cancelled", "interrupted"}


class Gateway:
    def __init__(self, base, deadline):
        self.base = base.rstrip("/")
        self.deadline = deadline
        self.requests = []

    def open(self, method, path, body=None, headers=None, cleanup=False):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0 and not cleanup:
            raise TimeoutError("Live probe deadline exceeded")
        row = {"method": method, "path": path, "at": time.time()}
        self.requests.append(row)
        payload = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=payload, method=method,
            headers={"Content-Type": "application/json", **(headers or {})})
        try:
            response = urllib.request.urlopen(request, timeout=10 if cleanup else max(1, min(30, remaining)))
            row["status"] = response.status
            return response
        except urllib.error.HTTPError as error:
            row["status"] = error.code
            row["error"] = error.read().decode("utf-8", "replace")
            raise RuntimeError(f"{method} {path}: HTTP {error.code}: {row['error']}") from error

    def json(self, method, path, body=None, headers=None, cleanup=False):
        with self.open(method, path, body, headers, cleanup) as response:
            raw = response.read()
            return json.loads(raw) if raw else None

    def events(self, path, cursor=0):
        while time.monotonic() < self.deadline:
            try:
                with self.open("GET", path, headers={"Accept": "text/event-stream", "Last-Event-ID": str(cursor)}) as response:
                    data = []
                    for raw in response:
                        if time.monotonic() >= self.deadline:
                            raise TimeoutError("SSE probe deadline exceeded")
                        line = raw.decode("utf-8").rstrip("\r\n")
                        if line.startswith("data:"):
                            data.append(line[5:].lstrip(" "))
                        elif not line and data:
                            event = json.loads("\n".join(data))
                            data = []
                            if int(event["id"]) <= cursor:
                                continue
                            cursor = int(event["id"])
                            yield event
                    if data:
                        raise ValueError("SSE ended with an incomplete event")
                    return  # Gateway closes a terminal stream after durable trailing events.
            except (socket.timeout, TimeoutError, urllib.error.URLError, ConnectionError) as error:
                if time.monotonic() >= self.deadline:
                    raise TimeoutError("SSE deadline exceeded during reconnect") from error
                # The durable cursor prevents transport retries from duplicating receipts.
                time.sleep(0.25)
        raise TimeoutError("SSE deadline exceeded")


def actual_generation(event):
    data = event.get("data", {})
    if data.get("agentId"):
        return False
    if event["type"] in {"reasoning.delta", "text.delta"}:
        return bool(data.get("delta"))
    return False  # Token/prefill counters alone do not prove generated reasoning.


def steering_text(scenario, marker):
    if scenario == "priority":
        return ("Neue verbindliche Priorität für denselben Auftrag: Beende jetzt den bisherigen langen Entwurf. "
                "Benutze keinerlei Werkzeuge. Antworte ab jetzt ausschließlich mit dieser exakten Zeichenfolge "
                f"ohne Anführungszeichen oder Erklärung: {marker}")
    return ("Ich habe mich anders entschieden. Beende bitte die bisherige Erklärung. "
            "Benutze weiterhin keine Werkzeuge. Antworte jetzt nur noch mit genau diesem Text "
            f"ohne Anführungszeichen oder Erklärung: {marker}")


def answer_text(events):
    answer = ""
    for event in events:
        data = event.get("data", {})
        if event["type"] != "text.delta" or data.get("agentId"):
            continue
        if data.get("replaceFrom") is not None:
            # Contract offsets count UTF-16 code units, including surrogate pairs.
            answer = answer.encode("utf-16-le")[:int(data["replaceFrom"]) * 2].decode("utf-16-le")
        answer += data.get("delta", "")
    return answer


def require(condition, description):
    if not condition:
        raise AssertionError(description)


def run_mode(args, mode):
    started = time.monotonic()
    gateway = Gateway(args.base, started + args.timeout)
    nonce = uuid.uuid4().hex
    marker = "UMLENKUNG_" + mode.upper() + "_" + nonce[:16].upper()
    session = "live-steering-" + mode + "-" + nonce
    steering = {"sessionId": session, "inputId": str(uuid.uuid4()),
        "text": steering_text(args.scenario, marker)}
    report = {"mode": mode, "scenario": args.scenario, "sessionId": session, "requestedModel": args.model, "reasoningEffort": "low",
        "marker": marker, "inputId": steering["inputId"], "passed": False, "events": [],
        "requests": gateway.requests, "checks": {}, "error": None,
        "status": "running",
        "scope": "Real gateway/native model; no desktop UI, no tools, no model or process restart."}
    args.output.mkdir(parents=True, exist_ok=True)
    target = args.output / ("steering-live-" + mode + "-" + args.scenario + "-" + nonce[:12] + ".json")

    def checkpoint(stage=None):
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if stage:
            print(json.dumps({"mode": mode, "stage": stage, "runId": report.get("runId"),
                "elapsedSeconds": report["elapsedSeconds"], "report": str(target.resolve())}), flush=True)

    run_id = None
    workspace = None
    snapshot = None
    try:
        capabilities = gateway.json("GET", "/v1/capabilities")
        require(capabilities.get("supportsRunSteering") is True, "Gateway does not advertise run steering")
        initial = (
            "Reine textuelle Erklärung auf Deutsch, keine Werkzeuge, keine Dateien lesen oder ändern, "
            "kein Terminal und keine Delegation. Alle benötigten Informationen stehen hier. "
            "Erstelle einen ausführlichen Lehrtext mit 24 nummerierten Abschnitten und jeweils mindestens "
            "vier Sätzen über einen fiktiven CSV-Importer: Einlesen, Spaltenerkennung, UTF-8, Validierung, "
            "Fehlerberichte und schrittweises Testen. Erkläre nur konzeptionell, ohne ein echtes Projekt "
            "zu untersuchen. Beginne mit einer kurzen Einleitung und arbeite die Abschnitte der Reihe nach aus.")
        body = {"protocolVersion": "1.0", "mode": mode, "sessionId": session,
            "messages": [{"role": "user", "content": [{"type": "text", "text": initial}]}],
            "clientCapabilities": ["coding"] if mode == "coding" else [], "allowedServerTools": [],
            "preferredGeneralModelId": args.model, "preferredCodingModelId": args.model,
            "reasoningEffort": "low", "limits": {"maximumOutputTokens": 4096, "timeoutSeconds": args.timeout}}
        if mode == "coding":
            workspace = Path(tempfile.mkdtemp(prefix="go-steering-probe-"))
            body["codingOptions"] = {"useWorkingState": False, "reasoningPolicy": "adaptive",
                "workspacePath": str(workspace), "continueSessionContext": False}
            report["fixtureWorkspace"] = str(workspace)
        report["initialRequest"] = body
        created = gateway.json("POST", "/v1/runs", body, {"Idempotency-Key": "probe-" + nonce})
        report["creationReceipt"] = created
        run_id = created["runId"]
        report["runId"] = run_id
        checkpoint("created")
        path = "/v1/runs/" + urllib.parse.quote(run_id, safe="")
        events_path = path + "/events"
        accepted = None
        initial_reasoning = ""
        applied_offset = None
        for event in gateway.events(events_path):
            report["events"].append(event)
            data = event.get("data", {})
            if len(report["events"]) % 100 == 0:
                checkpoint()
            if event["type"] == "run.steering.applied":
                applied_offset = int(data["visibleTextOffset"])
                checkpoint("applied")
            require(event["runId"] == run_id, "SSE switched to a different run")
            require(event["type"] not in {"client_tool.proposed", "server_tool.started"},
                "Unexpected tool request: the probe never executes tools")
            if accepted is None and event["type"] == "reasoning.delta" and not data.get("agentId"):
                if data.get("replaceFrom") is not None:
                    initial_reasoning = initial_reasoning.encode("utf-16-le")[:int(data["replaceFrom"]) * 2].decode("utf-16-le")
                initial_reasoning += data.get("delta", "")
            # A template may emit its fixed thinking heading before any actual
            # model token. Wait for substantive reasoning or real answer text.
            ready = actual_generation(event) and (event["type"] == "text.delta" or len(initial_reasoning.strip()) >= 64)
            if accepted is None and ready:
                report["triggerEvent"] = event
                report["reasoningCharactersBeforeSteer"] = len(initial_reasoning)
                snapshot = gateway.json("GET", path)
                report["snapshotBeforeSteer"] = snapshot
                require(snapshot["state"] not in TERMINAL, "Generation finished before steering could be sent")
                accepted = gateway.json("POST", path + "/steer", steering)
                report["steeringRequest"] = steering
                report["acceptedReceipt"] = accepted
                require(accepted["runId"] == run_id and accepted["inputId"] == steering["inputId"], "Wrong steering receipt identity")
                require(accepted["duplicate"] is False, "First steering submission unexpectedly duplicate")
                duplicate = gateway.json("POST", path + "/steer", steering)
                report["immediateDuplicateReceipt"] = duplicate
                require(duplicate["duplicate"] is True and duplicate["sequence"] == accepted["sequence"]
                    and duplicate["runId"] == run_id, "Immediate retry is not idempotent")
                checkpoint("steering-accepted-and-duplicate-confirmed")
            if applied_offset is not None and event["type"] == "text.delta" and not data.get("agentId"):
                current_answer = answer_text(report["events"])
                suffix = current_answer.encode("utf-16-le")[applied_offset * 2:].decode("utf-16-le")
                require(len(suffix.strip()) <= len(marker) + 64,
                    "Model continued a longer answer after steering instead of the requested exact marker")
        require(accepted is not None, "No actual reasoning/text/generation event was observed")
        snapshot = gateway.json("GET", path)
        report["finalSnapshot"] = snapshot
        require(snapshot["runId"] == run_id and snapshot["state"] == "completed", "Steered run did not complete successfully")
        terminal_duplicate = gateway.json("POST", path + "/steer", steering)
        report["terminalDuplicateReceipt"] = terminal_duplicate
        require(terminal_duplicate["duplicate"] is True and terminal_duplicate["sequence"] == accepted["sequence"]
            and terminal_duplicate["runId"] == run_id and terminal_duplicate["state"] == "applied",
            "Terminal retry did not return the existing applied receipt")
        replay = list(gateway.events(events_path))
        report["replayedEventIds"] = [event["id"] for event in replay]
        events = report["events"]
        receipts = lambda name: [event for event in replay if event["type"] == name
            and event.get("data", {}).get("inputId") == steering["inputId"]]
        applied = receipts("run.steering.applied")
        answer = answer_text(replay)
        report["answer"] = answer
        report["checks"] = {
            "oneRunCreationRequest": sum(row["method"] == "POST" and row["path"] == "/v1/runs" for row in gateway.requests) == 1,
            "sameRunIdThroughout": all(event["runId"] == run_id for event in replay),
            "oneRunStarted": sum(event["type"] == "run.started" for event in replay) == 1,
            "oneAcceptedEvent": len(receipts("run.steering.accepted")) == 1,
            "oneAppliedEvent": len(applied) == 1,
            "appliedAfterGeneration": len(applied) == 1 and applied[0]["id"] > report["triggerEvent"]["id"],
            "markerInVisibleAnswer": marker in answer,
            "markerAfterAppliedOffset": len(applied) == 1 and marker in answer.encode("utf-16-le")[int(applied[0]["data"]["visibleTextOffset"]) * 2:].decode("utf-16-le"),
            "exactAnswerAfterAppliedOffset": len(applied) == 1 and marker == answer.encode("utf-16-le")[int(applied[0]["data"]["visibleTextOffset"]) * 2:].decode("utf-16-le").strip(),
            "samePersistedEventsAfterDuplicate": [event["id"] for event in events] == [event["id"] for event in replay],
            "requestedModelUsed": snapshot.get("selectedModel") == args.model,
            "noFailureOrCancel": not any(event["type"] in {"run.failed", "run.cancelled"} for event in replay),
            "noToolsDispatched": not any(event["type"] in {"client_tool.proposed", "server_tool.started"} for event in replay),
        }
        require(all(report["checks"].values()), "Failed checks: " + ", ".join(key for key, ok in report["checks"].items() if not ok))
        report["passed"] = True
        report["status"] = "completed"
    except (Exception, KeyboardInterrupt) as error:
        report["status"] = "failed"
        report["error"] = f"{type(error).__name__}: {error}"
        if run_id and (not snapshot or snapshot.get("state") not in TERMINAL):
            try:
                gateway.json("POST", "/v1/runs/" + urllib.parse.quote(run_id, safe="") + "/cancel", cleanup=True)
                report["failedProbeOwnRunCancelled"] = True
            except Exception as cleanup_error:
                report["cleanupError"] = str(cleanup_error)
    finally:
        report.setdefault("answer", answer_text(report["events"]))
        if workspace:
            try:
                workspace.rmdir()  # Only the empty probe directory; never recursive deletion.
            except OSError as cleanup_error:
                report["workspaceCleanupError"] = str(cleanup_error)
        checkpoint()
        print(json.dumps({"mode": mode, "passed": report["passed"], "runId": run_id,
            "report": str(target.resolve()), "error": report["error"]}, ensure_ascii=False), flush=True)
    return report["passed"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default=MODEL)
    parser.add_argument("--mode", choices=("general", "coding", "both"), default="both")
    parser.add_argument("--scenario", choices=("natural", "priority"), default="natural",
        help="Natural user correction or the stricter priority-worded diagnostic; both require the exact marker")
    parser.add_argument("--timeout", type=int, default=600, help="Per-mode total deadline in seconds")
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[2] / "artifacts" / "validation")
    args = parser.parse_args()
    if args.timeout < 30:
        parser.error("--timeout must be at least 30 seconds")
    modes = ("general", "coding") if args.mode == "both" else (args.mode,)
    for mode in modes:
        if not run_mode(args, mode):
            return 1  # Preserve the worker for diagnosis; do not start another mode after failure.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
