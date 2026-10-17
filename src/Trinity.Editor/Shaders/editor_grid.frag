#version 450

layout(push_constant) uniform Frame {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDir;
    vec4 params;
} frame;

layout(location = 0) in vec3 inWorld;
layout(location = 0) out vec4 outColor;

// Anti-aliased lines every `spacing` metres
float lines(vec2 p, float spacing) {
    vec2 g = abs(fract(p / spacing - 0.5) - 0.5) / fwidth(p / spacing);
    return 1.0 - min(min(g.x, g.y), 1.0);
}

void main() {
    vec2 p = inWorld.xz;
    float minor = lines(p, 1.0) * 0.22;
    float major = lines(p, 10.0) * 0.5;
    vec3 color = vec3(0.30, 0.33, 0.38);
    float alpha = max(minor, major);

    // Red X axis, blue Z axis
    vec2 axis = abs(p) / fwidth(p);
    if (axis.y < 1.0) { color = vec3(0.75, 0.22, 0.20); alpha = 0.9; }
    if (axis.x < 1.0) { color = vec3(0.22, 0.38, 0.80); alpha = 0.9; }

    float fade = 1.0 - smoothstep(frame.params.y * 0.5, frame.params.y, length(p - frame.cameraPos.xz));
    outColor = vec4(pow(color, vec3(2.2)), alpha * fade);
}
