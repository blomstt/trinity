#version 450
#extension GL_GOOGLE_include_directive : require

// HDR to the display: exposure, then a tone curve (AgX-style or ACES). The target is an sRGB format,
// so the hardware does the final sRGB encoding of these linear values. Debug views pass through.

#include "frame.glsl"

layout(set = 0, binding = 2) uniform sampler2D hdr;

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

// AgX-style: into a slightly desaturated working space (AgX's inset matrix), a log2 encoding over
// 16.5 stops, a smooth sigmoid around middle grey, then back out (the inverse matrix). Very bright
// colours roll towards white, as on film. The sigmoid is Trinity's own fit, not the reference curve.
vec3 agx(vec3 c) {
    const mat3 inset = mat3(
        0.842479062253094, 0.0423282422610123, 0.0423756549057051,
        0.0784335999999992, 0.878468636469772, 0.0784336,
        0.0792237451477643, 0.0791661274605434, 0.879142973793104);
    const mat3 outset = mat3(
        1.19687900512017, -0.0528968517574562, -0.0529716355144438,
        -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
        -0.0990297440797205, -0.0989611768448433, 1.15107367264116);
    const float minEv = -12.47393, maxEv = 4.026069;
    c = inset * max(c, vec3(1e-10));
    vec3 x = clamp((log2(c) - minEv) / (maxEv - minEv), 0.0, 1.0);
    // Logistic curve pivoted at middle grey, rescaled to pass through 0 and 1
    const float mid = 0.6060606, k = 6.8;
    vec3 y = 1.0 / (1.0 + exp(-k * (x - mid)));
    float y0 = 1.0 / (1.0 + exp(k * mid)), y1 = 1.0 / (1.0 + exp(-k * (1.0 - mid)));
    y = (y - y0) / (y1 - y0);
    // The curve's output is display-encoded (gamma 2.2); back to linear for the sRGB target
    return clamp(outset * pow(y, vec3(2.2)), 0.0, 1.0);
}

// Narkowicz's fit of the ACES filmic curve
vec3 aces(vec3 c) {
    c *= 0.6;
    return clamp((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14), 0.0, 1.0);
}

void main() {
    vec3 c = texture(hdr, inUv).rgb;
    int debugView = int(frame.params.z);
    if (debugView == 1 || debugView == 2) {
        outColor = vec4(c, 1.0);
        return;
    }
    c *= frame.params.y;
    outColor = vec4(frame.params.w > 0.5 ? aces(c) : agx(c), 1.0);
}
