#version 450

layout(set = 0, binding = 0) uniform sampler2D image;
layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

// The image exactly as authored: sRGB texture in, sRGB target out, no tinting
void main() {
    outColor = texture(image, inUv);
}
