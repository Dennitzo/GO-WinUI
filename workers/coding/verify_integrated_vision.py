"""Opt-in real DeepSeek media routing check. Never loads a fallback model.

Uses a supplied Blender render and optional video, isolated gateway runs, and
records answers plus residency. Cancels only its own unfinished run on failure.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request
import uuid

from verify_run_steering import Gateway


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--video", type=Path)
    parser.add_argument("--agent-tools", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if "deepseek" not in args.model.lower():
        raise ValueError("This acceptance explicitly tests DeepSeek only")
    gateway = Gateway("http://127.0.0.1:8080", time.monotonic() + 2700)
    report = {"model": args.model, "passed": False, "checks": []}
    args.output.parent.mkdir(parents=True, exist_ok=True)

    def save():
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    try:
        status = gateway.json("GET", "/v1/models/status")
        assert any(m["id"] == args.model and m.get("supportsVision") for m in status["models"]), status
        cases = [("Blender render", args.image), ("Repeated image", args.image)]
        if args.video:
            cases.append(("Video frames", args.video))
        if args.agent_tools:
            cases.extend([("General tool", args.image), ("Coding tool", args.image)])
        for label, path in cases:
            data = path.read_bytes()
            media = "video/mp4" if path.suffix.lower() == ".mp4" else "image/png"
            created = gateway.json("POST", "/v1/uploads", {"fileName": path.name,
                "mediaType": media, "length": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            upload = created["uploadId"]
            for index, offset in enumerate(range(0, len(data), created["chunkSize"])):
                chunk = data[offset:offset + created["chunkSize"]]
                request = urllib.request.Request(gateway.base + f"/v1/uploads/{upload}/chunks/{index}",
                    data=chunk, method="PUT", headers={"Content-Type": "application/octet-stream",
                        "X-Chunk-SHA256": hashlib.sha256(chunk).hexdigest()})
                with urllib.request.urlopen(request, timeout=30) as response:
                    response.read()
            gateway.json("POST", f"/v1/uploads/{upload}/complete")
            request_body = {"uploadId": upload,
                "preferredModelId": args.model,
                "prompt": "Beschreibe auf Deutsch in zwei Sätzen das tatsächlich sichtbare 3D-Objekt, seine Farbe und den Boden. Keine Vermutungen."}
            endpoint = "/v1/media/analyze"
            if label.endswith(" tool"):
                mode = label.split()[0].lower()
                endpoint = "/v1/runs"
                request_body = {"protocolVersion": "1.0", "mode": mode,
                    "messages": [{"role": "user", "content": [{"type": "text", "text":
                        f"Analysiere mit media.analyze den bereits hochgeladenen Blender-Render {upload}. "
                        "Nenne auf Deutsch die sichtbare Farbe und Form. Verwende keine weiteren Werkzeuge und keine Dateioperationen."}]}],
                    "clientCapabilities": ["coding"] if mode == "coding" else [],
                    "preferredGeneralModelId": args.model, "preferredCodingModelId": args.model,
                    "allowedServerTools": ["media.analyze"], "reasoningEffort": "none"}
            accepted = gateway.json("POST", endpoint, request_body,
                {"Idempotency-Key": "deepseek-vision-" + uuid.uuid4().hex})
            run = accepted["runId"]
            check = {"case": label, "runId": run, "events": [], "passed": False}
            report["checks"].append(check)
            save()
            print(f"{label}: {run}", flush=True)
            terminal = False
            try:
                for event in gateway.events(accepted["eventsUrl"]):
                    check["events"].append(event)
                    save()
                    assert event["type"] != "client_tool.proposed", "Unexpected local tool in vision-only probe"
                    if event["type"] in {"run.completed", "run.failed", "run.cancelled"}:
                        terminal = True
                        assert event["type"] == "run.completed", event
                results = [e["data"]["result"] for e in check["events"]
                    if e["type"] == "server_tool.completed" and e["data"].get("tool") == "media.analyze"]
                result = results[-1]
                assert result["visionModelId"] == args.model and result["modelId"] == args.model, result
                answer = result["analysis"].lower()
                assert "blau" in answer and ("würfel" in answer or "wuerfel" in answer or "kubus" in answer), result
                status = gateway.json("GET", "/v1/models/status")
                loaded = sorted({m["id"] for m in status["models"] if m["loaded"]})
                check["loadedModels"] = loaded
                assert loaded == [args.model], loaded
                check["passed"] = True
                print(f"{label}: PASSED; only {args.model} loaded", flush=True)
            finally:
                if not terminal:
                    gateway.json("POST", f"/v1/runs/{run}/cancel", cleanup=True)
                save()
        report["passed"] = True
    finally:
        save()


if __name__ == "__main__":
    main()
