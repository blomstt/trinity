#version 450

layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params; // time, fog density
} frame;

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inWorld;

layout(location = 0) out vec4 outColor;

const vec3 SKY = vec3(0.149, 0.187, 0.231); // linear; sRGB (0.42, 0.47, 0.52)

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void main() {
    vec2 p = inWorld.xz;
    // Mossy, worn ground: dark green with patches of dry grass and mud
    float large = noise(p * 0.02) * 0.6 + noise(p * 0.09) * 0.3 + noise(p * 0.6) * 0.1;
    vec3 moss = vec3(0.14, 0.18, 0.09);
    vec3 dry = vec3(0.30, 0.27, 0.16);
    vec3 mud = vec3(0.17, 0.13, 0.09);
    vec3 c = mix(moss, dry, smoothstep(0.45, 0.75, large));
    c = mix(c, mud, smoothstep(0.62, 0.8, noise(p * 0.035 + 17.0)) * 0.7);

    c = pow(c, vec3(2.2)); // authored in sRGB
    float diffuse = max(dot(vec3(0, 1, 0), -frame.sunDir.xyz), 0.0);
    vec3 lit = c * (vec3(1.05, 0.95, 0.82) * diffuse + SKY * 0.55 + vec3(0.04));

    float dist = length(inWorld - frame.cameraPos.xyz);
    float fog = 1.0 - exp(-dist * frame.params.y);
    outColor = vec4(mix(lit, SKY, clamp(fog, 0.0, 1.0)), 1.0);
}
