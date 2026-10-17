#version 450

// Shared by soldiers and ground: one sun, sky ambient, distance fog
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
const vec3 SUN = vec3(1.05, 0.95, 0.82);

void main() {
    vec3 n = normalize(inNormal);
    float diffuse = max(dot(n, -frame.sunDir.xyz), 0.0);
    float sky = 0.5 + 0.5 * n.y;
    vec3 lit = inColor * (SUN * diffuse + SKY * 0.55 * sky + vec3(0.04));

    float dist = length(inWorld - frame.cameraPos.xyz);
    float fog = 1.0 - exp(-dist * frame.params.y);
    outColor = vec4(mix(lit, SKY, clamp(fog, 0.0, 1.0)), 1.0);
}
