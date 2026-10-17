#version 450
#extension GL_GOOGLE_include_directive : require
// For GPUs without descriptor indexing: a fixed-size texture array, uniform indices per draw
#include "mesh_frag.glsl"
