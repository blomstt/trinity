// The lit surface: glTF metallic-roughness materials, GGX specular and Lambert diffuse, lit by the
// sun and the sky. Included by mesh.frag (bindless) and mesh_fallback.frag.

#include "frame.glsl"
#include "material.glsl"
#include "lighting.glsl"
#include "fog.glsl"

layout(constant_id = 0) const bool ALPHA_MASK = false;
layout(constant_id = 2) const bool ALPHA_BLEND = false;

layout(location = 0) in vec3 inWorld;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) flat in uint inMaterial;
layout(location = 5) flat in uint inFlags;

layout(location = 0) out vec4 outColor;

void main() {
    Material m = materials[inMaterial];
    vec4 base = m.baseColor * texture(TEX(m.tex0.x), inUv);
    if (ALPHA_MASK && base.a < m.params.z)
        discard;

    // Normal: the tangent-space map, flipped for back faces of double-sided materials
    vec3 n = normalize(inNormal);
    vec3 t = inTangent.xyz - n * dot(n, inTangent.xyz);
    t = dot(t, t) > 1e-8 ? normalize(t) : vec3(1.0, 0.0, 0.0);
    vec3 b = cross(n, t) * (inTangent.w < 0.0 ? -1.0 : 1.0);
    vec3 tn = texture(TEX(m.tex0.z), inUv).xyz * 2.0 - 1.0;
    tn.xy *= m.emissive.w;
    n = normalize(mat3(t, b, n) * tn);
    if (!gl_FrontFacing)
        n = -n;

    vec4 mr = texture(TEX(m.tex0.y), inUv);
    float metallic = clamp(m.params.x * mr.b, 0.0, 1.0);
    float roughness = clamp(m.params.y * mr.g, 0.045, 1.0);
    float ao = mix(1.0, texture(TEX(m.tex0.w), inUv).r, m.params.w);
    vec3 emissive = m.emissive.rgb * texture(TEX(m.tex1.x), inUv).rgb;

    Surface s = Surface(inWorld, n, base.rgb, metallic, roughness, ao);
    vec3 color = applyFog(shade(s) + emissive, inWorld);

    // Highlighted objects (the editor's selection) get a bronze rim
    if ((inFlags & 1u) != 0u) {
        vec3 v = normalize(frame.cameraPos.xyz - inWorld);
        color += vec3(0.88, 0.55, 0.20) * pow(1.0 - max(dot(n, v), 0.0), 3.0) * 2.0;
    }

    int debugView = int(frame.params.z);
    if (debugView == 1) color = base.rgb;
    else if (debugView == 2) color = n * 0.5 + 0.5;
    else if (debugView == 3) color = cascadeColor(inWorld) * (0.25 + 0.75 * sunShadow(inWorld, n, -(frame.view * vec4(inWorld, 1.0)).z));
    outColor = vec4(color, ALPHA_BLEND ? base.a : 1.0);
}
