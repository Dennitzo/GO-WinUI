"""Small, general-purpose Blender authoring helpers (Blender 4.2+).

These helpers deliberately return ordinary bpy objects. Combine them with any
bpy modelling operation; they are building blocks, not a model template.
Coordinates use metres, rotations radians, and sizes are full dimensions.
"""
from __future__ import annotations

import math
from pathlib import Path

try:
    import bpy
    from mathutils import Vector
except ImportError:  # Allows documentation/help tools to import this module.
    bpy = None
    Vector = None

MAX_OBJECTS = 10000
MAX_VERTICES = 1000000
VIEWS = ("perspective", "front", "right", "top", "back", "left")


def _blender():
    if bpy is None:
        raise RuntimeError("Run this script with Blender's Python interpreter.")


def _vector(value, name="vector", positive=False):
    values = tuple(float(v) for v in value)
    if len(values) != 3 or not all(math.isfinite(v) for v in values):
        raise ValueError(f"{name} must contain three finite numbers")
    if positive and not all(v > 0 for v in values):
        raise ValueError(f"{name} dimensions must be positive")
    return values


def _positive(value, name):
    value = float(value)
    if not math.isfinite(value) or value <= 0:
        raise ValueError(f"{name} must be positive and finite")
    return value


def _integer(value, name, low, high):
    if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
        raise ValueError(f"{name} must be an integer between {low} and {high}")
    return value


def _budget():
    _blender()
    if len(bpy.context.scene.objects) >= MAX_OBJECTS:
        raise ValueError(f"Scene exceeds the helper object budget ({MAX_OBJECTS})")


def workspace_path(path, suffix=None, must_exist=False):
    """Resolve a path inside the current workspace, including symlink checks."""
    root = Path.cwd().resolve()
    result = Path(path).expanduser().resolve()
    if not result.is_relative_to(root) or result == root:
        raise ValueError("Path must remain inside the current workspace")
    if suffix and result.suffix.lower() != suffix:
        raise ValueError(f"Path must end in {suffix}")
    if must_exist and not result.is_file():
        raise FileNotFoundError(result)
    return result


def clear_scene():
    """Remove scene objects. Use only when beginning a new design."""
    _blender()
    for obj in list(bpy.context.scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def collection(name, parent=None):
    """Get/create a component without silently changing an existing hierarchy.

    Existing collections keep their parents. An explicit conflicting parent is
    rejected; deliberate reparenting remains available through Blender's API.
    """
    _blender()
    result = bpy.data.collections.get(str(name))
    if result is not None:
        if parent is not None:
            target = bpy.data.collections.get(parent) if isinstance(parent, str) else parent
            if target is None or target == result or result.name not in target.children:
                raise ValueError(f"Collection {name!r} already exists with another parent; explicit reparenting is required")
        result["go_component"] = True
        return result
    if isinstance(parent, str) and parent == str(name):
        raise ValueError("A collection cannot be its own parent")
    target = collection(parent) if isinstance(parent, str) else (parent or bpy.context.scene.collection)
    result = bpy.data.collections.new(str(name))
    target.children.link(result)
    result["go_component"] = True
    return result


def material(name, color=(0.5, 0.5, 0.5, 1.0), metallic=0.0, roughness=0.45):
    """Create/update a named Principled BSDF material using linear RGBA."""
    _blender()
    rgba = tuple(float(v) for v in color)
    if len(rgba) == 3:
        rgba += (1.0,)
    values = (*rgba, float(metallic), float(roughness))
    if len(rgba) != 4 or not all(math.isfinite(v) and 0 <= v <= 1 for v in values):
        raise ValueError("Material colors, metallic and roughness must be in [0, 1]")
    result = bpy.data.materials.get(str(name)) or bpy.data.materials.new(str(name))
    result.diffuse_color = rgba
    result.use_nodes = True
    shader = next((n for n in result.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if shader is None:
        shader = result.node_tree.nodes.new("ShaderNodeBsdfPrincipled")
        output = result.node_tree.nodes.new("ShaderNodeOutputMaterial")
        result.node_tree.links.new(shader.outputs["BSDF"], output.inputs["Surface"])
    shader.inputs["Base Color"].default_value = rgba
    shader.inputs["Metallic"].default_value = float(metallic)
    shader.inputs["Roughness"].default_value = float(roughness)
    shader.inputs["Alpha"].default_value = rgba[3]
    return result


def tag_component(obj, component, role=None):
    """Move an object to a named collection and attach editable semantic tags."""
    target = collection(component) if isinstance(component, str) else component
    if target is not None:
        for previous in list(obj.users_collection):
            if previous != target:
                previous.objects.unlink(obj)
        if obj.name not in target.objects:
            target.objects.link(obj)
        obj["go_component"] = target.name
    if role:
        obj["go_role"] = str(role)
    return obj


def _finish(obj, name, material_value, component, bevel=0.0):
    obj.name = str(name)
    if material_value is not None:
        obj.data.materials.append(material_value)
    if component is not None:
        tag_component(obj, component)
    if bevel:
        width = _positive(bevel, "bevel")
        modifier = obj.modifiers.new("Edge highlights", "BEVEL")
        modifier.width = width
        modifier.segments = 3
        modifier.limit_method = "ANGLE"
    return obj


def box(name, size=(1, 1, 1), location=(0, 0, 0), bevel=0.04,
        rotation=(0, 0, 0), material=None, collection=None):
    _budget()
    dimensions = _vector(size, "size", positive=True)
    bpy.ops.mesh.primitive_cube_add(size=1, location=_vector(location), rotation=_vector(rotation))
    obj = bpy.context.object
    # Bake dimensions into the mesh: bevel remains in world units.
    for vertex in obj.data.vertices:
        vertex.co = tuple(vertex.co[i] * dimensions[i] for i in range(3))
    return _finish(obj, name, material, collection, bevel)


def cylinder(name, radius=1, depth=2, location=(0, 0, 0), vertices=48,
             bevel=0.02, rotation=(0, 0, 0), material=None, collection=None):
    _budget()
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=_integer(vertices, "vertices", 3, 512), radius=_positive(radius, "radius"),
        depth=_positive(depth, "depth"), location=_vector(location), rotation=_vector(rotation))
    return _finish(bpy.context.object, name, material, collection, bevel)


def sphere(name, radius=1, location=(0, 0, 0), scale=(1, 1, 1),
           material=None, collection=None):
    _budget()
    dimensions = _vector(scale, "scale", positive=True)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24,
                                       radius=_positive(radius, "radius"), location=_vector(location))
    obj = bpy.context.object
    for vertex in obj.data.vertices:
        vertex.co = tuple(vertex.co[i] * dimensions[i] for i in range(3))
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    return _finish(obj, name, material, collection)


def beam(name, start, end, width=0.1, depth=None, material=None, collection=None):
    """Rectangular beam aligned between arbitrary 3D endpoints."""
    _blender()
    a, b = Vector(_vector(start)), Vector(_vector(end))
    direction = b - a
    length = _positive(direction.length, "beam length")
    width = _positive(width, "width")
    depth = width if depth is None else _positive(depth, "depth")
    obj = box(name, (width, depth, length), (a + b) * 0.5,
              bevel=min(width, depth) * 0.08, material=material, collection=collection)
    obj.rotation_euler = direction.to_track_quat("Z", "Y").to_euler()
    return obj


def tube_curve(name, points, radius=0.05, closed=False, material=None, collection=None):
    """A capped polyline tube for pipes, cables, rails, seams and organic paths."""
    _budget()
    coordinates = [_vector(point, "point") for point in points]
    if not 2 <= len(coordinates) <= 10000:
        raise ValueError("A tube needs between 2 and 10000 points")
    data = bpy.data.curves.new(str(name), "CURVE")
    data.dimensions = "3D"
    data.resolution_u = 2
    data.bevel_depth = _positive(radius, "radius")
    data.bevel_resolution = 3
    data.use_fill_caps = True
    spline = data.splines.new("POLY")
    spline.points.add(len(coordinates) - 1)
    for point, coordinate in zip(spline.points, coordinates):
        point.co = (*coordinate, 1)
    spline.use_cyclic_u = bool(closed)
    obj = bpy.data.objects.new(str(name), data)
    bpy.context.scene.collection.objects.link(obj)
    return _finish(obj, name, material, collection)


def mesh(name, vertices, faces, material=None, collection=None):
    """Create arbitrary polygon geometry, validating indices before allocation."""
    _budget()
    if not 3 <= len(vertices) <= MAX_VERTICES or not 1 <= len(faces) <= MAX_VERTICES:
        raise ValueError("Mesh exceeds helper geometry budget or is empty")
    coordinates = [_vector(point, "vertex") for point in vertices]
    polygons = []
    for face in faces:
        indices = tuple(face)
        if len(indices) < 3 or len(set(indices)) != len(indices):
            raise ValueError("Each face needs at least three distinct vertices")
        if any(isinstance(index, bool) or not isinstance(index, int) or
               index < 0 or index >= len(coordinates) for index in indices):
            raise ValueError("Face vertex index is out of range")
        polygons.append(indices)
    data = bpy.data.meshes.new(str(name))
    data.from_pydata(coordinates, [], polygons)
    data.update()
    obj = bpy.data.objects.new(str(name), data)
    bpy.context.scene.collection.objects.link(obj)
    return _finish(obj, name, material, collection)


def ground(size=20, z=0, material=None):
    """Optional presentation ground, excluded from model framing/diagnostics."""
    obj = box("Review ground", (_positive(size, "size"), size, 0.04),
              location=(0, 0, float(z) - 0.02), bevel=0, material=material,
              collection="Presentation")
    obj["go_environment"] = True
    return obj


def render_visible_objects(view_layer=None):
    """Object names reachable through a render-visible collection path.

    Viewport hiding does not hide an object in a render. An excluded layer
    collection does; an object linked through another visible path remains visible.
    """
    _blender()
    result = set()

    def visit(layer):
        if layer.exclude or layer.collection.hide_render:
            return
        result.update(obj.name for obj in layer.collection.objects if not obj.hide_render)
        for child in layer.children:
            visit(child)

    visit((view_layer or bpy.context.view_layer).layer_collection)
    return result


def render_instance_owners(visible=None):
    """Expand visible owners through render-visible nested collection instances."""
    _blender()
    result = set(render_visible_objects() if visible is None else visible)
    pending = [obj.instance_collection for obj in bpy.context.scene.objects
               if obj.name in result and obj.instance_type == "COLLECTION" and obj.instance_collection]
    visited = set()
    while pending:
        component = pending.pop()
        if component.as_pointer() in visited or component.hide_render:
            continue
        visited.add(component.as_pointer())
        if len(visited) > MAX_OBJECTS:
            raise ValueError(f"Scene exceeds the helper collection budget ({MAX_OBJECTS})")
        pending.extend(component.children)
        for obj in component.objects:
            if not obj.hide_render and obj.instance_type == "COLLECTION" and obj.instance_collection:
                result.add(obj.name)
                pending.append(obj.instance_collection)
    return result


def scene_bounds():
    """World bounds of visible model geometry, excluding presentation objects."""
    _blender()
    bpy.context.view_layer.update()
    visible = render_instance_owners()
    points = []
    for index, instance in enumerate(bpy.context.evaluated_depsgraph_get().object_instances):
        if index >= MAX_OBJECTS:
            raise ValueError(f"Scene exceeds the helper instance budget ({MAX_OBJECTS})")
        obj = instance.object
        owner = instance.parent.original if instance.is_instance and instance.parent else obj.original
        if obj.type not in {"MESH", "CURVE", "SURFACE", "META", "FONT"}:
            continue
        if owner.name not in visible or obj.hide_render or obj.get("go_environment", False):
            continue
        for corner in obj.bound_box:
            point = instance.matrix_world @ Vector(corner)
            if not all(math.isfinite(value) for value in point):
                raise ValueError(f"Non-finite bounding coordinates: {obj.name}")
            points.append(point)
    if not points:
        raise ValueError("No visible model geometry to frame")
    minimum = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    maximum = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return minimum, maximum


def frame_camera(view="perspective", bounds=None):
    """Frame every bbox corner; cardinal views are orthographic for comparison."""
    _blender()
    if view not in VIEWS:
        raise ValueError(f"view must be one of {VIEWS}")
    lower, upper = bounds or scene_bounds()
    lower, upper = Vector(lower), Vector(upper)
    center = (lower + upper) * 0.5
    extent = max((upper - lower).length, 0.01)
    direction = Vector({"perspective": (1.2, -1.6, 1.1), "front": (0, -1, 0),
                        "right": (1, 0, 0), "top": (0, 0, 1), "back": (0, 1, 0),
                        "left": (-1, 0, 0)}[view]).normalized()
    existing = next((obj for obj in bpy.context.scene.objects
                     if obj.type == "CAMERA" and obj.get("go_review_camera", False)), None)
    if existing is None:
        data = bpy.data.cameras.new("GO review camera")
        existing = bpy.data.objects.new("GO review camera", data)
        bpy.context.scene.collection.objects.link(existing)
        existing["go_review_camera"] = True
        existing["go_environment"] = True
    camera = existing
    # Looking straight down makes track-quaternion roll ambiguous. Identity looks
    # along world -Z with world +X at image right and world +Y at image top.
    camera.rotation_euler = ((0, 0, 0) if view == "top"
                             else (-direction).to_track_quat("-Z", "Y").to_euler())
    camera.data.lens = 50
    camera.data.sensor_width = 36
    camera.data.sensor_fit = "HORIZONTAL"
    camera.data.type = "PERSP" if view == "perspective" else "ORTHO"
    # A sphere around the box makes perspective fitting independent of view.
    distance = extent * 0.5 / math.sin(math.atan(18 / 50)) * 1.2
    camera.location = center + direction * distance
    inverse = camera.rotation_euler.to_matrix().transposed()
    corners = [inverse @ (Vector((x, y, z)) - center)
               for x in (lower.x, upper.x) for y in (lower.y, upper.y) for z in (lower.z, upper.z)]
    span_x = max(v.x for v in corners) - min(v.x for v in corners)
    span_y = max(v.y for v in corners) - min(v.y for v in corners)
    render = bpy.context.scene.render
    aspect = (render.resolution_x * render.pixel_aspect_x) / max(1, render.resolution_y * render.pixel_aspect_y)
    camera.data.ortho_scale = max(span_x, span_y * aspect, 0.01) * 1.18
    camera.data.clip_start = max(extent / 10000, 0.00001)
    camera.data.clip_end = max(distance + extent * 10, 10)
    camera.data.dof.use_dof = False
    bpy.context.scene.camera = camera
    return camera


def setup_review_scene(view="perspective", resolution=768, samples=32, bounds=None):
    """Set a neutral EEVEE studio rig in memory, preserving model geometry."""
    _blender()
    resolution = _integer(resolution, "resolution", 128, 2048)
    samples = _integer(samples, "samples", 1, 128)
    scene = bpy.context.scene
    for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            scene.render.engine = engine
            break
        except (TypeError, ValueError):
            continue
    else:
        raise RuntimeError("This Blender installation has no EEVEE renderer")
    scene.render.resolution_x = resolution
    scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.render.use_file_extension = True
    scene.render.use_compositing = False
    scene.render.use_sequencer = False
    scene.render.use_border = False
    scene.render.pixel_aspect_x = 1
    scene.render.pixel_aspect_y = 1
    scene.render.use_simplify = True
    scene.render.simplify_subdivision_render = 4
    if hasattr(scene, "eevee"):
        for attribute in ("taa_render_samples", "taa_samples"):
            if hasattr(scene.eevee, attribute):
                setattr(scene.eevee, attribute, samples)
    try:
        scene.view_settings.view_transform = "AgX"
        scene.view_settings.look = "AgX - Medium High Contrast"
    except (TypeError, ValueError):
        pass
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    world = bpy.data.worlds.new("GO neutral review world")
    world.use_nodes = True
    world.node_tree.nodes.get("Background").inputs["Color"].default_value = (0.12, 0.14, 0.18, 1)
    world.node_tree.nodes.get("Background").inputs["Strength"].default_value = 0.6
    scene.world = world
    lower, upper = bounds or scene_bounds()
    center = (Vector(lower) + Vector(upper)) * 0.5
    extent = max((Vector(upper) - Vector(lower)).length, 0.1)
    for obj in list(scene.objects):
        if obj.get("go_review_light", False):
            bpy.data.objects.remove(obj, do_unlink=True)
        elif obj.type == "LIGHT":
            obj.hide_render = True
    for name, offset, energy, size in (
        ("Key", (0.7, -0.9, 1.1), 80, 0.6),
        ("Fill", (-0.9, -0.3, 0.5), 45, 0.8),
        ("Rim", (0.2, 0.8, 1.0), 100, 0.5),
    ):
        data = bpy.data.lights.new(f"GO {name}", "AREA")
        data.energy = energy * extent * extent
        data.shape = "DISK"
        data.size = extent * size
        obj = bpy.data.objects.new(f"GO {name}", data)
        scene.collection.objects.link(obj)
        obj.location = center + Vector(offset) * extent
        obj.rotation_euler = (center - obj.location).to_track_quat("-Z", "Y").to_euler()
        obj["go_review_light"] = True
        obj["go_environment"] = True
    return frame_camera(view, (lower, upper))


def save_revision(path):
    """Save a NEW .blend inside cwd; never silently replace an earlier revision."""
    _blender()
    target = workspace_path(path, suffix=".blend")
    target.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive reservation protects against both accidental and concurrent reuse.
    with target.open("xb"):
        pass
    previous_versions = bpy.context.preferences.filepaths.save_version
    try:
        bpy.context.preferences.filepaths.save_version = 0
        result = bpy.ops.wm.save_as_mainfile(filepath=str(target), check_existing=False)
        if "FINISHED" not in result or target.stat().st_size == 0:
            raise RuntimeError("Blender did not finish saving the revision")
    except BaseException:
        if target.exists() and target.stat().st_size == 0:
            target.unlink()
        raise
    finally:
        bpy.context.preferences.filepaths.save_version = previous_versions
    return str(target)
