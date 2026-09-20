"""One authoring stage. Run with blender.execute stage, which loads and saves revisions."""
from pathlib import Path
import sys
import bpy

sys.dont_write_bytecode = True
PROJECT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT))
import go_blender as g

# Add only this stage's geometry or targeted corrections below.
# The stage wrapper starts empty for a first stage, or loads baseScene for a follow-up.
# Do not clear the scene, open another file, save a .blend, or start a render here.
# Create a separate small script for each next stage and preserve accepted parts.
bpy.context.scene.unit_settings.system = "METRIC"
bpy.context.scene.unit_settings.scale_length = 1.0

# Example API (author your own design):
# finish = g.material("Finish", (0.55, 0.65, 0.75, 1), metallic=0.2)
# g.box("Component_Part", size=(2, 3, 1), location=(0, 0, 0.5), material=finish)
