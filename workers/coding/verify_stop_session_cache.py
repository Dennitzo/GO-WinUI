"""Opt-in real gateway cache gate: Stop -> new run -> another new run.

Run only with the coordinating agent's release and an idle gateway. Creates its
own sessions, never executes client tools, cancels only its own probe runs. Cache
hits are measured from native token counters, not inferred from response speed.

For restart checks use --stage seed-restart --state FILE, restart the desired
client/gateway/native layers externally, then invoke --stage verify-restart
--state FILE. Each invocation is a separate client process. The probe does not
restart shared services itself. --require-native-restart verifies new process
IDs in the native runtime state file before testing persisted KV restoration.
"""
import argparse
import json
from pathlib import Path
import tempfile
import time
import uuid

from verify_run_steering import Gateway, answer_text


def message(role, text):
    return {"role": role, "content": [{"type": "text", "text": text}]}


def token_measurements(events):
    progress = [event["data"] for event in events if event["type"] == "model.generation"]
    metrics = [event["data"]["metrics"] for event in events if event["type"] == "coding.metrics"]
    def last(name):
        return next((row[name] for row in reversed(progress) if isinstance(row.get(name), (int, float))), None)
    return {"promptTokens": last("promptTokens"), "cachedPromptTokens": last("cachedPromptTokens"),
            "processedPromptTokens": last("processedPromptTokens"), "generatedTokens": last("generatedTokens"),
            "codingMetrics": metrics}


def select_reasoning(models, model_id, mode):
    model = next((item for item in models if item["id"] == model_id and item.get("role") == mode), None)
    model = model or next((item for item in models if item["id"] == model_id), None)
    if model is None:
        raise ValueError("Requested model is absent from the local catalog: " + model_id)
    supported = model.get("reasoningEfforts") or []
    default = model.get("defaultReasoningEffort")
    if default and (not supported or default in supported):
        return default
    return next((item for item in ("on", "high", "medium", "low", "none") if item in supported), None)


def run_failure(snapshot, events):
    failure = next((item.get("data", {}) for item in reversed(events) if item["type"] == "run.failed"), {})
    return json.dumps({"state": snapshot.get("state"), "errorCode": snapshot.get("errorCode"),
                       "failure": failure}, ensure_ascii=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--mode", choices=("general", "coding", "both"), default="both")
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stage", choices=("full", "seed-restart", "verify-restart"), default="full")
    parser.add_argument("--state", type=Path)
    parser.add_argument("--runtime-state", type=Path, default=Path.home() / ".go-winui" / "native-runtime" / "runtime.json")
    parser.add_argument("--require-native-restart", action="store_true")
    parser.add_argument("--restart-after-stop", action="store_true",
                        help="Seed only the interrupted first turn, then verify its cache after external restart")
    args = parser.parse_args()
    if args.stage != "full" and args.state is None:
        parser.error("Restart stages require --state")
    if args.require_native_restart and args.stage != "verify-restart":
        parser.error("--require-native-restart belongs to verify-restart")
    if args.restart_after_stop and args.stage != "seed-restart":
        parser.error("--restart-after-stop belongs to seed-restart")
    gateway = Gateway(args.base, time.monotonic() + args.timeout)
    report = {"passed": False, "model": args.model, "cases": [], "requests": gateway.requests,
              "stage": args.stage,
              "scope": "Real gateway/native inference; isolated sessions; no desktop UI; no tools; services restarted externally if requested."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    active = None
    def save():
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    try:
        gpu = gateway.json("GET", "/v1/gpu/status")
        if gpu.get("activeWorkloads") or gpu.get("queueLength", 0):
            raise RuntimeError("Gateway is busy; wait until the user workload is finished")
        report["initialModels"] = gateway.json("GET", "/v1/models/status")
        runtime = json.loads(args.runtime_state.read_text(encoding="utf-8-sig")) if args.runtime_state.is_file() else None
        report["runtimeInstance"] = runtime
        persisted = {"model": args.model, "runtimeInstance": runtime, "cases": {}}
        if args.stage == "verify-restart":
            persisted = json.loads(args.state.read_text(encoding="utf-8"))
            if persisted["model"] != args.model:
                raise ValueError("Restart verification must select the originally seeded model")
            before = persisted.get("runtimeInstance")
            changed = bool(runtime and before and all(runtime.get(key) != before.get(key) for key in ("supervisorPid", "serverPid")))
            report["nativeRestartObserved"] = changed
            if args.require_native_restart and not changed:
                raise AssertionError("A native supervisor and router restart was requested but both new process IDs were not observed")
        for mode in ("general", "coding") if args.mode == "both" else (args.mode,):
            session = "cache-stop-probe-" + uuid.uuid4().hex
            workspace = tempfile.mkdtemp(prefix="go-cache-stop-") if mode == "coding" else None
            facts = "\n".join(f"Prüfnotiz {i}: Die Projektkennung ist EICHE-42, Abschnitt {i} bleibt unverändert." for i in range(64))
            initial = ("Reiner Texttest: Benutze keine Werkzeuge, lies und ändere keine Dateien. "
                       "Merke dir folgende Notizen. Erkläre danach ausführlich in 24 Abschnitten, "
                       "wie ein fiktiver CSV-Importer Daten validiert.\n" + facts)
            history = [message("user", initial)]
            effort = select_reasoning(report["initialModels"]["models"], args.model, mode)
            saved_case = None
            if args.stage == "verify-restart":
                saved_case = persisted["cases"].get(mode)
                if not saved_case:
                    raise ValueError("State file has no completed seed for mode " + mode)
                session, workspace, history = saved_case["sessionId"], saved_case["workspace"], saved_case["history"]
                if effort != saved_case["reasoningEffort"]:
                    raise ValueError("Reasoning metadata changed since seed; repeat seed with current model configuration")
            case = {"mode": mode, "sessionId": session, "workspace": workspace, "turns": [], "passed": False}
            case["reasoningEffort"] = effort
            report["cases"].append(case)
            turns = (3,) if args.stage == "verify-restart" else range(1 if args.restart_after_stop else 3)
            for turn in turns:
                if turn:
                    history.append(message("user", "Der vorherige Auftrag ist beendet. Antworte jetzt ausschließlich mit der Projektkennung EICHE-42. Keine Werkzeuge."))
                request = {"protocolVersion": "1.0", "mode": mode, "sessionId": session,
                           "messages": history, "clientCapabilities": ["coding"] if mode == "coding" else [],
                           "allowedServerTools": [], "preferredGeneralModelId": args.model,
                           "preferredCodingModelId": args.model, "reasoningEffort": effort,
                           "limits": {"maximumOutputTokens": 4096, "timeoutSeconds": args.timeout}}
                if workspace:
                    request["codingOptions"] = {"workspacePath": workspace, "continueSessionContext": True,
                                                "useWorkingState": True}
                started = time.monotonic()
                receipt = gateway.json("POST", "/v1/runs", request,
                                       {"Idempotency-Key": "probe-" + uuid.uuid4().hex})
                active = receipt["runId"]
                path = "/v1/runs/" + active
                row = {"runId": active, "turn": turn, "events": [], "cancelSent": False}
                case["turns"].append(row)
                print(json.dumps({"mode": mode, "turn": turn, "runId": active, "stage": "started"}), flush=True)
                for event in gateway.events(path + "/events"):
                    row["events"].append(event)
                    if event["type"] in ("client_tool.proposed", "server_tool.started"):
                        raise AssertionError("Unexpected tool invocation; this probe executes no tools")
                    progress = token_measurements(row["events"])
                    reasoning = sum(len(item["data"].get("delta", "")) for item in row["events"] if item["type"] == "reasoning.delta")
                    if turn == 0 and not row["cancelSent"] and progress["promptTokens"] and (
                            (progress["generatedTokens"] or 0) >= 8 or reasoning >= 100):
                        gateway.json("POST", path + "/cancel")
                        row["cancelSent"] = True
                        save()
                    if len(row["events"]) % 25 == 0:
                        save()
                snapshot = gateway.json("GET", path)
                row.update(snapshot=snapshot, elapsedSeconds=time.monotonic() - started,
                           measurements=token_measurements(row["events"]), answer=answer_text(row["events"]))
                active = None
                if turn == 0:
                    if not row["cancelSent"] or snapshot["state"] != "cancelled":
                        raise AssertionError("Probe failed to interrupt genuine ongoing inference: " + run_failure(snapshot, row["events"]))
                else:
                    if snapshot["state"] != "completed" or "EICHE-42" not in row["answer"]:
                        raise AssertionError("Follow-up did not complete with the remembered project identifier: " + run_failure(snapshot, row["events"]))
                    previous_prompt = (saved_case["promptTokens"] if saved_case else case["turns"][-2]["measurements"]["promptTokens"])
                    cached = row["measurements"]["cachedPromptTokens"]
                    threshold = max(256, int((previous_prompt or 0) * 0.65))
                    row["minimumCachedTokens"] = threshold
                    if not isinstance(cached, int) or cached < threshold:
                        raise AssertionError(f"Real cache reuse below threshold: {cached} < {threshold}")
                if row["answer"].strip():
                    history.append(message("assistant", row["answer"]))
                save()
                print(json.dumps({"mode": mode, "turn": turn, "stage": "verified", "measurements": row["measurements"]}), flush=True)
            case["passed"] = True
            if args.stage in ("seed-restart", "verify-restart"):
                persisted["cases"][mode] = {"sessionId": session, "workspace": workspace,
                    "history": history, "reasoningEffort": effort,
                    "promptTokens": case["turns"][-1]["measurements"]["promptTokens"],
                    "lastRunId": case["turns"][-1]["runId"]}
                persisted["runtimeInstance"] = runtime
                args.state.parent.mkdir(parents=True, exist_ok=True)
                args.state.write_text(json.dumps(persisted, ensure_ascii=False, indent=2), encoding="utf-8")
        report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        if active:
            try:
                gateway.json("POST", "/v1/runs/" + active + "/cancel", cleanup=True)
            except Exception as error:
                report["cleanupError"] = str(error)
        save()


if __name__ == "__main__":
    main()
