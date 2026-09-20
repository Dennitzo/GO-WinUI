"""Real Blender behavioral checks; produces inspectable .blend/PNG/JSON evidence.

From the repository root:
  blender --background --factory-startup --disable-autoexec --python-exit-code 1 \
    --python examples/blender/validate_toolkit.py -- --output artifacts/blender-toolkit-check
Use a fresh output directory for each run. No source revision is overwritten.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import runpy
import shutil
import sys
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src" / "GoWinUI.App" / "Assets" / "Blender"))
import go_blender as go
import go_blender_tool as tool


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def request(output, name, blend, operation="inspect", **extra):
    directory = output / name
    value = {"operation": operation, "blendPath": str(blend), "outputDirectory": str(directory),
             "reportPath": str(directory / "report.json"), **extra}
    path = output / f"{name}.request.json"
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")
    return tool.run_request(path)


def expect_error(action, expected):
    try:
        action()
    except expected:
        return
    raise AssertionError(f"Expected {expected.__name__}")


def validate_camera_orientation():
    """A plan view must preserve the brief's X-right/Y-up convention in pixels."""
    import bpy
    from bpy_extras.object_utils import world_to_camera_view
    from mathutils import Vector
    scene = bpy.context.scene
    render = scene.render
    previous = (render.resolution_x, render.resolution_y,
                render.pixel_aspect_x, render.pixel_aspect_y)
    bounds = ((3, -9, 2), (17, 5, 6))
    center = (Vector(bounds[0]) + Vector(bounds[1])) * 0.5
    try:
        for width, height in ((768, 768), (800, 600)):
            render.resolution_x, render.resolution_y = width, height
            render.pixel_aspect_x = render.pixel_aspect_y = 1
            # Reuse a previously tilted review camera, as multi-view renders do.
            previous_camera = go.frame_camera("perspective", bounds)
            camera = go.frame_camera("top", bounds)
            bpy.context.view_layer.update()
            assert camera == previous_camera and camera.data.type == "ORTHO"
            origin = world_to_camera_view(scene, camera, center)
            east = world_to_camera_view(scene, camera, center + Vector((1, 0, 0)))
            north = world_to_camera_view(scene, camera, center + Vector((0, 1, 0)))
            assert abs(origin.x - 0.5) < 1e-5 and abs(origin.y - 0.5) < 1e-5
            assert origin.z > 0, "Model is behind the top camera"
            assert east.x > origin.x and abs(east.y - origin.y) < 1e-5, "+X must project right"
            assert north.y > origin.y and abs(north.x - origin.x) < 1e-5, "+Y must project up"
            for x in (bounds[0][0], bounds[1][0]):
                for y in (bounds[0][1], bounds[1][1]):
                    corner = world_to_camera_view(scene, camera, Vector((x, y, center.z)))
                    assert 0 < corner.x < 1 and 0 < corner.y < 1, "Plan bounds were clipped"
    finally:
        (render.resolution_x, render.resolution_y,
         render.pixel_aspect_x, render.pixel_aspect_y) = previous


def build_fixture(output):
    import bpy
    go.clear_scene()
    hull = go.material("Ceramic blue", (0.035, 0.17, 0.35, 1), metallic=0.45, roughness=0.3)
    steel = go.material("Brushed alloy", (0.48, 0.54, 0.62, 1), metallic=0.8)
    rubber = go.material("Graphite rubber", (0.015, 0.02, 0.026, 1), roughness=0.8)
    copper = go.material("Copper wiring", (0.7, 0.23, 0.05, 1), metallic=0.7)
    body = go.collection("01 chassis")
    wheels = go.collection("02 wheel assemblies")
    cargo = go.collection("03 instruments")
    go.box("Chassis", (3.6, 2.1, 0.4), (0, 0, 1.1), material=hull, collection=body)
    go.box("Battery housing", (1.9, 1.7, 0.8), (0.2, 0, 1.65), material=hull, collection=body)
    for index, x in enumerate((-1.3, 0, 1.3)):
        for side in (-1, 1):
            y = side * 1.35
            suffix = f"{index}_{side}"
            go.cylinder("Tire " + suffix, 0.48, 0.3, (x, y, 0.6), rotation=(1.5707963268, 0, 0),
                        material=rubber, collection=wheels)
            go.cylinder("Hub " + suffix, 0.23, 0.34, (x, y, 0.6), rotation=(1.5707963268, 0, 0),
                        material=steel, collection=wheels)
            go.beam("Suspension " + suffix, (x - 0.2, side * 0.65, 1.1), (x, y, 0.6),
                    0.14, material=steel, collection=wheels)
    go.cylinder("Sensor mast", 0.075, 1.5, (-0.9, 0, 2.25), material=steel, collection=cargo)
    go.sphere("Sensor head", 0.3, (-0.9, 0, 3), scale=(1, 0.8, 0.65), material=hull, collection=cargo)
    go.tube_curve("Power conduit", [(-1.4, -0.85, 1.4), (-0.6, -0.85, 1.6),
                                   (0.8, -0.85, 1.6), (1.45, -0.85, 1.25)],
                  radius=0.055, material=copper, collection=cargo)
    # A real Boolean modifier must stay supported by inspect/render.
    hatch = go.box("Boolean hatch", (0.8, 0.8, 0.25), (0.7, 0, 2.17), bevel=0,
                   material=steel, collection=cargo)
    cut = go.cylinder("Hidden hatch cutter", 0.16, 0.6, (0.7, 0, 2.17), bevel=0, collection=cargo)
    cut.hide_render = True
    modifier = hatch.modifiers.new("Cable access", "BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.object = cut
    go.ground(20, z=0, material=go.material("Ground", (0.12, 0.14, 0.17, 1)))
    # Loading the blend must never execute this embedded text block.
    sentinel = output / "EMBEDDED_SCRIPT_EXECUTED.txt"
    embedded = bpy.data.texts.new("untrusted_embedded.py")
    embedded.use_module = True
    embedded.use_fake_user = True
    embedded.write(f"from pathlib import Path\nPath({str(sentinel)!r}).write_text('unsafe')\n")
    blend = Path(go.save_revision(output / "rover-r001.blend"))
    previous_hash = digest(blend)
    expect_error(lambda: go.save_revision(blend), FileExistsError)
    assert digest(blend) == previous_hash, "Existing revision changed"
    assert not Path(str(blend) + "1").exists(), "Unexpected Blender backup file"
    return blend, sentinel


def save_headless_preview_edit(path, expected_value):
    """Persist the simulated user's edits and reset only the harness undo state.

    Blender background mode keeps an explicitly pushed undo step dirty across
    save_mainfile, save_as_mainfile(copy=False), and open_mainfile. A fresh window
    manager clears that synthetic state; reload the saved user copy afterward so
    the fixture cannot discard the work it is supposed to protect.
    """
    import bpy
    saved = Path(go.save_revision(path))
    assert saved.is_file()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.use_scripts_auto_execute = False
    bpy.ops.wm.open_mainfile(filepath=str(saved), load_ui=False, use_scripts=False)
    assert bpy.context.scene.get("preview_user_change") == expected_value
    assert not bpy.data.is_dirty, "The isolated headless undo state was not reset"
    return saved


def validate_stages_and_preview(output, checks):
    """Exercise stage/preview state machines without starting a GUI or timer."""
    import bpy
    import go_blender_stage as stages
    import go_blender_preview as preview_module
    project = output / "staged-project"
    project.mkdir()
    (project / "stage_parts.py").write_text(
        "import go_blender as g\n"
        "def anchor():\n"
        "    return g.box('Stage base module', (2, 1, 1), (0, 0, 0.5), bevel=0, collection='Stage body')\n",
        encoding="utf-8")

    def execute(name, code, base=None):
        script = project / f"{name}.py"
        script.write_text(code, encoding="utf-8")
        value = {"scriptPath": str(script), "baseScene": str(base) if base else None,
                 "outputPath": str(project / f"{name}.temporary.blend"),
                 "reportPath": str(project / f"{name}.report.json"),
                 "label": name, "projectDirectory": str(project)}
        request_file = project / f"{name}.request.json"
        request_file.write_text(json.dumps(value), encoding="utf-8")
        return stages.run_request(request_file), Path(value["outputPath"])

    first, first_path = execute("01_blockout", """from stage_parts import anchor
obj = anchor()
modifier = obj.modifiers.new('Render detail copies', 'ARRAY')
modifier.count = 2
modifier.use_relative_offset = False
modifier.use_constant_offset = True
modifier.constant_offset_displace = (3, 0, 0)
modifier.show_viewport = False
modifier.show_render = True
""")
    assert first["success"] and first["valid"] and first["inspectionCompleted"], first
    assert first["baseScene"] is None and first["outputSha256"] == digest(first_path)
    bpy.ops.wm.open_mainfile(filepath=str(first_path), load_ui=False, use_scripts=False)
    assert not bpy.data.objects["Stage base module"].modifiers["Render detail copies"].show_viewport
    first_hash = digest(first_path)
    second, second_path = execute("02_cabin", """import bpy
import go_blender as g
assert bpy.data.objects.get('Stage base module') is not None
g.box('Stage cabin', (1, 0.8, 0.6), (0, 0, 1.3), collection='Stage body')
""", first_path)
    assert second["success"] and second["valid"] and second["baseUnchanged"], second
    assert digest(first_path) == first_hash
    third, third_path = execute("03_defect", """import go_blender as g
g.mesh('Stage defect', [(0,0,2), (1,0,2), (2,0,2)], [(0,1,2)])
""", second_path)
    assert third["success"] and not third["valid"] and third_path.is_file(), third
    assert any(issue["code"] == "degenerate_faces" for issue in third["issues"])
    third_hash = digest(third_path)
    repaired, repaired_path = execute("04_repair", """import bpy
import go_blender as g
bpy.data.objects.remove(bpy.data.objects['Stage defect'], do_unlink=True)
g.box('Stage repaired bracket', (0.3, 0.3, 0.3), (0, 0, 2))
""", third_path)
    assert repaired["success"] and repaired["valid"] and repaired["baseUnchanged"], repaired
    assert digest(third_path) == third_hash
    failed, failed_path = execute("05_script_error", "raise RuntimeError('intentional stage smoke failure')\n", repaired_path)
    assert not failed["success"] and not failed_path.exists() and failed["baseUnchanged"], failed
    oversized, oversized_path = execute("06_oversized", "#" + "x" * stages.MAX_SCRIPT_CHARACTERS, repaired_path)
    assert not oversized["success"] and not oversized_path.exists(), oversized
    checks.append("small stages preserve source revisions and authoring settings, retain invalid intermediates, and support repair")
    checks.append("stage script failures and the 12000-character limit never produce a successful revision")

    runtime = output / "headless-preview-state"
    runtime.mkdir()
    state_path, status_path = runtime / "state.json", runtime / "status.json"
    token = "deterministic-headless-preview-smoke"
    preview = preview_module.PreviewSession({"workspace": str(Path.cwd().resolve()),
        "statePath": str(state_path), "statusPath": str(status_path), "sessionToken": token})
    preview.initialize_empty()
    assert not bpy.data.is_dirty, "Private preview baseline must be clean"
    assert not bpy.context.preferences.view.show_splash, "Quick Setup must not obscure the dedicated preview"
    assert not bpy.context.preferences.use_preferences_save, "Preview-only preferences must never be auto-saved globally"

    def state(revision, path=None, expected=None, session_token=token):
        tool.atomic_report(state_path, {"sessionToken": session_token, "revision": revision,
            "blendPath": str(path) if path is not None else None,
            "expectedSha256": expected if expected is not None else (digest(path) if path else None),
            "label": f"Smoke stage {revision}"})

    def tick():
        preview.tick()
        return json.loads(status_path.read_text(encoding="utf-8"))

    state(0)
    assert tick()["state"] == "ready"
    bpy.context.scene["preview_user_change"] = "keep even before stage 1"
    bpy.ops.ed.undo_push(message="Simulated unsaved preview edit")
    assert bpy.data.is_dirty
    state(1, second_path)
    assert tick()["state"] == "blocked"
    assert Path(bpy.data.filepath) == preview.initial_path
    saved_initial_edit = save_headless_preview_edit(runtime / "user-saved-initial-edit.blend",
                                                     "keep even before stage 1")
    loaded = tick()
    assert loaded["state"] == "ready" and loaded["revision"] == 1, loaded
    assert Path(bpy.data.filepath) == second_path and not bpy.data.is_dirty
    bpy.context.scene["preview_user_change"] = "keep existing stage edits"
    bpy.ops.ed.undo_push(message="Simulated unsaved stage edit")
    state(2, repaired_path)
    assert tick()["state"] == "blocked"
    assert Path(bpy.data.filepath) == second_path
    saved_stage_edit = save_headless_preview_edit(runtime / "user-saved-stage-edit.blend",
                                                  "keep existing stage edits")
    assert tick()["state"] == "ready" and Path(bpy.data.filepath) == repaired_path
    state(3, first_path, expected="0" * 64)
    assert tick()["state"] == "error" and Path(bpy.data.filepath) == repaired_path
    state(3, first_path, session_token="different-preview-session")
    assert tick()["state"] == "error" and Path(bpy.data.filepath) == repaired_path
    original_orient = preview_module._orient_view
    framing_calls = []

    def fail_framing():
        framing_calls.append(True)
        raise ValueError("intentional framing-budget regression")

    try:
        preview_module._orient_view = fail_framing
        state(3, first_path)
        first_failure = tick()
        assert first_failure["state"] == "error" and first_failure["revision"] == 3
        assert "framing-budget regression" in first_failure["message"]
        assert tick() == first_failure, "A loaded revision with failed framing became falsely ready"
        assert len(framing_calls) == 1, "The failing revision was repeatedly reloaded/reframed"
    finally:
        preview_module._orient_view = original_orient
    state(4, repaired_path)
    assert tick()["state"] == "ready" and Path(bpy.data.filepath) == repaired_path
    assert digest(first_path) == first_hash and digest(third_path) == third_hash
    checks.append("headless preview loads verified revisions and protects dirty edits, hashes and session identity")
    checks.append("preview framing failures remain errors across polls without reload; a newer revision can recover")


def main():
    import bpy
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default=f"artifacts/blender-toolkit-{int(time.time())}")
    parser.add_argument("--skip-render", action="store_true")
    args = parser.parse_args(argv)
    output = go.workspace_path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    checks = []
    expect_error(lambda: go.workspace_path(Path.cwd().parent / "outside.blend"), ValueError)
    expect_error(lambda: go.mesh("Invalid face", [(0, 0, 0)] * 3, [(0, 1, 9)]), ValueError)
    checks.append("workspace paths and arbitrary mesh indices are checked")
    validate_camera_orientation()
    checks.append("top camera projects +X right and +Y up with square and landscape framing")
    parent = go.collection("Hierarchy station")
    child = go.collection("Hierarchy module", parent=parent)
    part = go.box("Nested hierarchy part", collection=child.name)
    assert go.collection(child.name) == child
    assert go.collection(child.name, parent=parent.name) == child
    go.tag_component(part, child.name)
    assert child.name not in bpy.context.scene.collection.children
    actual_parents = [item for item in bpy.data.collections if child.name in item.children]
    assert actual_parents == [parent], [item.name for item in actual_parents]
    other_parent = go.collection("Other hierarchy station")
    expect_error(lambda: go.collection(child.name, parent=other_parent), ValueError)
    expect_error(lambda: go.collection(child.name, parent="Must not be created"), ValueError)
    assert bpy.data.collections.get("Must not be created") is None
    assert child.name not in other_parent.children
    parent.hide_render = True
    assert part.name not in go.render_visible_objects()
    checks.append("collection lookup and string tagging preserve parent hierarchy and inherited visibility")
    blend, sentinel = build_fixture(output)
    checks.append("named multi-component model and revision overwrite protection")
    original_hash = digest(blend)
    report = request(output, "inspect-rover", blend)
    assert report["valid"], report["issues"]
    assert report["counts"]["meshObjects"] >= 20, report["counts"]
    assert report["counts"]["curves"] == 1
    assert report["bounds"]["dimensions"][0] < 10, "Presentation ground polluted the model bounds"
    assert len({entry["component"] for entry in report["objects"] if entry["component"]}) >= 3
    assert not sentinel.exists(), "Embedded text script executed"
    assert report["sourceUnchanged"] and digest(blend) == original_hash
    assert any(entry.get("evaluatedMesh") for entry in report["objects"])
    checks.append("actual mesh/modifier diagnostics, ground exclusion and embedded-script suppression")
    if not args.skip_render:
        report = request(output, "review-rover", blend, "render", views=list(go.VIEWS), resolution=256, samples=8)
        assert report["valid"], report["issues"]
        assert len(report["images"]) == 6
        for image in report["images"]:
            assert image["width"] == image["height"] == 256 and image["bytes"] > 1000
            assert Path(image["path"]).is_file()
        assert report["sourceUnchanged"] and digest(blend) == original_hash
        checks.append("six actual EEVEE PNG views with dimensions, byte sizes and renderer evidence")
    # Open surfaces and non-manifold edges are reported, not mistaken for fatal errors.
    go.clear_scene()
    go.mesh("Intentional open panel", [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], [(0, 1, 2, 3)])
    panel = Path(go.save_revision(output / "open-panel.blend"))
    report = request(output, "inspect-open-panel", panel)
    assert report["valid"] and any(i["code"] == "boundary_edges" for i in report["issues"])
    checks.append("intentional open surfaces remain structurally valid with boundary warnings")
    # Review dimensions must follow render settings, including collection paths.
    go.clear_scene()
    base = go.box("Render-only array", (1, 1, 1), bevel=0)
    modifier = base.modifiers.new("Render copies", "ARRAY")
    modifier.count = 3
    modifier.use_relative_offset = False
    modifier.use_constant_offset = True
    modifier.constant_offset_displace = (4, 0, 0)
    modifier.show_viewport = False
    modifier.show_render = True
    hidden = go.collection("Hidden in render")
    go.box("Far hidden collection part", (2, 2, 2), (1000, 0, 0), bevel=0, collection=hidden)
    hidden.hide_render = True
    excluded = go.collection("Excluded layer")
    go.box("Far excluded layer part", (2, 2, 2), (-1000, 0, 0), bevel=0, collection=excluded)
    bpy.context.view_layer.layer_collection.children[excluded.name].exclude = True
    viewport_hidden = go.box("Render-visible viewport-hidden part", (1, 1, 1), (0, 4, 0), bevel=0)
    viewport_hidden.hide_viewport = True
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 0.01
    bpy.context.scene.unit_settings.length_unit = "CENTIMETERS"
    visibility = Path(go.save_revision(output / "render-visibility.blend"))
    report = request(output, "inspect-render-visibility", visibility)
    assert report["valid"], report["issues"]
    assert abs(report["bounds"]["dimensions"][0] - 9) < 1e-5, report["bounds"]
    assert abs(report["bounds"]["dimensions"][1] - 5) < 1e-5, report["bounds"]
    records = {entry["name"]: entry for entry in report["objects"]}
    assert records["Far hidden collection part"]["hiddenRender"]
    assert records["Far excluded layer part"]["hiddenRender"]
    assert not records["Render-visible viewport-hidden part"]["hiddenRender"]
    assert records["Render-only array"]["evaluatedMesh"]["vertices"] == 24
    assert abs(report["units"]["scaleLength"] - 0.01) < 1e-6
    assert report["units"]["lengthUnit"] == "CENTIMETERS"
    if not args.skip_render:
        review = request(output, "review-render-visibility", visibility, "render",
                         views=["perspective"], resolution=128, samples=4)
        assert review["valid"] and len(review["images"]) == 1, review["issues"]
        assert review["sourceUnchanged"]
    checks.append("render-only modifiers, viewport hiding, collection exclusion and explicit scene units")
    # A scene may legitimately consist solely of collection instances. Its
    # prototype is not linked to the scene and still needs source/budget checks.
    go.clear_scene()
    asset = go.collection("Reusable instance asset")
    prototype = go.box("Instance prototype", (2, 1, 1), bevel=0, collection=asset)
    bpy.context.scene.collection.children.unlink(asset)
    for index, x in enumerate((0, 4, 1000)):
        instance = bpy.data.objects.new(f"Asset instance {index}", None)
        instance.instance_type = "COLLECTION"
        instance.instance_collection = asset
        instance.location.x = x
        instance.hide_render = index == 2
        bpy.context.scene.collection.objects.link(instance)
    assert not any(obj.type == "MESH" for obj in bpy.context.scene.objects)
    lower, upper = go.scene_bounds()
    assert abs((upper - lower).x - 6) < 1e-5, (lower, upper)
    go.setup_review_scene(resolution=128, samples=4)
    instance_scene = Path(go.save_revision(output / "collection-instances.blend"))
    report = request(output, "inspect-collection-instances", instance_scene)
    assert report["valid"], report["issues"]
    assert abs(report["bounds"]["dimensions"][0] - 6) < 1e-5, report["bounds"]
    assert report["counts"]["evaluatedMeshInstances"] == 2, report["counts"]
    assert report["counts"]["evaluatedVertices"] == 16, report["counts"]
    prototype_record = next(entry for entry in report["objects"] if entry["name"] == "Instance prototype")
    assert prototype_record["instancedPrototype"] and prototype_record["mesh"]["vertices"] == 8
    assert prototype_record["visibleInstanceCount"] == 2
    if not args.skip_render:
        review = request(output, "review-collection-instances", instance_scene, "render",
                         views=["perspective"], resolution=128, samples=4)
        assert review["valid"] and len(review["images"]) == 1, review["issues"]
        assert review["sourceUnchanged"]
    checks.append("pure collection instances have checked prototype geometry, bounded counts and hidden-instance-safe framing")
    # Exercise the actual shipped scaffold with valid non-mesh-only geometry.
    curve_project = output / "curve-text-project"
    curve_project.mkdir()
    resources = ROOT / "src" / "GoWinUI.App" / "Assets" / "Blender"
    shutil.copyfile(resources / "go_blender.py", curve_project / "go_blender.py")
    template = (resources / "scene.py").read_text(encoding="utf-8")
    authoring = """g.tube_curve('Only curve pipe', [(0,0,0), (1,0,1), (2,0,1)], radius=0.12)
text_data = bpy.data.curves.new('Only text geometry', 'FONT')
text_data.body = 'GO'
text_data.extrude = 0.1
text_object = bpy.data.objects.new('Text component', text_data)
bpy.context.scene.collection.objects.link(text_object)
text_object.location = (0, 1, 0)
"""
    marker = "# AUTHOR YOUR MODEL HERE. Use named components, materials and reusable functions."
    assert marker in template
    curve_script = curve_project / "scene.py"
    curve_script.write_text(template.replace(marker, authoring), encoding="utf-8")
    runpy.run_path(str(curve_script), run_name="__main__")
    assert not any(obj.type == "MESH" for obj in bpy.context.scene.objects)
    curve_scene = curve_project / "scene-v001.blend"
    report = request(output, "inspect-curve-text", curve_scene)
    assert report["valid"] and report["counts"]["curves"] == 2, report["issues"]
    assert report["counts"]["meshObjects"] == 0
    checks.append("shipped scaffold saves and frames a valid curve-and-text-only model")
    # An unknown modifier must not prevent checks of later known multipliers.
    # Disable the large Array in the viewport so the fixture itself stays small.
    go.clear_scene()
    budget_base = go.sphere("Boolean before large array")
    cutter = go.box("Budget cutter", (0.3, 0.3, 0.3), bevel=0)
    cutter.hide_render = True
    boolean = budget_base.modifiers.new("Unknown output size", "BOOLEAN")
    boolean.operation = "DIFFERENCE"
    boolean.object = cutter
    large_array = budget_base.modifiers.new("Known large multiplier", "ARRAY")
    large_array.count = 1000
    large_array.show_viewport = False
    large_array.show_render = True
    assert tool._modifier_estimate(budget_base) > tool.MAX_EVALUATED_VERTICES
    budget_file = Path(go.save_revision(output / "modifier-budget.blend"))
    report = request(output, "inspect-modifier-budget", budget_file)
    assert not report["valid"] and any(i["code"] == "modifier_budget" for i in report["issues"])
    assert "evaluatedVertices" not in report["counts"], "Over-budget stack was evaluated"
    checks.append("Boolean followed by large Array is stopped before evaluated geometry allocation")
    go.clear_scene()
    bad = go.mesh("Broken part", [(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 1, 0)], [(0, 1, 2)])
    bad.scale = (1, 2, 1)
    image = bpy.data.images.new("Missing image", width=1, height=1)
    image.use_fake_user = True
    image.source = "FILE"
    image.filepath = str(output / "does-not-exist.png")
    broken = Path(go.save_revision(output / "broken.blend"))
    report = request(output, "inspect-broken", broken)
    codes = {issue["code"] for issue in report["issues"]}
    assert not report["valid"] and {"degenerate_faces", "loose_vertices", "nonuniform_scale", "missing_image"} <= codes, codes
    checks.append("degenerate faces, loose vertices, transform and missing-texture diagnostics")
    base = {"operation": "render", "blendPath": str(blend), "outputDirectory": str(output / "unused")}
    expect_error(lambda: tool.validate_request({**base, "resolution": 8192}), ValueError)
    expect_error(lambda: tool.validate_request({**base, "views": ["../../escape"]}), ValueError)
    expect_error(lambda: tool.validate_request({**base, "reportPath": str(output.parent / "escape.json")}), ValueError)
    checks.append("request limits, named views and report-directory containment")
    validate_stages_and_preview(output, checks)
    summary = {"passed": True, "checks": checks, "outputDirectory": str(output), "blenderVersion": bpy.app.version_string}
    tool.atomic_report(output / "validation.json", summary)
    print(json.dumps(summary, indent=2), flush=True)


if __name__ == "__main__":
    main()
