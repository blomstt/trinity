// Trinity GPU skinning from a GpuBoneTable. Include from a vertex shader, then call
// skinMatrix(clip, seconds, loop, joints, weights) to get the 3x4 skinning matrix.

layout(std430, set = 0, binding = 0) readonly buffer TrinityBones {
    vec4 header;  // joint count, bake fps, clip count
    vec4 data[];  // clip records (first frame, frame count, duration), then bone rows
} trinityBones;

vec4 trinityClip(int clip) { return trinityBones.data[clip]; }

mat3x4 trinityBone(uint frameIndex, uint joint) {
    uint clips = uint(trinityBones.header.z);
    uint base = clips + (frameIndex * uint(trinityBones.header.x) + joint) * 3u;
    return mat3x4(trinityBones.data[base], trinityBones.data[base + 1u], trinityBones.data[base + 2u]);
}

/// Seconds into the clip; looping clips wrap, others hold their last frame.
mat3x4 skinMatrix(int clip, float seconds, bool loop, uvec4 joints, vec4 weights) {
    vec4 c = trinityClip(clip);
    float f = seconds * trinityBones.header.y;
    float last = c.y - 1.0;
    float f0 = min(floor(f), last);
    float f1 = loop ? mod(f0 + 1.0, c.y) : min(f0 + 1.0, last);
    float blend = clamp(f - f0, 0.0, 1.0);
    uint a = uint(c.x + f0), b = uint(c.x + f1);

    mat3x4 skin = mat3x4(0.0);
    for (int i = 0; i < 4; i++) {
        float w = weights[i];
        if (w > 0.0)
            skin += w * ((1.0 - blend) * trinityBone(a, joints[i]) + blend * trinityBone(b, joints[i]));
    }
    return skin;
}

/// Clip length in seconds.
float clipDuration(int clip) { return trinityClip(clip).z; }
