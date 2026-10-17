// Height fog: exponential with distance, thinning exponentially with height, coloured by the sky in
// the direction of view (so it carries the sun's glow and the horizon's colour). Applied in linear HDR.

const float PI_FOG = 3.14159265359;

vec2 fogSkyUv(vec3 dir) {
    float lon = atan(dir.z, dir.x);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    float v = 0.5 + 0.5 * sign(lat) * sqrt(abs(lat) / (0.5 * PI_FOG));
    return vec2(lon / (2.0 * PI_FOG) + 0.5, 1.0 - v);
}

vec3 applyFog(vec3 color, vec3 world) {
    float density = frame.fog.x;
    if (density <= 0.0) return color;
    vec3 ray = world - frame.cameraPos.xyz;
    float dist = length(ray);
    vec3 dir = ray / max(dist, 1e-4);
    // Fog integrated along the ray through density * exp(-falloff * (height - fogHeight))
    float falloff = frame.fog.y;
    float h0 = frame.cameraPos.y - frame.fog.z;
    float dy = ray.y;
    float amount;
    if (falloff > 0.0) {
        float k = falloff * dy;
        float integral = abs(k) > 1e-4 ? (1.0 - exp(-k)) / k : 1.0;
        amount = density * exp(-falloff * h0) * dist * integral;
    } else {
        amount = density * dist;
    }
    float f = 1.0 - exp(-max(amount, 0.0));
    // The sky just above the horizon in this direction
    vec3 fogDir = normalize(vec3(dir.x, max(dir.y, 0.03), dir.z));
    vec3 fogColor = textureLod(skyView, fogSkyUv(fogDir), 0.0).rgb;
    return mix(color, fogColor, f);
}
