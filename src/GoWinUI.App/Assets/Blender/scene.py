"""Replace the authoring section with your design; run from the selected workspace."""
from pathlib import Path
import sys
import bpy

sys.dont_write_bytecode = True
PROJECT = Path(__file__).resolve().parent
sys.path.insert(0, str(PROJECT))
import go_blender as g

# For a follow-up, replace clear_scene with open_mainfile of design.json currentScene.
g.clear_scene()
bpy.context.scene.unit_settings.system = 'METRIC'
bpy.context.scene.unit_settings.scale_length = 1.0

# AUTHOR YOUR MODEL HERE. Use named components, materials and reusable functions.
# Parts = g.collection('ComponentName')
# finish = g.material('Finish', (0.55, 0.65, 0.75, 1), metallic=0.2)
# g.box('PartName', size=(2, 3, 1), location=(0, 0, 0.5), material=finish, collection=Parts)

# Framing validates actual visible geometry, including curves, text and collection
# instances. An untouched scaffold still fails because the scene is empty.
g.setup_review_scene()
g.save_revision(PROJECT / 'scene-v001.blend')
