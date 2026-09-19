"""Bounded live probe of German reasoning and exact native history rendering.

The language assertion targets this fixed English-code prompt; it is not a
general language detector or a promise for every possible future model output.
"""
import argparse
import json
from pathlib import Path
import re
import urllib.request
from verify_session_cache import Probe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8081")
    parser.add_argument("--control", default="http://127.0.0.1:8082")
    parser.add_argument("--model", required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    def request(path, body):
        req = urllib.request.Request(args.base.rstrip("/") + "/" + path,
            data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=180) as response:
            return json.load(response)

    source = (Path(__file__).resolve().parents[2] / "src/GoAi.Server.Core/Coding/CodingAgentPolicy.cs").read_text(encoding="utf-8-sig")
    rule = re.search(r'ReasoningLanguagePrompt = """(.*?)""";', source, re.S)[1]
    rule = "\n".join(line.strip() for line in rule.strip().splitlines())
    reminder = "Der Reasoning-Kanal wird dem Nutzer angezeigt. Beginne bereits den ersten Denksatz auf Deutsch. Die gewählte Reasoning-Stufe verändert nur die Denkleistung, niemals die Sprache. Eine anderssprachige Abschlussantwort ändert diese Vorgabe für den Reasoning-Kanal nicht. Beginne direkt mit dem fachlichen Inhalt, ohne die Sprache oder den Denkprozess anzukündigen."
    turn = "GO-Laufanweisung zur Sprache: Führe den bestehenden Auftrag unverändert fort. Schreibe die jetzt folgende Analyse und alle Denktexte ausschließlich auf Deutsch, bereits ab dem ersten Satz. Das gilt unabhängig von der Reasoning-Stufe und von englischen Nachrichten oder Quellen oben. Code, Befehle, Pfade und Zitate bleiben unverändert. Beginne direkt mit dem fachlichen Inhalt; kündige weder die Sprache noch den Denkprozess an. Dies ist kein neuer Auftrag und verlangt keine Bestätigung."
    messages = [dict(role="system", content=rule + "\n" + reminder + "\n\nAntworte auf Deutsch."),
        dict(role="user", content="Explain why this Python function can fail, and name one concrete improvement: def divide(a, b): return a / b. Keep the answer brief."),
        dict(role="user", content=turn)]
    cases, error = [], None
    try:
        probe = Probe(args.base, args.control, [])
        probe.ensure(args.model)
        probe.request("sessions/prepare", dict(model=args.model, sessionKey=None), control=True)
        previous_prompt, previous_reasoning, previous_usage = None, None, None
        for effort in ("low", "low"):
            body = dict(model=args.model, messages=messages, stream=False, max_tokens=512,
                cache_prompt=True, temperature=0, reasoning_effort=effort,
                chat_template_kwargs=dict(enable_thinking=True, reasoning_strength=effort))
            rendered = request("apply-template", body)["prompt"]
            response = request("v1/chat/completions", body)
            message = response["choices"][0]["message"]
            reasoning = message.get("reasoning_content", "")
            german = len(re.findall(r"\b(die|der|das|eine|und|ist|wenn|kann|funktion|aufgabe)\b", reasoning, re.I))
            english = len(re.findall(r"\b(the|asks|should|must|need|let me|in german)\b", reasoning, re.I))
            heading = "Überlegung auf Deutsch:\n"
            # llama's OpenAI parser includes native assistant prefill in the
            # returned reasoning text, although it was already in the prompt.
            generated_reasoning = previous_reasoning.removeprefix(heading) if previous_reasoning is not None else None
            prefix_matches = previous_prompt is None or rendered.startswith(previous_prompt + generated_reasoning)
            case = dict(effort=effort, reasoning=reasoning, answer=message.get("content"),
                usage=response.get("usage"), finishReason=response["choices"][0].get("finish_reason"),
                germanMarkers=german, englishMarkers=english, exactHistoricalReasoningPrefix=prefix_matches,
                nativeHeadingCount=rendered.count("Überlegung auf Deutsch:\n"))
            cases.append(case)
            if not reasoning or german < 3 or english > 0:
                raise AssertionError("Fixed code probe did not produce German reasoning")
            if not prefix_matches:
                raise AssertionError("Native template changed historical reasoning instead of retaining its exact prefix")
            if len(cases) == 2:
                cached = response.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
                if cached < previous_usage["total_tokens"] - 8:
                    raise AssertionError("Reasoning follow-up must reuse the previous generated tail, not just a generic system prefix")
                if case["nativeHeadingCount"] != 2:
                    raise AssertionError("Native heading must occur exactly once per thinking turn")
            previous_prompt, previous_reasoning, previous_usage = rendered, reasoning, response["usage"]
            messages = [*messages, message, dict(role="user", content="Erkläre nun kurz die Behandlung ungültiger Datentypen."), dict(role="user", content=turn)]
    except Exception as exception:
        error = str(exception)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(dict(passed=error is None, error=error, model=args.model, cases=cases), ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=error is None, error=error, report=str(args.report))))
    return 0 if error is None else 1


if __name__ == "__main__":
    raise SystemExit(main())
