"""Builds a material test scene as glTF, with every texture generated here (no outside images):

- a stone-tile ground with base colour, normal, roughness and occlusion maps
- a row of dielectric and a row of metal spheres, roughness from smooth to rough
- an alpha-masked, double-sided leaf card
- an emissive cube (KHR_materials_emissive_strength)
- a tinted glass panel (alpha blend)

    blender --background --factory-startup --python tools/make_material_test.py -- Assets/material_test.glb
"""
import math
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else "material_test.glb"

bpy.ops.wm.read_factory_settings(use_empty=True)
rng = np.random.default_rng(7)

# --- Generated images ---------------------------------------------------------------------------

def image(name, pixels, colour=True):
    """A packed Blender image from an (h, w, 4) float array, top row first."""
    h, w, _ = pixels.shape
    img = bpy.data.images.new(name, w, h, alpha=True)
    img.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(pixels[::-1]).ravel().astype(np.float32))
    img.pack()
    return img


def stone_tiles(size=512, tiles=4):
    """Height, base colour and crack mask for a grid of slightly uneven stone tiles."""
    y, x = np.mgrid[0:size, 0:size] / size * tiles
    cell_x, cell_y = np.floor(x), np.floor(y)
    fx, fy = x - cell_x, y - cell_y
    edge = np.minimum(np.minimum(fx, 1 - fx), np.minimum(fy, 1 - fy))
    mortar = np.clip(edge / 0.06, 0, 1)
    tint = rng.uniform(0.75, 1.1, (tiles, tiles))[cell_y.astype(int) % tiles, cell_x.astype(int) % tiles]
    noise = rng.normal(0, 1, (size // 8, size // 8))
    noise = np.kron(noise, np.ones((8, 8)))
    for _ in range(3):  # blur
        noise = (noise + np.roll(noise, 1, 0) + np.roll(noise, -1, 0) + np.roll(noise, 1, 1) + np.roll(noise, -1, 1)) / 5
    height = mortar * (0.85 + 0.15 * np.tanh(noise)) * np.minimum(1, edge * 12 + 0.6)
    stone = np.stack([0.42, 0.39, 0.35]) * tint[..., None] * (0.85 + 0.15 * np.tanh(noise))[..., None]
    grout = np.array([0.18, 0.17, 0.16])
    colour = stone * mortar[..., None] + grout * (1 - mortar[..., None])
    return height, colour, mortar


def normal_from_height(height, strength=6.0):
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    n = np.stack([-dx, dy, np.ones_like(height)], -1)  # +Y up in the image (OpenGL / glTF convention)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def rgba(rgb, a=1.0):
    return np.concatenate([rgb, np.full(rgb.shape[:2] + (1,), a)], -1)


height, colour, mortar = stone_tiles()
ground_base = image("ground_base", rgba(colour))
ground_normal = image("ground_normal", rgba(normal_from_height(height)), colour=False)
roughness = 0.55 + 0.4 * (1 - mortar)
# glTF metallic-roughness: G = roughness, B = metallic
ground_mr = image("ground_mr", rgba(np.stack([np.zeros_like(roughness), roughness, np.zeros_like(roughness)], -1)), colour=False)
occlusion = 0.35 + 0.65 * mortar
ground_ao = image("ground_ao", rgba(np.stack([occlusion] * 3, -1)), colour=False)

# A leaf: soft-edged ellipse with a midrib, alpha 0 outside
size = 256
y, x = np.mgrid[0:size, 0:size] / size * 2 - 1
inside = (x / 0.55) ** 2 + (y / 0.95) ** 2 < 1
leaf = np.zeros((size, size, 4))
leaf[..., 0:3] = np.array([0.20, 0.42, 0.12]) * (0.8 + 0.2 * np.cos(x * 9))[..., None]
leaf[np.abs(x) < 0.03, 0:3] = [0.35, 0.50, 0.20]
leaf[..., 3] = inside.astype(float)
leaf_base = image("leaf_base", leaf)

# --- Materials ----------------------------------------------------------------------------------

def material(name, base=(0.8, 0.8, 0.8, 1), metallic=0.0, roughness=0.5):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = base
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    m.use_backface_culling = True  # single-sided unless said otherwise
    return m, bsdf


def tex_node(m, img, input_socket, colour_space=None, channel=None):
    nodes, links = m.node_tree.nodes, m.node_tree.links
    t = nodes.new("ShaderNodeTexImage")
    t.image = img
    if channel is None:
        links.new(t.outputs["Color"], input_socket)
    else:
        sep = nodes.new("ShaderNodeSeparateColor")
        links.new(t.outputs["Color"], sep.inputs["Color"])
        links.new(sep.outputs[channel], input_socket)
    return t


ground, bsdf = material("Stone tiles")
tex_node(ground, ground_base, bsdf.inputs["Base Color"])
tex_node(ground, ground_mr, bsdf.inputs["Roughness"], channel="Green")
nm = ground.node_tree.nodes.new("ShaderNodeNormalMap")
tex_node(ground, ground_normal, nm.inputs["Color"])
ground.node_tree.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
# Occlusion: the glTF exporter picks it up from a "glTF Material Output" group's Occlusion input
group = bpy.data.node_groups.new("glTF Material Output", "ShaderNodeTree")
group.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
gnode = ground.node_tree.nodes.new("ShaderNodeGroup")
gnode.node_tree = group
tex_node(ground, ground_ao, gnode.inputs["Occlusion"], channel="Red")

leaf_mat, bsdf = material("Leaf", roughness=0.6)
t = tex_node(leaf_mat, leaf_base, bsdf.inputs["Base Color"])
# Alpha through a Round node: the glTF exporter writes that as alphaMode MASK, cutoff 0.5
cut = leaf_mat.node_tree.nodes.new("ShaderNodeMath")
cut.operation = "ROUND"
leaf_mat.node_tree.links.new(t.outputs["Alpha"], cut.inputs[0])
leaf_mat.node_tree.links.new(cut.outputs[0], bsdf.inputs["Alpha"])
leaf_mat.use_backface_culling = False

glow, bsdf = material("Glow", base=(0.05, 0.05, 0.05, 1), roughness=0.4)
bsdf.inputs["Emission Color"].default_value = (1.0, 0.55, 0.2, 1)
bsdf.inputs["Emission Strength"].default_value = 6.0

glass, bsdf = material("Glass", base=(0.55, 0.75, 0.85, 1), roughness=0.05)
bsdf.inputs["Alpha"].default_value = 0.35
glass.surface_render_method = "BLENDED"
glass.use_backface_culling = False

# --- Objects ------------------------------------------------------------------------------------

def add(obj_data, name, location, mat):
    obj = bpy.data.objects.new(name, obj_data)
    obj.location = location
    obj_data.materials.append(mat)
    bpy.context.scene.collection.objects.link(obj)
    return obj


bpy.ops.mesh.primitive_plane_add(size=12, location=(0, 0, 0))
plane = bpy.context.active_object
plane.name = "Ground"
plane.data.materials.append(ground)
# Tile the textures three times across the ground
for loop in plane.data.uv_layers.active.data:
    loop.uv = (loop.uv[0] * 3, loop.uv[1] * 3)

for row, metal in enumerate((0.0, 1.0)):
    for i in range(5):
        r = 0.1 + i * 0.2
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.45, segments=48, ring_count=24, location=(-2.4 + i * 1.2, -2.5 - row * 1.2, 0.45))
        sphere = bpy.context.active_object
        sphere.name = f"{'Metal' if metal else 'Dielectric'} r{r:.1f}"
        bpy.ops.object.shade_smooth()
        m, b = material(sphere.name, base=(0.95, 0.64, 0.54, 1) if metal else (0.8, 0.1, 0.1, 1), metallic=metal, roughness=r)
        sphere.data.materials.append(m)

bpy.ops.mesh.primitive_plane_add(size=1.4, location=(3.2, -2.5, 0.9), rotation=(math.pi / 2, 0, 0.4))
leafobj = bpy.context.active_object
leafobj.name = "Leaf card"
leafobj.data.materials.append(leaf_mat)

bpy.ops.mesh.primitive_cube_add(size=0.6, location=(3.2, -4.0, 0.3))
cube = bpy.context.active_object
cube.name = "Glow cube"
cube.data.materials.append(glow)

bpy.ops.mesh.primitive_plane_add(size=1.6, location=(0, -5.2, 0.8), rotation=(math.pi / 2, 0, 0))
pane = bpy.context.active_object
pane.name = "Glass pane"
pane.data.materials.append(glass)

bpy.ops.export_scene.gltf(
    filepath=OUT,
    export_format="GLB",
    export_tangents=True,
    export_yup=True,
    export_apply=True,
)
print(f"wrote {OUT}")
