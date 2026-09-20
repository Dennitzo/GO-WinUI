"""Inspect/render a .blend without executing embedded scripts or saving it.

Usage: blender --background --disable-autoexec --python go_blender_tool.py -- request.json
The process working directory is the workspace security boundary. No third-party
Python packages are required. Reports describe geometry and actual files, never
claim visual design quality or a successful vision review.
"""
from __future__ import annotations

import argparse
from array import array
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import sys
import tempfile
import time

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import go_blender as helpers

MAX_OBJECTS = 10000
MAX_VERTICES = 2000000
MAX_EDGES = 4000000
MAX_FACES = 2000000
MAX_LOOPS = 8000000
MAX_EVALUATED_VERTICES = 1000000


def _issue(issues, severity, code, message, name=None):
    value = {"severity": severity, "code": code, "message": message}
    if name is not None:
        value["object"] = name
    issues.append(value)


def _hash_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def atomic_report(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".go-report-", suffix=".json", dir=path.parent)
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


def validate_request(raw):
    if not isinstance(raw, dict):
        raise ValueError("Request must be a JSON object")
    operation = raw.get("operation")
    if operation not in {"inspect", "render"}:
        raise ValueError("operation must be inspect or render")
    path_value = raw.get("blendPath")
    if not isinstance(path_value, str) or not Path(path_value).is_absolute():
        raise ValueError("blendPath must be absolute")
    blend = helpers.workspace_path(path_value, suffix=".blend", must_exist=True)
    output = helpers.workspace_path(raw.get("outputDirectory", str(Path.cwd() / "blender-review")))
    report = helpers.workspace_path(raw.get("reportPath", str(output / "report.json")), suffix=".json")
    if not report.is_relative_to(output):
        raise ValueError("reportPath must be inside outputDirectory")
    if output.exists() and not output.is_dir():
        raise ValueError("outputDirectory must be a directory")
    views = raw.get("views", ["perspective", "front", "right", "top"])
    if not isinstance(views, list) or not 1 <= len(views) <= len(helpers.VIEWS):
        raise ValueError("views must contain between one and six named views")
    if any(not isinstance(view, str) or view not in helpers.VIEWS for view in views):
        raise ValueError(f"views must be drawn from {helpers.VIEWS}")
    if len(set(views)) != len(views):
        raise ValueError("views must not contain duplicates")
    resolution = helpers._integer(raw.get("resolution", 768), "resolution", 128, 2048)
    samples = helpers._integer(raw.get("samples", 32), "samples", 1, 128)
    return {"operation": operation, "blendPath": blend, "outputDirectory": output,
            "reportPath": report, "views": views, "resolution": resolution, "samples": samples}


def _mesh_diagnostics(data):
    """Read source mesh topology in bounded linear memory, without altering it."""
    edge_faces = array("I", [0]) * len(data.edges)
    vertex_edges = array("I", [0]) * len(data.vertices)
    finite = True
    for vertex in data.vertices:
        if not all(math.isfinite(value) for value in vertex.co):
            finite = False
    for edge in data.edges:
        for index in edge.vertices:
            vertex_edges[index] += 1
    for loop in data.loops:
        edge_faces[loop.edge_index] += 1
    degenerate = sum(not math.isfinite(face.area) or face.area <= 1e-12 for face in data.polygons)
    return {"vertices": len(data.vertices), "edges": len(data.edges), "faces": len(data.polygons),
            "triangles": sum(max(0, len(face.vertices) - 2) for face in data.polygons),
            "finiteCoordinates": finite, "degenerateFaces": degenerate,
            "looseVertices": sum(count == 0 for count in vertex_edges),
            "looseEdges": sum(count == 0 for count in edge_faces),
            "boundaryEdges": sum(count == 1 for count in edge_faces),
            "nonManifoldEdges": sum(count > 2 for count in edge_faces)}


def _modifier_estimate(obj):
    """Conservative guard before asking Blender to evaluate mesh modifiers."""
    estimate = max(len(obj.data.vertices), len(obj.data.polygons), 1)
    unknown = False
    known = {"BEVEL", "BOOLEAN", "BUILD", "CAST", "CORRECTIVE_SMOOTH", "CURVE",
             "DECIMATE", "DISPLACE", "EDGE_SPLIT", "HOOK", "LAPLACIANDEFORM",
             "LAPLACIANSMOOTH", "LATTICE", "MESH_CACHE", "MESH_DEFORM", "NORMAL_EDIT",
             "SHRINKWRAP", "SIMPLE_DEFORM", "SMOOTH", "SOLIDIFY", "SURFACE_DEFORM",
             "TRIANGULATE", "UV_PROJECT", "UV_WARP", "VERTEX_WEIGHT_EDIT",
             "VERTEX_WEIGHT_MIX", "VERTEX_WEIGHT_PROXIMITY", "WARP", "WAVE",
             "WEIGHTED_NORMAL", "WELD", "ARMATURE", "DATA_TRANSFER"}
    for modifier in obj.modifiers:
        if not modifier.show_render:
            continue
        if modifier.type in {"SUBSURF", "MULTIRES"}:
            level = getattr(modifier, "render_levels", getattr(modifier, "levels", 0))
            estimate *= 4 ** min(level, 12)
        elif modifier.type == "ARRAY":
            if modifier.fit_type != "FIXED_COUNT":
                unknown = True
            else:
                estimate *= max(1, modifier.count)
        elif modifier.type == "MIRROR":
            estimate *= 2 ** sum(modifier.use_axis)
        elif modifier.type == "BEVEL":
            estimate += len(obj.data.edges) * max(1, modifier.segments) * 4
        elif modifier.type == "SOLIDIFY":
            estimate *= 3
        elif modifier.type in {"BOOLEAN", "REMESH", "NODES", "PARTICLE_SYSTEM", "SCREW", "SKIN"}:
            unknown = True
        elif modifier.type not in known:
            unknown = True
        if estimate > MAX_EVALUATED_VERTICES:
            return estimate
    return None if unknown else estimate


def _source_inventory(scene):
    """Bounded source inventory, including unlinked collection-instance assets."""
    objects = {obj.name: obj for obj in list(scene.objects)[:MAX_OBJECTS + 1]}
    pending = [obj.instance_collection for obj in objects.values()
               if obj.instance_type == "COLLECTION" and obj.instance_collection is not None]
    visited = set()
    while pending and len(objects) <= MAX_OBJECTS and len(visited) <= MAX_OBJECTS:
        component = pending.pop()
        identity = component.as_pointer()
        if identity in visited:
            continue
        visited.add(identity)
        pending.extend(component.children)
        for obj in component.objects:
            if obj.name in objects:
                continue
            objects[obj.name] = obj
            if obj.instance_type == "COLLECTION" and obj.instance_collection is not None:
                pending.append(obj.instance_collection)
            if len(objects) > MAX_OBJECTS:
                break
    exceeded = len(scene.objects) > MAX_OBJECTS or len(objects) > MAX_OBJECTS or len(visited) > MAX_OBJECTS
    return list(objects.values())[:MAX_OBJECTS], exceeded


def inspect_scene():
    import bpy
    from mathutils import Vector
    issues, objects, cache = [], [], {}
    scene = bpy.context.scene
    visible = helpers.render_visible_objects()
    source_objects, budget_exceeded = _source_inventory(scene)
    counts = {"objects": len(source_objects), "sceneObjects": len(scene.objects), "meshObjects": 0, "vertices": 0, "edges": 0,
              "faces": 0, "triangles": 0, "materials": len(bpy.data.materials), "hiddenMeshes": 0,
              "curves": 0, "estimatedEvaluatedVertices": 0}
    model_min = [math.inf] * 3
    model_max = [-math.inf] * 3
    if budget_exceeded:
        _issue(issues, "error", "object_budget", f"Scene and instanced source assets exceed {MAX_OBJECTS} objects/collections")
    for obj in source_objects:
        matrix_finite = all(math.isfinite(v) for row in obj.matrix_world for v in row)
        scale = [float(value) if math.isfinite(value) else None for value in obj.scale]
        entry = {"name": obj.name, "type": obj.type, "collections": [c.name for c in obj.users_collection],
                 "materials": [slot.material.name if slot.material else None for slot in obj.material_slots],
                 "hiddenRender": obj.name not in visible,
                 "hiddenViewport": bool(obj.hide_viewport or (obj.hide_get() if obj.name in bpy.context.view_layer.objects else True)),
                 "instancedPrototype": obj.name not in scene.objects,
                 "environment": bool(obj.get("go_environment", False)), "scale": scale,
                 "component": obj.get("go_component", None), "bounds": None}
        objects.append(entry)
        if not matrix_finite:
            _issue(issues, "error", "nonfinite_transform", "Object has a non-finite world transform", obj.name)
        elif any(abs(value) < 1e-9 for value in scale):
            _issue(issues, "error", "zero_scale", "A scale axis collapses the geometry", obj.name)
        elif any(value < 0 for value in scale):
            _issue(issues, "warning", "negative_scale", "Negative scale can invert orientation during export", obj.name)
        elif max(scale) - min(scale) > 1e-5:
            _issue(issues, "warning", "nonuniform_scale", "Non-uniform object scale; check modifiers/export dimensions", obj.name)
        if obj.type == "MESH":
            data = obj.data
            counts["meshObjects"] += 1
            counts["vertices"] += len(data.vertices)
            counts["edges"] += len(data.edges)
            counts["faces"] += len(data.polygons)
            if entry["hiddenRender"] or entry["hiddenViewport"]:
                counts["hiddenMeshes"] += 1
                _issue(issues, "warning", "hidden_mesh", "Mesh is hidden in rendering or the viewport", obj.name)
            over = (counts["vertices"] > MAX_VERTICES or counts["edges"] > MAX_EDGES or
                    counts["faces"] > MAX_FACES or len(data.loops) > MAX_LOOPS)
            if over:
                budget_exceeded = True
                _issue(issues, "error", "geometry_budget", "Source geometry exceeds the bounded review budget", obj.name)
                entry["diagnosticsSkipped"] = "geometry_budget"
                continue
            pointer = data.as_pointer()
            if pointer not in cache:
                cache[pointer] = _mesh_diagnostics(data)
            diagnostic = dict(cache[pointer])
            entry["mesh"] = diagnostic
            entry["diagnosticScope"] = "source_mesh"
            counts["triangles"] += diagnostic["triangles"]
            if not diagnostic["finiteCoordinates"]:
                _issue(issues, "error", "nonfinite_vertices", "Mesh contains non-finite coordinates", obj.name)
            for key, code, severity, description in (
                ("degenerateFaces", "degenerate_faces", "error", "zero-area or non-finite faces"),
                ("looseVertices", "loose_vertices", "warning", "vertices without incident edges"),
                ("looseEdges", "loose_edges", "warning", "edges without incident faces"),
                ("boundaryEdges", "boundary_edges", "warning", "boundary edges; open surfaces may be intentional"),
                ("nonManifoldEdges", "nonmanifold_edges", "warning", "edges shared by more than two faces"),
            ):
                if diagnostic[key]:
                    _issue(issues, severity, code, f"{diagnostic[key]} {description}", obj.name)
            estimate = _modifier_estimate(obj)
            entry["estimatedEvaluatedVertices"] = estimate
            if estimate is None and obj.modifiers:
                _issue(issues, "warning", "procedural_geometry_estimate_unavailable",
                       "Procedural output has no reliable static size estimate; actual evaluated geometry is checked separately", obj.name)
            elif estimate is not None:
                counts["estimatedEvaluatedVertices"] += estimate
                if estimate > MAX_EVALUATED_VERTICES:
                    _issue(issues, "error", "modifier_budget", "Modifier stack exceeds the evaluation budget", obj.name)
                    entry["renderBlocked"] = True
        elif obj.type in {"CURVE", "SURFACE", "FONT", "META"}:
            counts["curves"] += 1
            if obj.type == "CURVE":
                points = sum(len(s.bezier_points) + len(s.points) for s in obj.data.splines)
                curve_resolution = obj.data.render_resolution_u or obj.data.resolution_u
                estimate = points * max(1, curve_resolution) * max(4, obj.data.bevel_resolution * 4)
                counts["estimatedEvaluatedVertices"] += estimate
                if estimate > MAX_EVALUATED_VERTICES:
                    _issue(issues, "error", "curve_budget", "Curve tessellation exceeds the evaluation budget", obj.name)
                    entry["renderBlocked"] = True
        if obj.type in {"MESH", "CURVE", "SURFACE", "FONT", "META"} and matrix_finite:
            corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
            if corners and all(math.isfinite(v) for point in corners for v in point):
                lower = [min(point[axis] for point in corners) for axis in range(3)]
                upper = [max(point[axis] for point in corners) for axis in range(3)]
                entry["bounds"] = {"min": lower, "max": upper, "dimensions": [b - a for a, b in zip(lower, upper)]}
                if not entry["hiddenRender"] and not entry["environment"]:
                    model_min = [min(a, b) for a, b in zip(model_min, lower)]
                    model_max = [max(a, b) for a, b in zip(model_max, upper)]
    if counts["estimatedEvaluatedVertices"] > MAX_EVALUATED_VERTICES * 2:
        budget_exceeded = True
        _issue(issues, "error", "evaluated_geometry_budget", "Combined estimated modifier output exceeds review budget")
    missing = []
    for image in bpy.data.images:
        if image.source not in {"FILE", "TILED", "SEQUENCE", "MOVIE"} or image.packed_file or image.packed_files:
            continue
        if image.filepath:
            path = Path(bpy.path.abspath(image.filepath, library=image.library))
            if image.source == "TILED":
                exists = all(Path(str(path).replace("<UDIM>", str(tile.number))).is_file() for tile in image.tiles)
            else:
                exists = path.is_file()
            if not exists:
                missing.append({"name": image.name, "path": str(path)})
                _issue(issues, "warning", "missing_image", f"Image file is missing: {image.name}")
    bounds = None
    if all(math.isfinite(value) for value in (*model_min, *model_max)):
        bounds = {"min": model_min, "max": model_max, "dimensions": [b - a for a, b in zip(model_min, model_max)]}
    elif not any(obj.name in visible and obj.instance_type == "COLLECTION" and obj.instance_collection
                 for obj in scene.objects):
        _issue(issues, "error", "no_visible_geometry", "No visible model geometry is available")
    return {"schemaVersion": 1, "valid": not any(i["severity"] == "error" for i in issues),
            "validationMeaning": "Structural geometry diagnostics only; visual quality requires image analysis.",
            "diagnosticScope": "source_mesh", "issues": issues, "objects": objects,
            "units": {"system": scene.unit_settings.system, "scaleLength": scene.unit_settings.scale_length,
                      "lengthUnit": scene.unit_settings.length_unit},
            "boundsUnit": "Blender units; multiply by units.scaleLength for metres.",
            "counts": counts, "bounds": bounds, "missingImages": missing, "images": [],
            "budgetExceeded": budget_exceeded}


def _prepare_render_evaluation(source_names):
    """Match review render visibility/modifiers in memory before graph evaluation.

    Blender's public context API supplies a viewport dependency graph. Its
    ordinary viewport hiding and modifier settings must therefore be aligned
    with the actual review render settings before measuring geometry.
    """
    import bpy
    scene = bpy.context.scene
    visible = helpers.render_visible_objects()

    def visit(layer):
        layer.hide_viewport = False
        # Preserve layer.exclude: it affects rendering as well as the viewport.
        layer.collection.hide_viewport = layer.collection.hide_render
        for child in layer.children:
            visit(child)

    visit(bpy.context.view_layer.layer_collection)
    # Include collections referenced by collection instances, not just scene links.
    for component in bpy.data.collections:
        component.hide_viewport = component.hide_render
    for name in source_names:
        obj = bpy.data.objects.get(name)
        if obj is None:
            continue
        obj.hide_viewport = obj.hide_render
        if obj.name in bpy.context.view_layer.objects:
            obj.hide_set(False)
        for modifier in obj.modifiers:
            modifier.show_viewport = modifier.show_render
            if modifier.show_render and modifier.type in {"SUBSURF", "MULTIRES"}:
                # setup_review_scene uses the same review subdivision cap.
                modifier.levels = min(modifier.render_levels, 4)
        if obj.type == "CURVE" and obj.data.render_resolution_u:
            obj.data.resolution_u = obj.data.render_resolution_u
    scene.render.use_simplify = True
    scene.render.simplify_subdivision = 4
    scene.render.simplify_subdivision_render = 4
    bpy.context.view_layer.update()
    return helpers.render_instance_owners(visible)


def inspect_evaluated(report):
    """Check actual modifier output after the inexpensive source preflight.

    Blender evaluation itself is controlled by the host process timeout. Geometry
    Nodes and Boolean modifiers remain usable; unknown output is never replaced
    by an invented static estimate.
    """
    import bpy
    from mathutils import Vector
    if not report["valid"] or report["budgetExceeded"]:
        return
    total_vertices, total_edges, total_faces, instance_count = 0, 0, 0, 0
    lower, upper = [math.inf] * 3, [-math.inf] * 3
    entries = {entry["name"]: entry for entry in report["objects"]}
    visible = _prepare_render_evaluation(entries)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh_cache = {}
    mesh_instances = 0
    for instance in depsgraph.object_instances:
        instance_count += 1
        if instance_count > MAX_OBJECTS:
            _issue(report["issues"], "error", "instance_budget", "Evaluated scene exceeds the object instance budget")
            report["budgetExceeded"] = True
            break
        obj = instance.object
        owner = instance.parent.original if instance.is_instance and instance.parent else obj.original
        if (obj.type not in {"MESH", "CURVE", "SURFACE", "FONT", "META"} or
                obj.hide_render or obj.get("go_environment", False) or
                owner.name not in visible):
            continue
        entry = entries.get(obj.original.name)
        if entry is not None:
            entry["visibleInstanceCount"] = entry.get("visibleInstanceCount", 0) + 1
        if obj.type == "MESH":
            data = obj.data
            if not len(data.vertices):
                continue
            total_vertices += len(data.vertices)
            total_edges += len(data.edges)
            total_faces += len(data.polygons)
            mesh_instances += 1
            if (total_vertices > MAX_VERTICES or total_edges > MAX_EDGES or
                    total_faces > MAX_FACES or len(data.loops) > MAX_LOOPS):
                _issue(report["issues"], "error", "evaluated_geometry_budget",
                       "Actual evaluated mesh instances exceed the bounded review budget", obj.name)
                report["budgetExceeded"] = True
                break
            identity = data.as_pointer()
            if identity not in mesh_cache:
                diagnostic = _mesh_diagnostics(data)
                mesh_cache[identity] = diagnostic
                if not diagnostic["finiteCoordinates"]:
                    _issue(report["issues"], "error", "evaluated_nonfinite_vertices",
                           "Modifier output contains non-finite coordinates", obj.name)
                if diagnostic["degenerateFaces"]:
                    _issue(report["issues"], "warning", "evaluated_degenerate_faces",
                           f"Modifier output contains {diagnostic['degenerateFaces']} zero-area faces; inspect the modifier stack", obj.name)
            if entry is not None:
                entry["evaluatedMesh"] = mesh_cache[identity]
        # Evaluated instances include Array/Geometry Nodes extents, rather than
        # framing only the source mesh before its modifiers.
        corners = [instance.matrix_world @ Vector(corner) for corner in obj.bound_box]
        if all(math.isfinite(value) for point in corners for value in point):
            local_lower = [min(point[axis] for point in corners) for axis in range(3)]
            local_upper = [max(point[axis] for point in corners) for axis in range(3)]
            lower = [min(a, b) for a, b in zip(lower, local_lower)]
            upper = [max(a, b) for a, b in zip(upper, local_upper)]
        else:
            _issue(report["issues"], "error", "nonfinite_instance_bounds",
                   "An evaluated object instance has non-finite bounds", obj.name)
    if all(math.isfinite(value) for value in (*lower, *upper)):
        report["bounds"] = {"min": lower, "max": upper, "dimensions": [b - a for a, b in zip(lower, upper)]}
        report["boundsScope"] = "evaluated_visible_object_instances"
    elif not report["budgetExceeded"]:
        report["bounds"] = None
        _issue(report["issues"], "error", "no_visible_geometry", "No evaluated visible model geometry is available")
    report["counts"].update({"evaluatedVertices": total_vertices, "evaluatedEdges": total_edges,
                              "evaluatedFaces": total_faces,
                              "evaluatedMeshInstances": mesh_instances,
                              "evaluatedObjectInstances": instance_count})
    report["diagnosticScope"] = "source_and_evaluated_mesh"
    report["evaluationSettings"] = "Review render visibility and modifier settings applied to the viewport dependency graph in memory."
    report["valid"] = not any(issue["severity"] == "error" for issue in report["issues"])


def _png_metadata(path, view):
    with path.open("rb") as stream:
        header = stream.read(24)
    if len(header) < 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise RuntimeError(f"Blender did not write a valid PNG: {path}")
    width, height = struct.unpack(">II", header[16:24])
    return {"view": view, "path": str(path), "width": width, "height": height, "bytes": path.stat().st_size}


def _backend():
    import bpy
    value = {"engine": bpy.context.scene.render.engine, "blenderVersion": bpy.app.version_string,
             "deviceVerification": "Renderer configured; GPU identity is recorded only when Blender exposes it."}
    try:
        import gpu
        value.update({"gpuVendor": gpu.platform.vendor_get(), "gpuRenderer": gpu.platform.renderer_get(),
                      "gpuVersion": gpu.platform.version_get(), "gpuBackend": gpu.platform.backend_type_get()})
    except Exception as error:
        value["gpuQueryUnavailable"] = str(error)
    return value


def render_views(request, report):
    import bpy
    if not report["valid"] or report["budgetExceeded"]:
        _issue(report["issues"], "error", "render_preflight_failed", "Repair structural errors before rendering")
        return
    output = request["outputDirectory"]
    output.mkdir(parents=True, exist_ok=True)
    # Refuse existing images: a review is a new, auditable observation.
    paths = [(view, helpers.workspace_path(output / f"{view}.png", suffix=".png")) for view in request["views"]]
    if any(path.exists() for _, path in paths):
        raise FileExistsError("Review image already exists; use a fresh outputDirectory")
    bounds = (report["bounds"]["min"], report["bounds"]["max"])
    helpers.setup_review_scene(request["views"][0], request["resolution"], request["samples"], bounds)
    report["render"] = {"resolution": request["resolution"], "requestedSamples": request["samples"],
                        "presentation": "temporary neutral EEVEE lighting; original blend is not saved",
                        "backend": _backend()}
    for view, path in paths:
        print(f"GO Blender: rendering {view} ({request['resolution']} x {request['resolution']})", flush=True)
        helpers.frame_camera(view, bounds)
        bpy.context.scene.render.filepath = str(path)
        result = bpy.ops.render.render(write_still=True)
        if "FINISHED" not in result:
            raise RuntimeError(f"Rendering {view} did not finish")
        report["images"].append(_png_metadata(path, view))
        print(f"GO Blender: saved {path.name} ({report['images'][-1]['bytes']} bytes)", flush=True)
        report["render"]["backend"] = _backend()


def run_request(request_path):
    import bpy
    started = time.monotonic()
    request_path = helpers.workspace_path(request_path, suffix=".json", must_exist=True)
    request = validate_request(json.loads(request_path.read_text(encoding="utf-8-sig")))
    source_hash = _hash_file(request["blendPath"])
    report = {"schemaVersion": 1, "valid": False, "issues": [], "objects": [], "counts": {},
              "bounds": None, "images": []}
    try:
        # load_ui=False prevents file UI/workspace settings from replacing this context.
        bpy.context.preferences.filepaths.use_scripts_auto_execute = False
        bpy.ops.wm.open_mainfile(filepath=str(request["blendPath"]), load_ui=False, use_scripts=False)
        report = inspect_scene()
        inspect_evaluated(report)
        if request["operation"] == "render":
            render_views(request, report)
    except Exception as error:
        _issue(report["issues"], "error", "operation_failed", f"{type(error).__name__}: {error}")
    report["valid"] = not any(issue["severity"] == "error" for issue in report["issues"])
    report.update({"operation": request["operation"], "blendPath": str(request["blendPath"]),
                   "sourceSha256": source_hash, "sourceUnchanged": source_hash == _hash_file(request["blendPath"]),
                   "blenderVersion": bpy.app.version_string, "elapsedSeconds": round(time.monotonic() - started, 3)})
    if not report["sourceUnchanged"]:
        _issue(report["issues"], "error", "source_changed", "The source .blend changed during review")
        report["valid"] = False
    atomic_report(request["reportPath"], report)
    print(json.dumps({"reportPath": str(request["reportPath"]), "valid": report["valid"],
                      "images": len(report["images"]), "issues": len(report["issues"])}), flush=True)
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path, help="Workspace JSON request file")
    args = parser.parse_args(arguments)
    report = run_request(args.request)
    if not report["valid"]:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
