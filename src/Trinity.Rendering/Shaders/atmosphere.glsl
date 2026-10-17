// A physically based atmosphere (single scattering), after Hillaire, "A Scalable and Production Ready
// Sky and Atmosphere Rendering Technique" (2020). Distances in kilometres. Rayleigh and Mie
// scattering, ozone absorption, an earth-sized planet; the ground is at radius BOTTOM.

const float BOTTOM = 6360.0;
const float TOP = 6460.0;
const vec3 RAYLEIGH_SCATTERING = vec3(5.802, 13.558, 33.1) * 1e-3;
const float RAYLEIGH_HEIGHT = 8.0;
const float MIE_SCATTERING = 3.996e-3;
const float MIE_EXTINCTION = 4.40e-3;
const float MIE_HEIGHT = 1.2;
const vec3 OZONE_ABSORPTION = vec3(0.650, 1.881, 0.085) * 1e-3;
const float PI_ATMO = 3.14159265359;

layout(std140, set = 0, binding = 0) uniform Atmosphere {
    vec4 sunDir;     // xyz: towards the sun; w: sun illuminance
    vec4 params;     // mie (haze) multiplier, mie anisotropy g, viewer altitude (km), multiple scattering
    vec4 ground;     // rgb: ground albedo
} atmosphere;

struct Medium { vec3 scatteringR; float scatteringM; vec3 extinction; };

Medium medium(float altitude) {
    float r = exp(-altitude / RAYLEIGH_HEIGHT);
    float m = exp(-altitude / MIE_HEIGHT) * atmosphere.params.x;
    float ozone = max(0.0, 1.0 - abs(altitude - 25.0) / 15.0);
    Medium s;
    s.scatteringR = RAYLEIGH_SCATTERING * r;
    s.scatteringM = MIE_SCATTERING * m;
    s.extinction = s.scatteringR + MIE_EXTINCTION * m + OZONE_ABSORPTION * ozone;
    return s;
}

// Distance along a ray from `o` in direction `d` to a sphere of radius `r` at the origin; -1 if missed
float raySphere(vec3 o, vec3 d, float r) {
    float b = dot(o, d);
    float c = dot(o, o) - r * r;
    float disc = b * b - c;
    if (disc < 0.0) return -1.0;
    float s = sqrt(disc);
    float t0 = -b - s, t1 = -b + s;
    return t0 > 0.0 ? t0 : (t1 > 0.0 ? t1 : -1.0);
}

float rayleighPhase(float c) { return 3.0 / (16.0 * PI_ATMO) * (1.0 + c * c); }

float miePhase(float c, float g) {
    // Cornette-Shanks
    float k = 3.0 / (8.0 * PI_ATMO) * (1.0 - g * g) / (2.0 + g * g);
    return k * (1.0 + c * c) / pow(1.0 + g * g - 2.0 * g * c, 1.5);
}

// Transmittance lookup: u = cos(zenith) remapped to 0..1, v = altitude over the atmosphere's height
vec2 transmittanceUv(float altitude, float cosZenith) {
    return vec2(cosZenith * 0.5 + 0.5, clamp(altitude / (TOP - BOTTOM), 0.0, 1.0));
}

// Sky-view lookup: longitude around the up axis, latitude squeezed towards the horizon where the sky changes fastest
vec2 skyViewUv(vec3 dir) {
    float lon = atan(dir.z, dir.x);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    float v = 0.5 + 0.5 * sign(lat) * sqrt(abs(lat) / (0.5 * PI_ATMO));
    return vec2(lon / (2.0 * PI_ATMO) + 0.5, 1.0 - v);
}

vec3 skyViewDir(vec2 uv) {
    float lon = (uv.x - 0.5) * 2.0 * PI_ATMO;
    float v = (1.0 - uv.y) * 2.0 - 1.0;
    float lat = sign(v) * v * v * 0.5 * PI_ATMO;
    return vec3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
}
