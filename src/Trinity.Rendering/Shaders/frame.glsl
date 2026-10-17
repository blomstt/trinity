// Per-view, per-frame data shared by every Trinity rendering shader (set 0), and the environment (set 3).

struct Instance {
    vec4 row0, row1, row2; // object-to-world, 3x4
    vec4 anim;             // clip index, seconds, loop (1/0), flags (bit 0: highlighted)
    uvec4 data;            // material override (0xFFFFFFFF: the part's own), unused
};

layout(std140, set = 0, binding = 0) uniform Frame {
    mat4 viewProj;
    mat4 view;
    mat4 invViewProj;
    mat4 shadow[4];       // world to each cascade's shadow map (xy 0..1, reversed depth)
    vec4 cameraPos;
    vec4 sunDir;          // xyz: direction sunlight travels (normalised)
    vec4 sunColor;        // rgb: sunlight reaching the ground (linear, HDR)
    vec4 cascadeSplits;   // view distance where each cascade ends
    vec4 shadowTexel;     // world size of a shadow texel, per cascade
    vec4 shadowParams;    // on (1/0), blend band (fraction of a cascade), normal bias (texels), resolution
    vec4 fog;             // density, height falloff, height, sky blend
    vec4 params;          // time, exposure multiplier, debug view, tone mapper
    vec4 grid;            // half size, centre x, centre z, unused
} frame;

layout(std430, set = 0, binding = 1) readonly buffer Instances {
    Instance instances[];
};

// Each pass draws only its visible instances: gl_InstanceIndex indexes this list, which points into instances
layout(std430, set = 0, binding = 4) readonly buffer Visible {
    uint visible[];
};

layout(set = 0, binding = 3) uniform sampler2DArrayShadow shadowMap;

layout(set = 3, binding = 0) uniform samplerCube environmentMap;
layout(std430, set = 3, binding = 1) readonly buffer SkyLight { vec4 sh[9]; } skyLight;
layout(set = 3, binding = 2) uniform sampler2D skyView;

mat3x4 instanceTransform(Instance i) { return mat3x4(i.row0, i.row1, i.row2); }
