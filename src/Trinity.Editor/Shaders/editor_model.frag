#version 450

layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params;
} frame;

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inWorld;
layout(location = 3) in float inSelected;

layout(location = 0) out vec4 outColor;

void main() {
    vec3 n = normalize(inNormal);
    float diffuse = max(dot(n, -frame.sunDir.xyz), 0.0);
    float sky = 0.5 + 0.5 * n.y;
    vec3 lit = inColor * (vec3(1.05, 0.95, 0.82) * diffuse + vec3(0.15, 0.19, 0.23) * 0.8 * sky + vec3(0.05));

    // Selected objects get a bronze rim
    if (inSelected > 0.5) {
        vec3 v = normalize(frame.cameraPos.xyz - inWorld);
        float rim = pow(1.0 - max(dot(n, v), 0.0), 3.0);
        lit += vec3(0.88, 0.55, 0.20) * rim * 1.5;
    }
    outColor = vec4(lit, 1.0);
}
