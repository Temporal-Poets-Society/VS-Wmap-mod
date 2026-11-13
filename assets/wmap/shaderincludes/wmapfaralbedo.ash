// Wmap far terrain's ground colours, shared by the smooth mesh's fragments (wmapfarshade.ash) and the terraces' cell map
// (farcellmap.fsh, which colours each cell once): the atlas's Terrain style, or the game's own climate tints where forest
// and climate are known (Earth Map). Include after wmapfartiles.ash.

uniform vec4 wmapWater;                   // rgb: sea colour from the Sea mod; a: 1 when supplied
uniform float wmapHeightScale;            // blocks from the sea surface to the top of the world
uniform sampler2D wmapPlantTint;          // the game's climate colour maps (264 x 264, 4 texels of padding)
uniform sampler2D wmapWaterTint;
uniform vec4 wmapTint;                    // 1 when the colour maps are loaded; unused

// The far water's contract with a water mod (0.5.0-pre.2; docs/mid-distance-view.md, "Far water and Sea"). Wmap declares
// these and does the shading; a water mod sets them whenever the game uses a program that has them (Sea's Use hook does,
// for every program declaring seaNightFloor, from Sea 0.4.3), and they stay zero without one. Sea 0.4.x also text-patches
// a program named farterrain, but leaves one alone that already declares seaNightFloor.
uniform vec3 seaNightFloor = vec3(0.0);   // the least light on far terrain at night
uniform float seaSkyReflect = 0.0;        // how strongly the far sea reflects the sky (Fresnel); 0 without a water mod
uniform int seaWaveCount = 0;             // the near water's wave trains (Sea 0.4.7): kx, kz, phase at the camera, amplitude
uniform vec4 seaWave[24];
uniform float seaWaveFactor = 0.2;        // the near water's waves over the shader's divisor at the camera (Sea 0.4.7-pre.5): 1 on the open sea, 0.2 on still water
uniform vec4 seaSunset = vec4(0.0);       // AstraTerra's sunset glow, as Sea passes it (Sea 0.4.7-pre.5): the sun's colour x strength; strength
uniform float seaGlint = 0.0;             // the near water's glint strength (Sea 0.4.7-pre.7); 0: no glitter on the far sea
uniform vec4 seaSun = vec4(0.0, -1.0, 0.0, 0.0);   // the sun's direction; its glint brightness (Sea's own, the near water's)
uniform vec4 seaMoon = vec4(0.0, -1.0, 0.0, 0.0);  // the moon's direction; its glint brightness
uniform vec3 seaSunColor = vec3(1.0);     // the drawn sun's colour (Sea: AstraTerra's when it draws the sun)
uniform vec4 seaWeather = vec4(1.0, 0.0, 0.0, 0.0); // clear sky, night, cloud, roughness (Sea's)

// The atlas's Terrain style (src/Client/Shaders/map.frag).
vec3 hypsometric(float hn)
{
    vec3 c = vec3(0.42, 0.58, 0.34);
    c = mix(c, vec3(0.58, 0.68, 0.40), smoothstep(0.0, 0.12, hn));
    c = mix(c, vec3(0.80, 0.76, 0.50), smoothstep(0.12, 0.32, hn));
    c = mix(c, vec3(0.70, 0.56, 0.38), smoothstep(0.32, 0.50, hn));
    c = mix(c, vec3(0.60, 0.52, 0.46), smoothstep(0.50, 0.66, hn));
    c = mix(c, vec3(0.86, 0.85, 0.83), smoothstep(0.66, 0.82, hn));
    return c;
}

// The game's climate tint for a block this many blocks above sea level: vanilla's altitude adjustments of the
// stored climate (Climate.GetAdjustedTemperature, Climate.GetRainFall) and its colour-map lookup (colormap.vsh).
vec3 climateTint(sampler2D map, vec4 biome, float aboveSea)
{
    float temperature = clamp(biome.g * 255.0 - aboveSea / 1.5, 0.0, 255.0);
    float rain = clamp(biome.b * 255.0 + aboveSea / 2.0 + 5.0 * clamp(8.0 - aboveSea, 0.0, 8.0), 0.0, 255.0);
    vec2 rel = clamp(vec2(temperature, rain) / 255.0, 0.001, 0.999);
    return texture(map, (4.0 + rel * 256.0) / 264.0).rgb;
}

// The climate's water tint at a point (the game's watertint map at the sea surface), a temperate sea's where the climate is
// not known (from the map's middle).
vec3 wmapWaterTintAt(vec4 biome)
{
    vec3 temperate = vec3(0.137, 0.463, 0.416);
    if (wmapTint.x < 0.5 || biome.a <= 0.0) return temperate;
    return mix(temperate, climateTint(wmapWaterTint, biome, -1.0), biome.a);
}

// Water under this tint, deep 0 (shallow) to 1 (55 blocks or more). With a water mod's sky reflection (Sea): the dark body
// the game's near sea shows looking down, a quarter of the tint over deep blue (fitted to Sea's near water in game,
// 23 October 2026: its colour across distance is this plus the sky by Fresnel), the reflection added in wmapfarshade.ash.
// Without: as 0.4.0 calibrated it against vanilla water, the grey texture (about 0.5) tinted and lifted toward what its
// translucent surface shows at a low angle. Sea's FarWaterColor wins.
vec3 wmapWaterAlbedo(float deep, vec3 tint)
{
    if (wmapWater.a > 0.5) return wmapWater.rgb;
    if (seaSkyReflect > 0.0) return mix(tint * 0.35 + vec3(0.10, 0.12, 0.16), tint * 0.25 + vec3(0.13, 0.15, 0.20), deep);
    if (wmapTint.x < 0.5) return mix(vec3(0.40, 0.62, 0.70), vec3(0.11, 0.25, 0.40), deep);
    vec3 water = tint * 0.55 + vec3(0.09);
    return mix(water, water * 0.85, deep);
}

// The near water's waves as a surface normal at p (blocks from the camera), each wave train fading out where it gets
// shorter than about four pixels (footprint: blocks per pixel), so the far sea does not shimmer. Sea's own slope formula
// (CelestialShader.Vertex), divided as the near water is: by 5 x max(0.2, 1 - oceanity), which Sea passes as seaWaveFactor
// for the camera's place (0.5.0-pre.6). Up to pre.5 this took still water's 5, a fifth of the open sea's slope, and the
// far sea lay flat beyond the near water's waves. Sea fades its near trains by the same rule.
vec3 wmapWaveNormal(vec2 p, float footprint, out float rough)
{
    vec2 slope = vec2(0.0);
    rough = 0.0;
    for (int i = 0; i < seaWaveCount; i++)
    {
        vec4 w = seaWave[i];
        float k = length(w.xy);
        float keep = 1.0 - smoothstep(0.25, 0.75, k * footprint);
        // The slope a faded train would have given is kept as roughness for the glint, as Sea's near water keeps it.
        float steep = w.w * k * seaWaveFactor;
        rough += (1.0 - keep * keep) * 0.5 * steep * steep;
        if (keep <= 0.0) continue;
        slope += keep * w.w * w.xy * cos(dot(w.xy, p) + w.z) * seaWaveFactor;
    }
    return normalize(vec3(-slope.x, 1.0, -slope.y));
}

// AstraTerra's sunset glow on a sky colour in direction v (its SunsetScatter, the same numbers as the dome's and Sea's near
// water's): a halo round a low sun and a band along the horizon beneath it, muted by the sky's own fog. Without AstraTerra
// (or Sea to pass it) seaSunset is zero and the colour is unchanged.
vec3 wmapSunsetGlow(vec3 sky, vec3 v, vec3 sun, float fog)
{
    if (seaSunset.w <= 0.001) return sky;
    float c = max(dot(v, sun), 0.0);
    float halo = 0.8 * pow(c, 24.0) + 0.3 * pow(c, 4.0);
    vec2 vh = v.xz / max(length(v.xz), 1e-4);
    vec2 sh = sun.xz / max(length(sun.xz), 1e-4);
    float toward = 0.5 + 0.5 * dot(vh, sh);
    float band = exp(-max(v.y, 0.0) * 10.0) * (0.15 + 0.85 * toward * toward);
    float k = clamp(seaSunset.w * (halo + 0.55 * band) * (1.0 - 0.5 * fog), 0.0, 0.7);
    return mix(sky, seaSunset.rgb / seaSunset.w * (0.55 + 0.45 * pow(c, 8.0)), k);
}

// The sun's glitter path and the moon's trail on the far sea (0.5.0-pre.8), Sea's own lobe (CelestialShader seaGlints,
// the same numbers): full while the body's centre is up, gone once its top has set; wider where the waves are too short
// for the pixel (rough: their slope variance), the same light spread over it. Up to pre.7 the far sea had none, and the
// path stopped in a calm grey band short of the horizon, where a real one is brightest. glint: grazing fade and Fresnel.
float wmapLobe(vec3 ray, vec3 body, float width)
{
    return exp(-max(0.0, 1.0 - dot(ray, body)) / width) * smoothstep(-0.04, 0.0, body.y) * seaWeather.x;
}
vec3 wmapSeaGlints(vec3 c, vec3 ray, float glint, float rough)
{
    float width = 0.0012 + seaWeather.w * 0.018;
    float spread = width + min(2.0 * rough, 0.02);
    glint *= width / spread;
    float sun = clamp(seaGlint * glint * wmapLobe(ray, seaSun.xyz, spread) * seaSun.w, 0.0, 1.0);
    float moon = clamp(seaGlint * glint * wmapLobe(ray, seaMoon.xyz, spread * 1.3) * seaMoon.w, 0.0, 1.0);
    c = mix(c, seaSunColor, sun);
    return mix(c, vec3(0.62, 0.72, 0.90), moon);
}

// Land this many blocks above the sea under this biome (R forest).
vec3 wmapLandAlbedo(float height, vec4 biome)
{
    float hn = height / wmapHeightScale;
    vec3 albedo = hypsometric(hn) * 0.85;
    if (wmapTint.x > 0.5 && biome.a > 0.0)
    {
        // Grass and leaves: grey textures (about 0.6 and 0.55) tinted by climate; canopies are darker in their own shade.
        vec3 plant = climateTint(wmapPlantTint, biome, height - 1.0);
        vec3 green = mix(plant * 0.60, plant * 0.40, biome.r);
        // Rock and snow above the green, as the atlas draws them.
        green = mix(green, albedo, smoothstep(0.45, 0.70, hn));
        albedo = mix(albedo, green, biome.a);
    }
    return albedo;
}
