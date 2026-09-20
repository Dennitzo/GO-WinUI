"""A dedicated, persistent Blender preview process for staged revisions.

blender --factory-startup --disable-autoexec --python go_blender_preview.py -- request.json

Only this process's scene is changed. State/status files belong to one session;
unsaved user edits block every subsequent revision load, including the first.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import uuid

sys.dont_write_bytecode = True
POLL_SECONDS = 0.5
MAX_INSTANCES = 10000


def _inside_workspace(value, workspace, name, suffix=None, must_exist=False):
    if not isinstance(value, str) or not Path(value).is_absolute():
        raise ValueError(f"{name} must be an absolute workspace path")
    path = Path(value).resolve()
    if path == workspace or not path.is_relative_to(workspace):
        raise ValueError(f"{name} must remain inside the preview workspace")
    if suffix and path.suffix.lower() != suffix:
        raise ValueError(f"{name} must end in {suffix}")
    if must_exist and not path.is_file():
        raise FileNotFoundError(path)
    return path


def _file_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".go-preview-", suffix=".json", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _orient_view():
    """Change viewport UI only; do not edit cameras, geometry, materials or files."""
    import bpy
    from mathutils import Vector
    points = []
    graph = bpy.context.evaluated_depsgraph_get()
    for index, instance in enumerate(graph.object_instances):
        if index >= MAX_INSTANCES:
            raise ValueError("Preview framing exceeds the instance budget")
        obj = instance.object
        if obj.type not in {"MESH", "CURVE", "SURFACE", "FONT", "META"}:
            continue
        if obj.hide_render or obj.get("go_environment", False) or (instance.parent and instance.parent.hide_render):
            continue
        for corner in obj.bound_box:
            point = instance.matrix_world @ Vector(corner)
            if not all(math.isfinite(value) for value in point):
                raise ValueError("Preview geometry has non-finite bounds")
            points.append(point)
    if not points:
        return "The revision is open; there is no visible geometry to frame yet."
    lower = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    upper = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    center = (lower + upper) * 0.5
    extent = max((upper - lower).length, 0.1)
    rotation = Vector((-1.2, 1.6, -1.1)).to_track_quat("-Z", "Y")
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type != "VIEW_3D":
                continue
            space = area.spaces.active
            space.shading.type = "SOLID"
            space.shading.color_type = "MATERIAL"
            space.clip_start = max(extent / 10000, 0.00001)
            space.clip_end = max(extent * 100, 100)
            if space.region_3d is not None:
                space.region_3d.view_location = center
                space.region_3d.view_distance = extent * 1.3
                space.region_3d.view_rotation = rotation
                space.region_3d.view_perspective = "PERSP"
            area.tag_redraw()
    return None


class PreviewSession:
    """State machine also exercised directly by the headless deterministic smoke."""

    def __init__(self, request):
        if not isinstance(request, dict):
            raise ValueError("Preview request must be a JSON object")
        workspace_value = request.get("workspace")
        if not isinstance(workspace_value, str) or not Path(workspace_value).is_absolute():
            raise ValueError("workspace must be an absolute directory")
        self.workspace = Path(workspace_value).resolve()
        if not self.workspace.is_dir():
            raise ValueError("workspace must exist")
        self.state_path = _inside_workspace(request.get("statePath"), self.workspace, "statePath", ".json")
        self.status_path = _inside_workspace(request.get("statusPath"), self.workspace, "statusPath", ".json")
        if self.state_path == self.status_path:
            raise ValueError("State and status paths must be different")
        self.token = request.get("sessionToken")
        if not isinstance(self.token, str) or not 1 <= len(self.token) <= 512:
            raise ValueError("sessionToken must be a nonempty session identifier")
        self.revision = 0
        self.blend_path = None
        self.expected_hash = None
        self._last_status = None
        self.initial_path = None
        self._missing_checks = 0
        self._framing_error = None
        self._framing_note = None

    def _status(self, revision, state, message, blend_path=None):
        value = {"sessionToken": self.token, "revision": revision, "state": state,
                 "message": message, "blendPath": str(blend_path) if blend_path is not None else None}
        if value != self._last_status:
            _atomic_json(self.status_path, value)
            self._last_status = value

    def initialize_empty(self):
        """Create a private clean baseline, so dirty detection works from stage 1."""
        import bpy
        bpy.ops.wm.read_factory_settings(use_empty=True)
        # CLI Python runs before Blender's startup splash decision. Disable both
        # the splash/Quick Setup and preference auto-saving in this process only;
        # never write userpref.blend or change another Blender window's settings.
        bpy.context.preferences.use_preferences_save = False
        bpy.context.preferences.view.show_splash = False
        bpy.context.preferences.filepaths.use_scripts_auto_execute = False
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        self.initial_path = _inside_workspace(
            str(self.state_path.parent / f".preview-empty-{uuid.uuid4().hex}.blend"),
            self.workspace, "initialPath", ".blend")
        with self.initial_path.open("xb"):
            pass
        previous_versions = bpy.context.preferences.filepaths.save_version
        try:
            bpy.context.preferences.filepaths.save_version = 0
            result = bpy.ops.wm.save_as_mainfile(filepath=str(self.initial_path), check_existing=False)
            if "FINISHED" not in result or self.initial_path.stat().st_size == 0:
                raise RuntimeError("Could not establish a clean private preview baseline")
        finally:
            bpy.context.preferences.filepaths.save_version = previous_versions
        self._status(0, "ready", "Neues Blender-Projekt: Vorschau wartet auf die erste Etappe.")

    def tick(self):
        import bpy
        requested_revision, requested_path = self.revision, self.blend_path
        try:
            if not self.workspace.is_dir() or not self.state_path.exists():
                self._missing_checks += 1
                if self._missing_checks >= 20 and not bpy.data.is_dirty:
                    # The host removed this session/workspace, e.g. test cleanup.
                    # Never recreate the deleted directory or close unsaved work.
                    bpy.ops.wm.quit_blender()
                    return None
                return POLL_SECONDS
            self._missing_checks = 0
            if self.state_path.stat().st_size > 64 * 1024:
                raise ValueError("Preview state exceeds 64 KiB")
            state = json.loads(self.state_path.read_text(encoding="utf-8-sig"))
            if not isinstance(state, dict) or state.get("sessionToken") != self.token:
                raise ValueError("Preview state belongs to a different session")
            revision = state.get("revision")
            if isinstance(revision, bool) or not isinstance(revision, int) or revision < 0:
                raise ValueError("revision must be a nonnegative integer")
            requested_revision = revision
            if revision < self.revision:
                raise ValueError("Preview revision must not move backwards")
            label = state.get("label", "Blender revision")
            if not isinstance(label, str) or len(label) > 512:
                raise ValueError("Preview label must be text of at most 512 characters")
            if revision == 0 and state.get("blendPath") is None and state.get("expectedSha256") is None:
                if bpy.data.is_dirty:
                    self._status(0, "blocked", "Vorschau enthält ungespeicherte Änderungen. Speichern oder verwerfen, bevor eine Etappe geladen wird.")
                else:
                    self._status(0, "ready", f"{label}: Vorschau wartet auf die erste Etappe.")
                return POLL_SECONDS
            requested_path = _inside_workspace(state.get("blendPath"), self.workspace, "blendPath", ".blend", True)
            expected = state.get("expectedSha256")
            if not isinstance(expected, str) or re.fullmatch(r"[0-9a-fA-F]{64}", expected) is None:
                raise ValueError("expectedSha256 must contain exactly 64 hexadecimal characters")
            expected = expected.lower()
            if revision == self.revision and (requested_path != self.blend_path or expected != self.expected_hash):
                raise ValueError("A loaded preview revision cannot be silently replaced")
            if bpy.data.is_dirty:
                self._status(revision, "blocked", "Vorschau enthält ungespeicherte Benutzeränderungen; die neue Etappe wurde nicht geladen. Speichern oder verwerfen, um fortzufahren.", requested_path)
                return POLL_SECONDS
            if revision == self.revision:
                if self._framing_error is not None:
                    self._status(revision, "error", self._framing_error, requested_path)
                else:
                    message = f"{label}: Etappe ist in Blender sichtbar."
                    if self._framing_note:
                        message += " " + self._framing_note
                    self._status(revision, "ready", message, requested_path)
                return POLL_SECONDS
            if _file_hash(requested_path) != expected:
                raise ValueError("Preview revision SHA-256 differs from the confirmed file")
            bpy.context.preferences.filepaths.use_scripts_auto_execute = False
            result = bpy.ops.wm.open_mainfile(filepath=str(requested_path), load_ui=False, use_scripts=False)
            if "FINISHED" not in result:
                raise RuntimeError("Blender did not finish loading the preview revision")
            self.revision, self.blend_path, self.expected_hash = revision, requested_path, expected
            self._framing_error, self._framing_note = None, None
            try:
                self._framing_note = _orient_view()
            except Exception as error:
                # The file is already loaded. Retain its failed framing outcome
                # instead of reloading it or claiming readiness on the next poll.
                self._framing_error = f"Revision loaded, but viewport framing failed: {type(error).__name__}: {error}"[:2400]
                self._status(revision, "error", self._framing_error, requested_path)
                return POLL_SECONDS
            message = f"{label}: Etappe ist in Blender sichtbar."
            if self._framing_note:
                message += " " + self._framing_note
            self._status(revision, "ready", message, requested_path)
        except Exception as error:
            try:
                self._status(requested_revision, "error", f"{type(error).__name__}: {error}"[:2400], requested_path)
            except OSError as status_error:
                # A temporarily unavailable status directory must not unregister
                # the persistent Blender timer. The host will observe its timeout.
                print(f"GO Blender preview: status write failed: {status_error}", flush=True)
        return POLL_SECONDS

    def start(self):
        import bpy
        self.initialize_empty()
        # The persistent callback survives wm.open_mainfile. No other Blender
        # process or pre-existing user window is discovered or controlled.
        bpy.app.timers.register(self.tick, first_interval=0.1, persistent=True)


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path)
    args = parser.parse_args(arguments)
    request = json.loads(args.request.read_text(encoding="utf-8-sig"))
    session = PreviewSession(request)
    session.start()


if __name__ == "__main__":
    main()
