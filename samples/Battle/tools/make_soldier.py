"""Builds the stand-in spearman as a rigged, animated glTF, the same shape of file a Meshy or
Tripo auto-rigged character exports as, so a real soldier can replace it later.

    blender --background --factory-startup --python tools/make_soldier.py -- Assets/soldier.glb [previews_dir]

Blender's axes: Z up, the character faces -Y. The glTF exporter turns that into Y up, facing +Z.
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else "soldier.glb"
PREVIEWS = argv[1] if len(argv) > 1 else None

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps = 30

# --- Skeleton -------------------------------------------------------------------------------

# name: (head, tail, parent)
BONES = {
    "hips": ((0, 0, 0.93), (0, 0, 1.05), None),
    "spine": ((0, 0, 1.05), (0, 0, 1.26), "hips"),
    "chest": ((0, 0, 1.26), (0, 0, 1.46), "spine"),
    "neck": ((0, 0, 1.46), (0, 0, 1.55), "chest"),
    "head": ((0, 0, 1.55), (0, 0, 1.80), "neck"),
}
# Arm points per side: shoulder, elbow, wrist, fist. Right arm holds the spear level, left holds the
# shield up in front. The rig's rest pose is this combat-ready stance.
ARMS = {
    "R": ((-0.19, 0, 1.42), (-0.22, -0.07, 1.17), (-0.22, -0.33, 1.13), (-0.22, -0.41, 1.13)),
    "L": ((0.19, 0, 1.42), (0.25, -0.06, 1.17), (0.20, -0.30, 1.21), (0.19, -0.37, 1.22)),
}
for side, s in (("L", 1), ("R", -1)):
    sh, el, wr, fi = ARMS[side]
    BONES |= {
        f"shoulder.{side}": ((s * 0.05, 0, 1.42), sh, "chest"),
        f"upper_arm.{side}": (sh, el, f"shoulder.{side}"),
        f"forearm.{side}": (el, wr, f"upper_arm.{side}"),
        f"hand.{side}": (wr, fi, f"forearm.{side}"),
        f"thigh.{side}": ((s * 0.10, 0, 0.93), (s * 0.10, -0.01, 0.50), "hips"),
        f"shin.{side}": ((s * 0.10, -0.01, 0.50), (s * 0.10, 0.02, 0.09), f"thigh.{side}"),
        f"foot.{side}": ((s * 0.10, 0.02, 0.09), (s * 0.10, -0.14, 0.02), f"shin.{side}"),
    }

arm_data = bpy.data.armatures.new("Rig")
rig = bpy.data.objects.new("Rig", arm_data)
scene.collection.objects.link(rig)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="EDIT")
for name, (head, tail, parent) in BONES.items():
    b = arm_data.edit_bones.new(name)
    b.head, b.tail, b.roll = Vector(head), Vector(tail), 0
for name, (_, _, parent) in BONES.items():
    if parent:
        arm_data.edit_bones[name].parent = arm_data.edit_bones[parent]
        arm_data.edit_bones[name].use_connect = (arm_data.edit_bones[name].head - arm_data.edit_bones[parent].tail).length < 1e-4
bpy.ops.object.mode_set(mode="OBJECT")

# --- Body: a skin-modifier mesh grown along the bones -----------------------------------------

# joint point: radius
JOINTS = {
    "pelvis": ((0, 0, 0.93), 0.15), "waist": ((0, 0, 1.08), 0.14), "chest": ((0, 0, 1.30), 0.17),
    "neck": ((0, 0, 1.48), 0.06), "head": ((0, -0.01, 1.66), 0.11), "crown": ((0, 0, 1.78), 0.08),
}
EDGES = [("pelvis", "waist"), ("waist", "chest"), ("chest", "neck"), ("neck", "head"), ("head", "crown")]
for side, s in (("L", 1), ("R", -1)):
    JOINTS |= {
        f"shoulder{side}": (ARMS[side][0], 0.065), f"elbow{side}": (ARMS[side][1], 0.05),
        f"wrist{side}": (ARMS[side][2], 0.04), f"fist{side}": (ARMS[side][3], 0.045),
        f"hip{side}": ((s * 0.10, 0, 0.90), 0.085), f"knee{side}": ((s * 0.10, -0.01, 0.50), 0.065),
        f"ankle{side}": ((s * 0.10, 0.02, 0.10), 0.05), f"toe{side}": ((s * 0.10, -0.13, 0.04), 0.045),
    }
    EDGES += [
        ("chest", f"shoulder{side}"), (f"shoulder{side}", f"elbow{side}"), (f"elbow{side}", f"wrist{side}"),
        (f"wrist{side}", f"fist{side}"), ("pelvis", f"hip{side}"), (f"hip{side}", f"knee{side}"),
        (f"knee{side}", f"ankle{side}"), (f"ankle{side}", f"toe{side}"),
    ]
names = list(JOINTS)
mesh = bpy.data.meshes.new("Body")
mesh.from_pydata([JOINTS[n][0] for n in names], [(names.index(a), names.index(b)) for a, b in EDGES], [])
body = bpy.data.objects.new("Soldier_LOD0", mesh)
scene.collection.objects.link(body)
skin = body.modifiers.new("Skin", "SKIN")
for i, n in enumerate(names):
    r = JOINTS[n][1]
    mesh.skin_vertices[0].data[i].radius = (r, r * (0.8 if n in ("chest", "waist", "pelvis") else 1.0))
mesh.skin_vertices[0].data[names.index("pelvis")].use_root = True
body.modifiers.new("Smooth", "SUBSURF").levels = 1
bpy.context.view_layer.objects.active = body
for m in list(body.modifiers):
    bpy.ops.object.modifier_apply(modifier=m.name)
bpy.ops.object.shade_smooth()


def material(name, rgb, team=False):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*rgb, 1)
    m.diffuse_color = (*rgb, 1)
    m["team"] = 1 if team else 0  # the loader tints these with the army colour
    return m


def srgb(c):
    return tuple(((x + 0.055) / 1.055) ** 2.4 if x > 0.04045 else x / 12.92 for x in c)


MATS = {
    "Skin": material("Skin", srgb((0.62, 0.47, 0.38))),
    "Cloth": material("Cloth", srgb((0.20, 0.17, 0.14))),
    "Tabard": material("Team_Tabard", srgb((0.75, 0.75, 0.75)), team=True),
    "Iron": material("Iron", srgb((0.40, 0.41, 0.43))),
    "Wood": material("Wood", srgb((0.30, 0.20, 0.11))),
    "Leather": material("Leather", srgb((0.24, 0.16, 0.09))),
    "Shield": material("Team_Shield", srgb((0.85, 0.85, 0.85)), team=True),
}
for m in MATS.values():
    mesh.materials.append(m)
order = list(MATS)
for poly in mesh.polygons:
    c = poly.center
    if c.z < 0.13:
        poly.material_index = order.index("Leather")  # boots
    elif c.z < 0.86:
        poly.material_index = order.index("Cloth")  # legs
    elif c.y < -0.29:
        poly.material_index = order.index("Skin")  # hands, held out in front
    elif c.z < 1.47:
        poly.material_index = order.index("Tabard")  # torso and sleeves
    else:
        poly.material_index = order.index("Skin")  # neck, face
    if 0.95 < c.z < 1.02 and abs(c.x) < 0.2:
        poly.material_index = order.index("Leather")  # belt

# Automatic weights from the bones (heat diffusion)
body.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.parent_set(type="ARMATURE_AUTO")
bpy.ops.object.select_all(action="DESELECT")

# --- Gear, each piece rigidly bound to one bone ----------------------------------------------


def gear(op, bone, mat, **kw):
    op(**kw)
    obj = bpy.context.active_object
    obj.data.materials.append(MATS[mat])
    vg = obj.vertex_groups.new(name=bone)
    vg.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
    return obj


parts = [
    gear(bpy.ops.mesh.primitive_uv_sphere_add, "head", "Iron", segments=12, ring_count=6, radius=0.125, location=(0, -0.005, 1.70), scale=(1, 1.05, 0.75)),
    gear(bpy.ops.mesh.primitive_cylinder_add, "head", "Iron", vertices=12, radius=0.135, depth=0.02, location=(0, -0.005, 1.665)),
    # Round shield strapped to the left forearm, facing forward
    gear(bpy.ops.mesh.primitive_cylinder_add, "forearm.L", "Shield", vertices=16, radius=0.31, depth=0.04, location=(0.17, -0.40, 1.12), rotation=(math.pi / 2, 0, 0)),
    gear(bpy.ops.mesh.primitive_uv_sphere_add, "forearm.L", "Iron", segments=8, ring_count=4, radius=0.06, location=(0.17, -0.43, 1.12), scale=(1, 0.5, 1)),
    # Spear through the right fist, level and pointing forward
    gear(bpy.ops.mesh.primitive_cylinder_add, "hand.R", "Wood", vertices=6, radius=0.017, depth=2.3, location=(-0.22, -0.55, 1.15), rotation=(math.pi / 2 + 0.06, 0, 0)),
    gear(bpy.ops.mesh.primitive_cone_add, "hand.R", "Iron", vertices=6, radius1=0.035, radius2=0, depth=0.22, location=(-0.22, -1.80, 1.22), rotation=(math.pi / 2 + 0.06, 0, 0)),
]
for p in parts:
    bpy.context.view_layer.objects.active = p
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.shade_flat()
bpy.ops.object.select_all(action="DESELECT")
for p in parts:
    p.select_set(True)
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.join()  # keeps every vertex group, so the gear follows its bone

# Far-away version: same rig, about a quarter of the triangles
lod1 = body.copy()
lod1.data = body.data.copy()
lod1.name = "Soldier_LOD1"
scene.collection.objects.link(lod1)
dec = lod1.modifiers.new("Decimate", "DECIMATE")
dec.ratio = 0.22
bpy.context.view_layer.objects.active = lod1
bpy.ops.object.modifier_move_to_index(modifier="Decimate", index=0)
bpy.ops.object.modifier_apply(modifier="Decimate")

# --- Animations -------------------------------------------------------------------------------

pose = rig.pose.bones
for pb in pose:
    pb.rotation_mode = "XYZ"
rig.animation_data_create()


def key(frame, rotations, hips_offset=(0, 0, 0)):
    """rotations: bone -> (x, y, z) degrees in the bone's own space. Unlisted bones go to rest."""
    for pb in pose:
        pb.rotation_euler = [math.radians(a) for a in rotations.get(pb.name, (0, 0, 0))]
        pb.keyframe_insert("rotation_euler", frame=frame)
    pose["hips"].location = hips_offset
    pose["hips"].keyframe_insert("location", frame=frame)


# In bone space, X rotation swings a downward limb: negative moves it forward
READY = {}  # the rest pose already is the ready stance


def clip(name, frames, loop=True):
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data.action = act
    for f, rot, hips in frames:
        key(f, rot, hips)
    track = rig.animation_data.nla_tracks.new()
    track.name = name
    track.strips.new(name, int(frames[0][0]), act)
    rig.animation_data.action = None


def merge(*dicts):
    out = {}
    for d in dicts:
        out.update(d)
    return out


clip("idle", [
    (1, merge(READY, {"chest": (2, 0, 0)}), (0, 0, 0)),
    (31, merge(READY, {"chest": (4, 0, 0), "head": (-3, 0, 0)}), (0, 0, -0.01)),
    (61, merge(READY, {"chest": (2, 0, 0)}), (0, 0, 0)),
])

# One stride per 30 frames (left foot forward, then right)
def walk(phase):
    s = math.sin(phase)
    knee = lambda p: max(0, math.sin(p)) * 45
    return merge(READY, {
        "thigh.L": (-28 * s, 0, 0), "shin.L": (knee(phase + math.pi), 0, 0),
        "thigh.R": (28 * s, 0, 0), "shin.R": (knee(phase), 0, 0),
        "upper_arm.L": (5 * s, 0, 0), "upper_arm.R": (-4 * s, 0, 0), "spine": (3, 0, 0), "chest": (0, 4 * s, 0),
    })


clip("walk", [(1 + i * 5, walk(i * math.pi / 3), (0, 0, -0.025 * abs(math.cos(i * math.pi / 3)))) for i in range(7)])

# Draw the spear back along the body, then drive it forward with the hips and shoulder
DRAW_BACK = {"chest": (0, 16, 0), "upper_arm.R": (35, 0, 0), "forearm.R": (-20, 0, 0), "thigh.R": (6, 0, 0)}
THRUST = {
    "spine": (6, 0, 0), "chest": (4, -22, 0), "upper_arm.R": (-50, 0, 0), "forearm.R": (40, 0, 0),
    "thigh.R": (14, 0, 0), "thigh.L": (-24, 0, 0), "shin.L": (18, 0, 0),
}
clip("attack", [(1, READY, (0, 0, 0)), (12, DRAW_BACK, (0, 0, 0)), (20, THRUST, (0, 0, -0.04)), (24, THRUST, (0, 0, -0.04)), (28, READY, (0, 0, 0))])

# Hips location is in the hips bone's own space: its Y runs up the body, so -Y lowers it.
# Rotating the hips by -X tips the torso backwards and swings the legs forward: lying flat.
FALLEN = {
    "hips": (-84, 0, 0), "head": (-15, 10, 0),
    "upper_arm.R": (60, 0, -30), "forearm.R": (60, 0, 0), "upper_arm.L": (50, 0, 40),
    "thigh.L": (-8, 0, 4), "thigh.R": (6, 0, -6), "shin.R": (12, 0, 0),
}
clip("death", [
    (1, {}, (0, 0, 0)),
    (9, {"hips": (-12, 0, 0), "chest": (-14, 0, 0), "head": (-20, 0, 0), "thigh.L": (-35, 0, 0), "shin.L": (60, 0, 0),
         "thigh.R": (-30, 0, 0), "shin.R": (55, 0, 0)}, (0, -0.32, 0)),
    (22, FALLEN, (0, -0.80, 0)),
    (33, FALLEN, (0, -0.81, 0)),
], loop=False)

# --- Export -----------------------------------------------------------------------------------

bpy.ops.object.select_all(action="DESELECT")
for o in (rig, body, lod1):
    o.select_set(True)
bpy.ops.export_scene.gltf(
    filepath=OUT,
    export_format="GLB",
    use_selection=True,
    export_apply=False,
    export_skins=True,
    export_animations=True,
    export_animation_mode="NLA_TRACKS",
    export_yup=True,
    export_extras=True,
)
print(f"wrote {OUT}: LOD0 {len(body.data.polygons)} faces, LOD1 {len(lod1.data.polygons)} faces")

# --- Optional previews of each clip, to check the poses ----------------------------------------

if PREVIEWS:
    import os

    os.makedirs(PREVIEWS, exist_ok=True)
    lod1.hide_render = True
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    cam.location = (2.6, -3.2, 1.6)
    cam.rotation_euler = (math.radians(80), 0, math.radians(39))
    scene.camera = cam
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x, scene.render.resolution_y = 480, 480
    for track in rig.animation_data.nla_tracks:
        track.mute = True
    for track in rig.animation_data.nla_tracks:
        track.mute = False
        strip = track.strips[0]
        for f in sorted({int(strip.frame_start), int((strip.frame_start + strip.frame_end) / 2), int(strip.frame_end)}):
            scene.frame_set(f)
            scene.render.filepath = os.path.join(PREVIEWS, f"{track.name}_{f:02d}.png")
            bpy.ops.render.render(write_still=True)
        track.mute = True
