"""Execute one small authoring step, save a fresh revision, and inspect it.

blender --background --factory-startup --disable-autoexec --python-exit-code 1
  --python go_blender_stage.py -- request.json

The host promotes outputPath only after reading this wrapper's report. Keep the
temporary output in the final revision's directory so relative asset paths stay
valid after promotion. Inspection changes only the in-memory review state.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import runpy
import sys
import time
import traceback

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import go_blender as helpers
import go_blender_tool as inspection

MAX_SCRIPT_CHARACTERS = 12000


def _absolute_path(value, name, suffix=None, must_exist=False):
    if not isinstance(value, str) or not Path(value).is_absolute():
        raise ValueError(f"{name} must be an absolute workspace path")
    return helpers.workspace_path(value, suffix=suffix, must_exist=must_exist)


def validate_request(raw):
    if not isinstance(raw, dict):
        raise ValueError("Stage request must be a JSON object")
    script = _absolute_path(raw.get("scriptPath"), "scriptPath", ".py", True)
    output = _absolute_path(raw.get("outputPath"), "outputPath", ".blend")
    report = _absolute_path(raw.get("reportPath"), "reportPath", ".json")
    base = (_absolute_path(raw["baseScene"], "baseScene", ".blend", True)
            if raw.get("baseScene") is not None else None)
    project_value = raw.get("projectDirectory")
    if not isinstance(project_value, str) or not Path(project_value).is_absolute():
        raise ValueError("projectDirectory must be an absolute workspace directory")
    project = Path(project_value).resolve()
    if not project.is_relative_to(Path.cwd().resolve()) or not project.is_dir():
        raise ValueError("projectDirectory must be an existing directory inside the workspace")
    if output.exists():
        raise FileExistsError("outputPath must be a fresh temporary .blend revision")
    if report.exists():
        raise FileExistsError("reportPath must be fresh for this stage attempt")
    label = raw.get("label", "Blender stage")
    if not isinstance(label, str) or not label.strip() or len(label) > 512:
        raise ValueError("label must contain between 1 and 512 characters")
    return {"scriptPath": script, "baseScene": base, "outputPath": output,
            "reportPath": report, "projectDirectory": project, "label": label.strip()}


def run_request(request_path):
    import bpy
    started = time.monotonic()
    workspace = Path.cwd()
    request_file = helpers.workspace_path(request_path, suffix=".json", must_exist=True)
    request = validate_request(json.loads(request_file.read_text(encoding="utf-8-sig")))
    script, base, output = request["scriptPath"], request["baseScene"], request["outputPath"]
    source_hash = inspection._hash_file(base) if base else None
    report = {"schemaVersion": 1, "operation": "stage", "success": False,
              "valid": False, "inspectionCompleted": False, "issues": [], "objects": [],
              "counts": {}, "bounds": None, "images": [], "label": request["label"],
              "scriptPath": str(script), "scriptSha256": inspection._hash_file(script),
              "baseScene": str(base) if base else None, "baseSha256": source_hash,
              "outputPath": str(output)}
    try:
        if script.stat().st_size > MAX_SCRIPT_CHARACTERS * 4 + 3:
            raise ValueError(f"Stage script exceeds the {MAX_SCRIPT_CHARACTERS}-character budget")
        code = script.read_text(encoding="utf-8-sig")
        report["scriptCharacters"] = len(code)
        if not code.strip() or len(code) > MAX_SCRIPT_CHARACTERS:
            raise ValueError(f"Each authoring stage needs 1–{MAX_SCRIPT_CHARACTERS} characters; split larger work into further stages")
        bpy.context.preferences.filepaths.use_scripts_auto_execute = False
        if base is not None:
            result = bpy.ops.wm.open_mainfile(filepath=str(base), load_ui=False, use_scripts=False)
            if "FINISHED" not in result:
                raise RuntimeError("Blender did not finish loading the base revision")
        else:
            # A default cube is not evidence that the authoring step made geometry.
            bpy.ops.wm.read_factory_settings(use_empty=True)
            bpy.context.preferences.filepaths.use_scripts_auto_execute = False
        print(f"GO Blender stage: {request['label']}", flush=True)
        previous_path, previous_argv = list(sys.path), list(sys.argv)
        try:
            # Trusted helpers are already imported. Project-specific modelling
            # modules may be imported by the small user-authored stage script.
            sys.path.insert(0, str(script.parent))
            sys.path.insert(0, str(request["projectDirectory"]))
            sys.argv = [str(script)]
            runpy.run_path(str(script), run_name="__main__")
        finally:
            sys.path[:] = previous_path
            sys.argv[:] = previous_argv
            os.chdir(workspace)
        # Save the authoring state BEFORE inspect_evaluated aligns viewport and
        # render settings. Those diagnostic overrides must never enter the file.
        helpers.save_revision(output)
        report["success"] = True
        report["outputSha256"] = inspection._hash_file(output)
        report["outputBytes"] = output.stat().st_size
        print(f"GO Blender stage: saved {output.name}; checking geometry", flush=True)
        try:
            diagnostics = inspection.inspect_scene()
            inspection.inspect_evaluated(diagnostics)
            for key in ("valid", "validationMeaning", "diagnosticScope", "evaluationSettings",
                        "issues", "objects", "counts", "bounds", "boundsScope", "boundsUnit",
                        "units", "missingImages", "budgetExceeded"):
                if key in diagnostics:
                    report[key] = diagnostics[key]
            report["inspectionCompleted"] = True
        except Exception as error:
            inspection._issue(report["issues"], "error", "stage_inspection_failed", f"{type(error).__name__}: {error}")
            report["valid"] = False
    except BaseException as error:
        report["success"] = False
        report["valid"] = False
        inspection._issue(report["issues"], "error", "stage_failed", f"{type(error).__name__}: {error}")
        report["error"] = f"{type(error).__name__}: {error}"
        report["traceback"] = traceback.format_exc(limit=16)[-12000:]
    report["baseUnchanged"] = base is None or (base.is_file() and source_hash == inspection._hash_file(base))
    if not report["baseUnchanged"]:
        report["success"] = False
        report["valid"] = False
        inspection._issue(report["issues"], "error", "base_changed", "The source revision changed during the stage")
    report["blenderVersion"] = bpy.app.version_string
    report["elapsedSeconds"] = round(time.monotonic() - started, 3)
    inspection.atomic_report(request["reportPath"], report)
    print(json.dumps({"reportPath": str(request["reportPath"]), "success": report["success"],
                      "valid": report["valid"], "outputPath": str(output),
                      "issues": len(report["issues"])}), flush=True)
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path)
    report = run_request(parser.parse_args(arguments).request)
    if not report["success"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
