// Physically based shading: GGX microfacet specular with Smith height-correlated visibility and
// Schlick Fresnel, Lambert diffuse. Light from the sun (direct, shadowed by cascaded shadow maps) and
// the sky (diffuse from spherical harmonics, reflections from a prefiltered cubemap).

const float PI = 3.14159265359;
const float ENV_MIPS = 5.0; // the prefiltered cubemap's last mip

struct Surface {
    vec3 position;
    vec3 normal;
    vec3 albedo;
    float metallic;
    float roughness;  // perceptual
    float occlusion;
};

float distributionGGX(float nh, float a) {
    float a2 = a * a;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

float visibilitySmithGGX(float nv, float nl, float a) {
    float a2 = a * a;
    float gv = nl * sqrt(nv * nv * (1.0 - a2) + a2);
    float gl = nv * sqrt(nl * nl * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-5);
}

vec3 fresnelSchlick(float vh, vec3 f0) {
    return f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
}

// Pre-integrated specular for environment light (Karis's analytic fit), avoiding a lookup texture
vec3 envBrdf(vec3 f0, float roughness, float nv) {
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    vec2 ab = vec2(-1.04, 1.04) * a004 + r.zw;
    return f0 * ab.x + ab.y;
}

// The sky's light arriving at a surface facing n (irradiance, from spherical harmonics)
vec3 skyIrradiance(vec3 n) {
    vec3 d = n;
    vec3 e = skyLight.sh[0].rgb * 0.282095
        + skyLight.sh[1].rgb * 0.488603 * d.y + skyLight.sh[2].rgb * 0.488603 * d.z + skyLight.sh[3].rgb * 0.488603 * d.x
        + skyLight.sh[4].rgb * 1.092548 * d.x * d.y + skyLight.sh[5].rgb * 1.092548 * d.y * d.z
        + skyLight.sh[6].rgb * 0.315392 * (3.0 * d.z * d.z - 1.0) + skyLight.sh[7].rgb * 1.092548 * d.x * d.z
        + skyLight.sh[8].rgb * 0.546274 * (d.x * d.x - d.y * d.y);
    return max(e, vec3(0.0));
}

vec3 skyRadiance(vec3 dir, float roughness) {
    return textureLod(environmentMap, dir, roughness * ENV_MIPS).rgb;
}

// One cascade's shadow: 9 bilinear compare taps in a 3x3 grid, a smooth 4x4-texel filter
float shadowCascade(int c, vec3 world) {
    vec4 p = frame.shadow[c] * vec4(world, 1.0);
    vec2 uv = p.xy;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        return 1.0;
    float texel = 1.0 / frame.shadowParams.w;
    float sum = 0.0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            sum += texture(shadowMap, vec4(uv + vec2(x, y) * texel, float(c), p.z));
    return sum / 9.0;
}

int cascadeOf(float viewDistance) {
    for (int c = 0; c < 4; c++)
        if (viewDistance < frame.cascadeSplits[c]) return c;
    return 4;
}

// Sunlight reaching a point (0 in shadow, 1 lit), blending into the next cascade near each cascade's end
float sunShadow(vec3 world, vec3 n, float viewDistance) {
    if (frame.shadowParams.x < 0.5) return 1.0;
    int c = cascadeOf(viewDistance);
    if (c >= 4) return 1.0;
    // Push the lookup off the surface by a few texels along its normal, against acne
    vec3 offset = n * frame.shadowTexel[c] * frame.shadowParams.z;
    float s = shadowCascade(c, world + offset);
    float start = c == 0 ? 0.0 : frame.cascadeSplits[c - 1];
    float end = frame.cascadeSplits[c];
    float band = (end - start) * frame.shadowParams.y;
    float t = (viewDistance - (end - band)) / band;
    if (t > 0.0) {
        float next = c < 3 ? shadowCascade(c + 1, world + n * frame.shadowTexel[c + 1] * frame.shadowParams.z) : 1.0;
        s = mix(s, next, clamp(t, 0.0, 1.0));
    }
    return s;
}

vec3 shade(Surface s) {
    vec3 toCamera = frame.cameraPos.xyz - s.position;
    vec3 v = normalize(toCamera);
    vec3 n = s.normal;
    float nv = max(dot(n, v), 1e-4);
    float a = s.roughness * s.roughness;
    vec3 f0 = mix(vec3(0.04), s.albedo, s.metallic);
    vec3 diffuseColor = s.albedo * (1.0 - s.metallic);

    // Sun
    vec3 l = -frame.sunDir.xyz;
    vec3 h = normalize(v + l);
    float nl = max(dot(n, l), 0.0);
    float nh = max(dot(n, h), 0.0);
    float vh = max(dot(v, h), 0.0);
    vec3 f = fresnelSchlick(vh, f0);
    vec3 specular = distributionGGX(nh, a) * visibilitySmithGGX(nv, nl, a) * f;
    vec3 diffuse = (1.0 - f) * diffuseColor / PI;
    float viewDistance = -(frame.view * vec4(s.position, 1.0)).z;
    float shadow = nl > 0.0 ? sunShadow(s.position, n, viewDistance) : 0.0;
    vec3 direct = (diffuse + specular) * frame.sunColor.rgb * nl * shadow;

    // Sky
    vec3 r = reflect(-v, n);
    vec3 ambientDiffuse = diffuseColor * skyIrradiance(n) / PI;
    vec3 ambientSpecular = envBrdf(f0, s.roughness, nv) * skyRadiance(r, s.roughness);
    vec3 indirect = (ambientDiffuse + ambientSpecular) * s.occlusion;

    return direct + indirect;
}

// Debug: which cascade a point uses
vec3 cascadeColor(vec3 world) {
    float viewDistance = -(frame.view * vec4(world, 1.0)).z;
    const vec3 COLORS[5] = vec3[](vec3(1, 0.3, 0.3), vec3(0.3, 1, 0.3), vec3(0.3, 0.5, 1), vec3(1, 1, 0.3), vec3(0.5));
    return COLORS[cascadeOf(viewDistance)];
}
