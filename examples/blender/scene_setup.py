import bpy
import os

BASE = os.path.dirname(os.path.abspath(__file__))

# Saubere Szene: alle vorhandenen Objekte und Datenblöcke entfernen
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
for block in (bpy.data.meshes, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
    for item in list(block):
        if item.users == 0:
            block.remove(item)

# ---- Hellgraue Fläche (Boden) ----
bpy.ops.mesh.primitive_plane_add(size=10, location=(0, 0, 0))
floor = bpy.context.active_object
floor.name = "Floor"
floor_mat = bpy.data.materials.new("FloorMat")
floor_mat.use_nodes = True
floor_mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.7, 0.7, 0.7, 1.0)
floor.data.materials.append(floor_mat)

# ---- Blauer Wuerfel ----
bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0.5))
cube = bpy.context.active_object
cube.name = "BlueCube"
cube_mat = bpy.data.materials.new("BlueMat")
cube_mat.use_nodes = True
cube_mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.0, 0.15, 0.9, 1.0)
cube.data.materials.append(cube_mat)

# ---- Kamera ----
cam_data = bpy.data.cameras.new("Camera")
cam = bpy.data.objects.new("Camera", cam_data)
bpy.context.collection.objects.link(cam)
cam.location = (3.5, -3.5, 3.0)
# Kamera auf den Wuerfel ausrichten (Track-To-Constraint)
from mathutils import Vector
direction = Vector((0, 0, 0.5)) - cam.location
rot_quat = direction.to_track_quat("-Z", "Y")
cam.rotation_euler = rot_quat.to_euler()
bpy.context.scene.camera = cam

# ---- Licht (Sonnenlicht) ----
light_data = bpy.data.lights.new("SunLight", type="SUN")
light_data.energy = 3.0
light = bpy.data.objects.new("SunLight", light_data)
bpy.context.collection.objects.link(light)
light.location = (5, 5, 10)
light.rotation_euler = rot_quat.to_euler()

# ---- Render-Einstellungen ----
scene = bpy.context.scene
scene.render.resolution_x = 1280
scene.render.resolution_y = 720
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.filepath = os.path.join(BASE, "render.png")

# Szene speichern
blend_path = os.path.join(BASE, "scene.blend")
bpy.ops.wm.save_as_mainfile(filepath=blend_path)

# Rendern
bpy.ops.render.render(write_still=True)

print("Fertig: " + blend_path)
print("Render: " + scene.render.filepath)
