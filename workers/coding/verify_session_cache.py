"""Native integration gate: require measured cached_tokens, never infer a hit.

Run only while GO has no active generation. The full gate deliberately switches
sessions and (with --other-model) unloads/reloads models. For a real supervisor
restart, run seed-restart, restart GO's native runtime, then verify-restart using
the same --state file. Reports describe the tested layer explicitly; UI-tab and
gateway continuation behavior are covered by their separate regression tests.
"""
import argparse
import json
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class Probe:
    def __init__(self, base, control, results):
        self.base, self.control, self.results = base.rstrip("/"), control.rstrip("/"), results

    def request(self, path, body=None, control=False):
        request = urllib.request.Request((self.control if control else self.base) + "/" + path,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(path + ": HTTP " + str(error.code) + " " + error.read(4096).decode("utf-8", "replace")) from error

    def ensure(self, model):
        models = self.request("v1/models")["data"]
        if not any(item["id"] == model for item in models):
            raise RuntimeError("Selected model is not in the local runtime catalog: " + model)
        for item in models:
            if item.get("status", {}).get("value") not in ("loaded", "sleeping", "loading"):
                continue
            slots = self.request("slots?model=" + urllib.parse.quote(item["id"], safe=""))
            if any(slot.get("is_processing") for slot in slots):
                raise RuntimeError("Runtime is busy; do not run the cache gate during a user turn")
            if item["id"] != model:
                self.request("sessions/save", dict(model=item["id"], sessionKey=None), control=True)
                self.request("models/unload", dict(model=item["id"]))
                deadline = time.monotonic() + 60
                while any(candidate["id"] == item["id"] and candidate.get("status", {}).get("value") in ("loaded", "sleeping", "loading")
                          for candidate in self.request("v1/models")["data"]):
                    if time.monotonic() >= deadline:
                        raise RuntimeError("Previous model did not finish unloading")
                    time.sleep(0.1)
        self.request("models/load", dict(model=model), control=True)
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            selected = next(item for item in self.request("v1/models")["data"] if item["id"] == model)
            if selected.get("status", {}).get("value") in ("loaded", "sleeping"):
                return
            time.sleep(0.5)
        raise RuntimeError("Model did not load within five minutes")

    def turn(self, scenario, model, key, messages, minimum_cached=0, expected_prepare=None):
        preparation = self.request("sessions/prepare", dict(model=model, sessionKey=key), control=True)
        started = time.monotonic()
        response = self.request("v1/chat/completions", dict(model=model, messages=messages,
            stream=False, cache_prompt=True, temperature=0, max_tokens=64,
            chat_template_kwargs=dict(enable_thinking=False)))
        usage = response.get("usage", {})
        cached = usage.get("prompt_tokens_details", {}).get("cached_tokens")
        message = response.get("choices", [{}])[0].get("message", {})
        if message.get("role") != "assistant":
            raise RuntimeError("Native completion returned no assistant message")
        saved = self.request("sessions/save", dict(model=model, sessionKey=key), control=True)
        row = dict(scenario=scenario, model=model, prepare=preparation, save=saved,
            usage=usage, timings=response.get("timings"), elapsedSeconds=time.monotonic() - started,
            requiredCachedTokens=minimum_cached, layer="native-runtime", passed=False)
        self.results.append(row)
        if preparation.get("status") not in ("resident", "miss", "restored"):
            raise AssertionError(scenario + ": cache preparation unavailable: " + json.dumps(preparation))
        if expected_prepare and preparation.get("status") != expected_prepare:
            raise AssertionError(scenario + ": expected preparation " + expected_prepare)
        if not isinstance(cached, int) or cached < minimum_cached:
            raise AssertionError(scenario + ": insufficient measured cached_tokens: " + str(cached))
        if saved.get("status") not in ("saved", "unchanged"):
            raise AssertionError(scenario + ": native snapshot was not saved")
        row["passed"] = True
        clean = {name: message[name] for name in ("role", "content", "reasoning_content", "tool_calls") if name in message}
        return [*messages, clean], usage.get("prompt_tokens", 0)


def initial(role, nonce):
    facts = "\n".join(f"Notiz {i}: Projekt {nonce}, Abschnitt {i}, Kennwort Eiche." for i in range(48))
    return [dict(role="system", content="Antworte kurz auf Deutsch. Analysen und Denktexte ebenfalls auf Deutsch."),
            dict(role="user", content=f"Kontext für {role}:\n{facts}\nNenne ausschließlich das Kennwort.")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--other-model")
    parser.add_argument("--stage", choices=("full", "seed-restart", "verify-restart"), default="full")
    parser.add_argument("--state", type=Path)
    parser.add_argument("--runtime-state", type=Path, default=Path.home() / ".go-winui" / "native-runtime" / "runtime.json")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if args.stage != "full" and args.state is None:
        parser.error("Restart stages require --state")
    rows, error = [], None
    probe = Probe(args.base, args.control, rows)
    try:
        probe.ensure(args.model)
        if args.stage == "verify-restart":
            saved = json.loads(args.state.read_text(encoding="utf-8"))
            if saved["model"] != args.model:
                raise ValueError("Restart verification must select the seeded model")
            current_runtime = json.loads(args.runtime_state.read_text(encoding="utf-8"))
            previous_runtime = saved.get("runtimeInstance", {})
            if not previous_runtime.get("supervisorPid") or not previous_runtime.get("serverPid"):
                raise ValueError("Seed lacks native process identity; repeat seed-restart before restarting")
            if any(current_runtime.get(field) == previous_runtime[field] for field in ("supervisorPid", "serverPid")):
                raise AssertionError("Supervisor and router must both restart before verify-restart")
            for role, case in saved["cases"].items():
                probe.turn(role + "/native-restart", args.model, case["key"],
                    [*case["messages"], dict(role="user", content="Nenne das Kennwort erneut.")],
                    case["threshold"], expected_prepare="restored")
        else:
            state = dict(model=args.model, cases={})
            nonce = uuid.uuid4().hex
            for role in ("general", "coding"):
                key = "go-cache-gate/" + nonce + "/" + role + "/session-a"
                messages, tokens = probe.turn(role + "/first-prompt", args.model, key, initial(role, nonce), expected_prepare="miss")
                threshold = max(16, tokens // 2)
                if args.stage == "full":
                    messages, _ = probe.turn(role + "/follow-up", args.model, key,
                        [*messages, dict(role="user", content="Wie lautet das Kennwort?")], threshold, "resident")
                    probe.turn(role + "/other-session", args.model, key + "-other", initial(role, "different-" + nonce))
                    messages, _ = probe.turn(role + "/return-to-session", args.model, key,
                        [*messages, dict(role="user", content="Wiederhole das Kennwort.")], threshold, "restored")
                    # Fresh transport object models an app reconnect without
                    # asserting that the native process itself restarted.
                    probe = Probe(args.base, args.control, rows)
                    messages, _ = probe.turn(role + "/new-client", args.model, key,
                        [*messages, dict(role="user", content="Noch einmal das Kennwort.")], threshold, "resident")
                state["cases"][role] = dict(key=key, messages=messages, threshold=threshold)
            if args.stage == "seed-restart":
                runtime = json.loads(args.runtime_state.read_text(encoding="utf-8"))
                state["runtimeInstance"] = {field: runtime[field] for field in ("supervisorPid", "serverPid")}
                args.state.parent.mkdir(parents=True, exist_ok=True)
                args.state.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
            elif args.other_model:
                case = state["cases"]["general"]
                probe.ensure(args.other_model)
                probe.turn("general/different-model-context-replay", args.other_model, case["key"],
                    [*case["messages"], dict(role="user", content="Nenne das Kennwort.")], expected_prepare="miss")
                probe.ensure(args.model)
                probe.turn("general/return-to-original-model", args.model, case["key"],
                    [*case["messages"], dict(role="user", content="Nenne das Kennwort.")], case["threshold"], "restored")
    except Exception as exception:
        error = str(exception)
    report = dict(passed=error is None, stage=args.stage, scenarios=rows, error=error,
        modelSwitchTested=bool(args.other_model and args.stage == "full"),
        nativeRestartTested=args.stage == "verify-restart",
        note="Native KV evidence only. Different models replay content and use separate snapshots. UI and gateway tests are separate.")
    if args.stage == "verify-restart" and "previous_runtime" in locals():
        report["previousRuntime"] = previous_runtime
        report["currentRuntime"] = {field: current_runtime.get(field) for field in ("supervisorPid", "serverPid")}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=report["passed"], scenarios=len(rows), report=str(args.report), error=error)))
    return 0 if error is None else 1


if __name__ == "__main__":
    raise SystemExit(main())
