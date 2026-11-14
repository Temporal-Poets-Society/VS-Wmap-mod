// Wmap far terrain's ground colours, shared by the smooth mesh's fragments (wmapfarshade.ash) and the terraces' cell map
// (farcellmap.fsh, which colours each cell once): the atlas's Terrain style, or the game's own climate tints where forest
// and climate are known (Earth Map). Include after wmapfartiles.ash.

uniform vec4 wmapWater;                   // rgb: sea colour from the Sea mod; a: 1 when supplied
uniform float wmapHeightScale;            // blocks from the sea surface to the top of the world
uniform sampler2D wmapPlantTint;          // the game's climate colour maps (264 x 264, 4 texels of padding)
uniform sampler2D wmapWaterTint;
uniform vec4 wmapTint;                    // 1 when the colour maps are loaded; unused
uniform sampler2D wmapGrassSeasons;       // the season maps' rows over the year (Core/GrassSeasons.cs): 0-6 Earth's grass maps, 7 the game's, 8 oak, 9 needles

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

// The climate a block this many blocks above sea level reads (temperature and rain bytes): vanilla's altitude adjustments
// of the stored climate (Climate.GetAdjustedTemperature, Climate.GetRainFall). The bytes are Earth's
// ClimatePixelForVanillaLapse, stored so that vanilla's adjustments give the real surface temperature and rain.
vec2 wmapClimateAt(vec4 biome, float aboveSea)
{
    float temperature = clamp(biome.g * 255.0 - aboveSea / 1.5, 0.0, 255.0);
    float rain = clamp(biome.b * 255.0 + aboveSea / 2.0 + 5.0 * clamp(8.0 - aboveSea, 0.0, 8.0), 0.0, 255.0);
    return vec2(temperature, rain);
}

// How much this world's own climate cools a block this many blocks above sea (climate-byte units; 0.5.3-rc.14, WM-0022): the
// client's Climate.GetAdjustedTemperature (Core/GrassSeasons.AltitudeRow, row 10 of the season strip), which Earth patches
// with the real lapse on its vertical scale; vanilla's 1 byte per 1.5 blocks without the strip or Earth.
float wmapCooling(float aboveSea)
{
    if (wmapSeason.z < 0.5) return aboveSea / 1.5;
    float c = clamp((aboveSea + 64.0) / max(wmapHeightScale + 64.0, 1.0) * 127.0, 0.0, 127.0);
    int c0 = int(floor(c)), c1 = min(c0 + 1, 127);
    vec4 a = texelFetch(wmapGrassSeasons, ivec2(c0, 10), 0) * 255.0, b = texelFetch(wmapGrassSeasons, ivec2(c1, 10), 0) * 255.0;
    float at = (floor(a.r + 0.5) * 256.0 + floor(a.g + 0.5)) / 128.0 - 128.0, bt = (floor(b.r + 0.5) * 256.0 + floor(b.g + 0.5)) / 128.0 - 128.0;
    return mix(at, bt, c - float(c0));
}

// The temperature byte the game's season weight reads at a block (colormap.vsh's x): the block's temperature with this
// world's cooling taken back, the sea-level temperature where Earth's real lapse applies (Earth's rewrite of colormap.vsh).
// The stored byte (vanilla's cooling taken back) is far warmer on high ground in tall worlds: at 1:10 the Engadin's floor
// (181 blocks up) read 209 against the game's 137, a weight of 0 against 0.89, and its July meadows took the cold climate
// tint alone, tundra-yellow beside the green real chunks (0.5.3-rc.13 and before).
float wmapWeightTemperature(vec4 biome, float aboveSea) { return wmapClimateAt(biome, aboveSea).x + wmapCooling(aboveSea); }

// A climate colour map at those bytes (colormap.vsh's lookup).
vec3 wmapClimateLookup(sampler2D map, vec2 climate)
{
    vec2 rel = clamp(climate / 255.0, 0.001, 0.999);
    return texture(map, (4.0 + rel * 256.0) / 264.0).rgb;
}

// The game's climate tint for a block this many blocks above sea level.
vec3 climateTint(sampler2D map, vec4 biome, float aboveSea) { return wmapClimateLookup(map, wmapClimateAt(biome, aboveSea)); }

// How much the season map tints a plant against the climate map (colormap.vsh, maps without the tropical flag): x is the
// temperature byte at the block plus its altitude cooling taken back, which is the stored byte. About 0.9 in temperate
// lands, less toward the poles and the tropics, near none on hot or high ground.
float wmapSeasonWeight(float x)
{
    return clamp(0.5 - cos(x / 42.0) / 2.3 + max(0.0, 128.0 - x) / 512.0 - max(0.0, x - 130.0) / 200.0, 0.0, 1.0);
}

// A season map's row at a time of year (Core/GrassSeasons.cs).
vec3 wmapSeasonRow(float row, float rel) { return texture(wmapGrassSeasons, vec2(clamp(rel, 0.0, 1.0), (row + 0.5) / 11.0)).rgb; }

// The grass season map a place follows (wmapSeasonAt): Earth's maps by their growing season (1, 0.80, 0.66, 0.52, 0.38 of
// the year), its summer-dry and steppe maps, or the game's own map where Earth gives none.
vec3 wmapGrassSeason(vec4 season, float rel)
{
    float share = season.r;
    float p = clamp(share >= 0.8 ? (1.0 - share) / 0.2 : 1.0 + (0.8 - share) / 0.14, 0.0, 4.0);
    float p0 = min(floor(p), 3.0);
    vec3 temperate = mix(wmapSeasonRow(p0, rel), wmapSeasonRow(p0 + 1.0, rel), p - p0);
    vec3 dry = mix(wmapSeasonRow(6.0, rel), wmapSeasonRow(5.0, rel), smoothstep(0.66, 1.0, share));
    return mix(wmapSeasonRow(7.0, rel), mix(temperate, dry, season.g), smoothstep(0.2, 0.38, share));
}

// Earth's grass rainfall at a time of year (its GrassSeason.SeasonalRain): the yearly rainfall's aridity index (its
// SoilFertility curve) scaled by exp(wet), the place's wetness then against its yearly mean.
const float wmapAridRain[10] = float[10](0.0, 15.0, 50.0, 95.0, 115.0, 130.0, 150.0, 170.0, 200.0, 235.0);
const float wmapAridLog[10] = float[10](-4.60517, -2.995732, -1.609438, -0.693147, -0.287682, 0.0, 0.405465, 0.693147, 1.098612, 1.609438);
float wmapSeasonalRain(float rain, float wet)
{
    if (rain <= 0.0 || abs(wet) < 1e-3) return rain;
    float la = wmapAridLog[9];
    for (int i = 1; i < 10; i++)
        if (rain <= wmapAridRain[i]) { la = mix(wmapAridLog[i - 1], wmapAridLog[i], (rain - wmapAridRain[i - 1]) / (wmapAridRain[i] - wmapAridRain[i - 1])); break; }
    la += clamp(wet, -2.0, 2.0);
    if (la <= wmapAridLog[0]) return 0.0;
    for (int i = 1; i < 10; i++)
        if (la <= wmapAridLog[i]) return mix(wmapAridRain[i - 1], wmapAridRain[i], (la - wmapAridLog[i - 1]) / (wmapAridLog[i] - wmapAridLog[i - 1]));
    return wmapAridRain[9];
}

// The place's wetness against its yearly mean now (the season's B and A: its yearly wave over the calendar's year).
float wmapWetness(vec4 season)
{
    float a = (season.b * 255.0 - 128.0) / 63.5, b = (season.a * 255.0 - 128.0) / 63.5, t = 6.2831853 * wmapSeason.y;
    return a * cos(t) + b * sin(t);
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

// The stored climate's mean temperature in C this many blocks above the sea: the byte less vanilla's 1 C per 6.375 blocks
// (Climate.GetAdjustedTemperature; Earth stores it so that this is its own temperature at the sample's height).
float wmapTemperature(vec4 biome, float aboveSea) { return (biome.g * 255.0 - aboveSea / 1.5) / 4.25 - 20.0; }

// Land this many blocks above the sea under this biome (R forest) and grass season (wmapSeasonAt).
vec3 wmapLandAlbedo(float height, vec4 biome, vec4 season)
{
    float hn = height / wmapHeightScale;
    vec3 albedo = hypsometric(hn) * 0.85;
    if (wmapTint.x > 0.5 && biome.a > 0.0)
    {
        vec2 climate = wmapClimateAt(biome, height - 1.0);
        vec3 green;
        if (wmapSeason.z > 0.5)
        {
            // As the game colours them (0.5.3-rc.5; WM-0022): the grass texture (grasscoverage/normal-top, about 0.60 0.60
            // 0.50) times the climate map at the grass's rainfall now, mixed with its season map by the season weight; leaves
            // (about 0.55 grey) the same with the oak's and the needles' season maps and the yearly rainfall. Until rc.4 the
            // climate map alone tinted both: the season map, which carries about nine tenths of the colour in temperate lands,
            // was missing, and cool or dry lands came out olive to yellow where the game draws them green.
            float weight = wmapSeasonWeight(wmapWeightTemperature(biome, height - 1.0));
            vec3 grassClimate = wmapClimateLookup(wmapPlantTint, vec2(climate.x, wmapSeasonalRain(climate.y, wmapWetness(season))));
            vec3 grass = vec3(0.596, 0.603, 0.497) * mix(grassClimate, wmapGrassSeason(season, wmapSeason.x), weight);
            // Needles where it is cold, broad leaves where it is warm, until Earth gives the forest's make-up (EA-0071).
            float conifer = mix(0.85, 0.25, smoothstep(-2.0, 14.0, wmapTemperature(biome, height)));
            vec3 leafSeason = mix(wmapSeasonRow(8.0, wmapSeason.x), wmapSeasonRow(9.0, wmapSeason.x), conifer);
            // (0.5.3-rc.6: 0.66; rc.5's 0.55 drew far and recorded forest about a fifth darker than the real forest beside it.)
            vec3 leaves = 0.66 * mix(wmapClimateLookup(wmapPlantTint, climate), leafSeason, weight);
            green = mix(grass, leaves, biome.r);
        }
        else
        {
            // Without the season maps: grey textures (about 0.6 and 0.55) tinted by climate; canopies darker in their shade.
            vec3 plant = wmapClimateLookup(wmapPlantTint, climate);
            green = mix(plant * 0.60, plant * 0.40, biome.r);
        }
        // Bare ground and snow by the climate on high ground (0.5.3-rc.4). Until then the atlas's height palette took over
        // above about 1,200 m whatever the climate: the Ethiopian highlands tan, alpine meadows grey, Tibet half white all
        // year. Now high ground turns to scree and rock where it is cold (a mean year below about 2 C, all of it below
        // -4 C: Tibet's plateau, the Alps above about 3,000 m) and white only where it is very cold (below about -10 C:
        // the Himalaya's summits). Both need height as well, so cold lowlands (Siberia's taiga and tundra) stay green; the
        // game's own rule goes by the warmest month (Earth's MountainEcology), which the climate tiles do not carry yet.
        // Glaciers come from the chart's ice, as before.
        float t = wmapTemperature(biome, height);
        // (0.5.3-rc.5: bare from a mean below -5 C, all of it below -10 C; rc.4's -4 to 2 C turned Tibet's grass, which the
        // game draws yellow in summer, to orange-brown scree.)
        float bare = (1.0 - smoothstep(-10.0, -5.0, t)) * smoothstep(0.25, 0.45, hn);
        float snow = (1.0 - smoothstep(-16.0, -10.0, t)) * smoothstep(0.45, 0.60, hn);
        green = mix(green, vec3(0.47, 0.44, 0.40), 0.85 * bare);
        green = mix(green, vec3(0.94, 0.96, 0.98), snow);
        albedo = mix(albedo, green, biome.a);
    }
    return albedo;
}
