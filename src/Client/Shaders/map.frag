// Wmap map shader. Terrain arrives as semantic texels (see Core/Semantic.cs); every style is
// evaluated here per screen pixel, so lines stay sharp at any zoom and styles switch instantly.
// Shared by the game (GLSL 330 core) and the browser design prototype (GLSL ES 3.00).

in vec2 vScreen;
out vec4 outColor;

uniform vec4 uViewport;   // x, y, width, height in screen pixels
uniform vec4 uCamera;     // pixels per block, GUI scale, unused, unused
uniform vec2 uCamUV;      // camera position in world-raster coordinates (x wrapped to 0..1)
uniform vec2 uAnchor;     // camera minus pattern anchor, in blocks (keeps pattern coordinates small)
uniform vec4 uWorld;      // width, height (blocks), wraps, development (1) / gameplay (0)
uniform float uHeightScale; // blocks from the sea surface to the top of the world
uniform float uHeightRange; // blocks the G channel spans (Core/Semantic.cs HeightRangeFor)
uniform int uStyle;       // 0 chart, 1 terrain
uniform int uMode;        // 0 world raster, 1 detail tile, 2 player marker, 3 compass rose, 4 wind arrows

uniform sampler2D uBase;
uniform vec4 uBaseInfo;   // present, blocks per texel, texels wide, texels high
uniform sampler2D uMask;
uniform vec4 uMaskInfo;   // enabled, unused, unused, unused
uniform sampler2D uGeo;
uniform vec4 uGeoInfo;    // present, meridian step (deg), parallel step (deg), unused
uniform sampler2D uTile;
uniform vec4 uTileInfo;   // camera-relative origin x, z (blocks), blocks per texel, alpha
uniform vec4 uTileSlot;   // slot x, slot y (texels), page size, recorded (1) / predicted (0)
uniform sampler2D uRef;   // the coarser recording drawn under a prediction tile (WM-D41)
uniform vec4 uRefInfo;    // its camera-relative origin x, z (blocks), blocks per texel, present
uniform vec4 uRefSlot;    // its slot x, slot y (texels), page size, unused
uniform vec4 uMarker;     // centre x, centre y (screen), heading (radians, 0 = north), radius (px)
uniform vec4 uMarkerColor; // negative red selects the style's own-player color
uniform sampler2D uWind;  // wind toward (east, south) in vanilla units over the whole world
uniform vec4 uWindInfo;   // enabled, arrow spacing (px), belt boundaries (1) for prevailing winds, local field (1)
uniform vec4 uWindRect;   // local field: its square's origin relative to the camera (blocks, x on the camera's lap), width, height
uniform sampler2D uDay;   // the sun's elevation over the whole world (Core/DayGrid.cs: degrees = (byte - 128) / 4)
uniform vec4 uDayInfo;    // enabled, unused, unused, unused
uniform sampler2D uClimate; // forest, stored temperature, stored rain, known (Core/ClimateRaster.cs)
uniform vec4 uClimateInfo;  // present, unused, unused, unused

const float CoastRange = 128.0;
const float RiverRange = 12.0;
const float Far = 1.0e6;

// ---------------------------------------------------------------- noise

float hash(vec2 p)
{
    p = mod(p, 4096.0);
    vec3 q = fract(vec3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return fract((q.x + q.y) * q.z);
}

float noise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p)
{
    float v = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { v += a * noise(p); p = p * 2.03 + vec2(17.1, 9.7); a *= 0.5; }
    return v;
}

// ---------------------------------------------------------------- terrain

struct Terrain
{
    float coast;   // blocks to the coast, land positive
    float height;  // blocks relative to the sea surface
    float ice;     // 0..1
    float river;   // blocks to the nearest river bank, inside negative
    float known;   // 0..1
    vec2 slope;    // height gradient (blocks per block)
};

float unsignedCurve(float v) { float e = v * (255.0 / 127.5) - 1.0; return sign(e) * e * e; }

// Level of detail that keeps one texel per screen pixel, so gradients match the sampled mip.
float lodFor(float blocksPerTexel) { return max(0.0, log2(max(1e-6, 1.0 / (uCamera.x * blocksPerTexel)))); }

Terrain decode(vec4 t, float blocksPerTexel, bool riverChannel)
{
    Terrain r;
    r.coast = unsignedCurve(t.r) * CoastRange * blocksPerTexel;
    r.height = unsignedCurve(t.g) * uHeightRange;
    r.ice = t.b;
    r.river = riverChannel ? unsignedCurve(t.a) * RiverRange * blocksPerTexel : Far;
    r.known = riverChannel ? 1.0 : t.a;
    r.slope = vec2(0.0);
    return r;
}

// Regional slope: central differences four texels apart at a coarser level, so relief shading
// follows hills and mountains rather than the terrain's small-scale noise.
vec2 baseSlope(vec2 uv, float lod)
{
    float l = lod + 2.0;
    vec2 step = exp2(l) / uBaseInfo.zw;
    float hx = unsignedCurve(textureLod(uBase, uv + vec2(step.x, 0.0), l).g) - unsignedCurve(textureLod(uBase, uv - vec2(step.x, 0.0), l).g);
    float hz = unsignedCurve(textureLod(uBase, uv + vec2(0.0, step.y), l).g) - unsignedCurve(textureLod(uBase, uv - vec2(0.0, step.y), l).g);
    return vec2(hx, hz) * uHeightRange / (2.0 * exp2(l) * uBaseInfo.y);
}

Terrain sampleBase(vec2 offset)
{
    float texel = uBaseInfo.y;
    vec2 size = uBaseInfo.zw;
    vec2 uv = uCamUV + offset / uWorld.xy;
    float lod = lodFor(texel);
    Terrain r = decode(textureLod(uBase, uv, lod), texel, true);
    r.slope = baseSlope(uv, lod);
    return r;
}

Terrain sampleTile(vec2 offset)
{
    float texel = uTileInfo.z, page = uTileSlot.z;
    vec2 p = (offset - uTileInfo.xy) / texel + 1.0;         // padded texel coordinate (centres on .5)
    vec2 lo = uTileSlot.xy + 0.5, hi = uTileSlot.xy + 129.5;
    vec2 at = clamp(uTileSlot.xy + p, lo, hi);
    Terrain r = decode(texture(uTile, at / page), texel, false);
    float hx = unsignedCurve(texture(uTile, clamp(at + vec2(1.0, 0.0), lo, hi) / page).g) - unsignedCurve(texture(uTile, clamp(at - vec2(1.0, 0.0), lo, hi) / page).g);
    float hz = unsignedCurve(texture(uTile, clamp(at + vec2(0.0, 1.0), lo, hi) / page).g) - unsignedCurve(texture(uTile, clamp(at - vec2(0.0, 1.0), lo, hi) / page).g);
    r.slope = vec2(hx, hz) * uHeightRange / (2.0 * texel);
    return r;
}

// A prediction finer than the recording drawn under it refines that recording only where the recording confirms it (WM-D41):
// zooming in shows the prediction's detail between four-block samples that agree with it, while a recorded sample the
// prediction contradicts keeps its cell, and the cells around it, to the recording (WM-D40: an old dry chunk under a newer
// predicted river).

// The prediction at a point (blocks from the camera), within the tile's padded texels. No mipmaps: explicit level 0
// keeps this valid in the non-uniform loop below.
vec4 predictedAt(vec2 offset)
{
    vec2 at = clamp(uTileSlot.xy + (offset - uTileInfo.xy) / uTileInfo.z + 1.0, uTileSlot.xy + 0.5, uTileSlot.xy + 129.5);
    return textureLod(uTile, at / uTileSlot.z, 0.0);
}

// One recorded sample against the prediction where it was taken, as the recorder compares them (RecordingRules.Differs):
// land against water, ice against none, or heights more than 3 blocks plus 3 % apart (plus both texels' rounding).
bool confirms(vec4 recorded, vec4 predicted)
{
    if (recorded.a < 0.5) return true;   // not recorded
    if ((recorded.r < 0.5) != (predicted.r < 0.5) || (recorded.b > 0.5) != (predicted.b > 0.5)) return false;
    float hr = unsignedCurve(recorded.g) * uHeightRange, hp = unsignedCurve(predicted.g) * uHeightRange;
    float rounding = (abs(recorded.g * (255.0 / 127.5) - 1.0) + abs(predicted.g * (255.0 / 127.5) - 1.0)) * uHeightRange / 127.5;
    return abs(hr - hp) <= 3.0 + 0.03 * abs(hp) + rounding;
}

// Whether the prediction may draw here: its recorded cell and the recorded cells around it (a sample that matches may
// still lie beside part of a contrary feature, as the recorder's shore rule assumes) all confirm it. Cells beyond this
// prediction tile are not compared.
bool refines(vec2 offset)
{
    float step = uRefInfo.z;
    vec2 cell = floor((offset - uRefInfo.xy) / step);
    vec2 lo = uTileInfo.xy, hi = uTileInfo.xy + 128.0 * uTileInfo.z;
    for (int dz = -1; dz <= 1; dz++)
    for (int dx = -1; dx <= 1; dx++)
    {
        vec2 c = cell + vec2(float(dx), float(dz));
        if (c.x < -1.0 || c.y < -1.0 || c.x > 128.0 || c.y > 128.0) continue;
        // The recorder samples the block at a cell's centre (ObservedTerrain) and compares the prediction at that block's centre.
        vec2 at = uRefInfo.xy + (c + 0.5) * step + 0.5;
        if (any(lessThan(at, lo)) || any(greaterThanEqual(at, hi))) continue;
        if (!confirms(texelFetch(uRef, ivec2(uRefSlot.xy + c + 1.0), 0), predictedAt(at))) return false;
    }
    return true;
}

// ---------------------------------------------------------------- drawing helpers

float line(float distancePx, float halfWidth) { return 1.0 - smoothstep(halfWidth - 0.6, halfWidth + 0.6, abs(distancePx)); }

// World-anchored pattern coordinates in pixels at two zoom octaves; t blends between them.
void octaves(vec2 offset, out vec2 q0, out vec2 q1, out float t)
{
    float l = log2(uCamera.x);
    float o = floor(l);
    t = l - o;
    vec2 local = offset + uAnchor;
    q0 = local * exp2(o);
    q1 = local * exp2(o + 1.0);
}

float hatch(vec2 q, vec2 direction, float spacing, float halfWidth)
{
    float d = dot(q, direction) / spacing;
    return line((fract(d) - 0.5) * spacing, halfWidth);
}

float hatchOctaves(vec2 q0, vec2 q1, float t, vec2 direction, float spacing, float halfWidth)
{
    // The finer octave fades in as the zoom approaches it; spacing stays constant on screen.
    return mix(hatch(q0, direction, spacing / 1.0, halfWidth), hatch(q1, direction, spacing * 2.0, halfWidth), t);
}

float stipple(vec2 q, float density, float size)
{
    vec2 cell = floor(q / 4.0);
    vec2 jitter = vec2(hash(cell), hash(cell + 71.3)) * 2.0 + 1.0;
    float present = step(hash(cell + 13.7), density);
    return present * (1.0 - smoothstep(size - 0.5, size + 0.5, length(q - cell * 4.0 - jitter)));
}

float explored(vec2 offset)
{
    if (uMaskInfo.x < 0.5) return 1.0;
    vec2 uv = uCamUV + offset / uWorld.xy;
    return texture(uMask, uv).r;
}

vec3 geoTexel(ivec2 i, ivec2 size)
{
    i.x = uWorld.z > 0.5 ? (i.x % size.x + size.x) % size.x : clamp(i.x, 0, size.x - 1);
    return texelFetch(uGeo, ivec2(i.x, clamp(i.y, 0, size.y - 1)), 0).rgb;
}

// Longitude (continuous), latitude, outside geography. Filtered by hand: where the ring's ends meet (the texture's wrap)
// the longitude jumps by 360 degrees, and the hardware's filter drew the graticule there as a band one texel wide
// (worlds without Earth's gutter, 0.5.0-pre.8). Each texel's longitude is unwrapped against the first one's.
vec3 geo(vec2 offset)
{
    vec2 uv = uCamUV + offset / uWorld.xy;
    ivec2 size = textureSize(uGeo, 0);
    vec2 p = uv * vec2(size) - 0.5;
    ivec2 i = ivec2(floor(p));
    vec2 f = p - vec2(i);
    vec3 a = geoTexel(i, size), b = geoTexel(i + ivec2(1, 0), size);
    vec3 c = geoTexel(i + ivec2(0, 1), size), d = geoTexel(i + ivec2(1, 1), size);
    b.x -= 360.0 * floor((b.x - a.x) / 360.0 + 0.5);
    c.x -= 360.0 * floor((c.x - a.x) / 360.0 + 0.5);
    d.x -= 360.0 * floor((d.x - a.x) / 360.0 + 0.5);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// A change of longitude across neighbouring pixels, taking the 360-degree wrap between them as no change.
float lonStep(float d)
{
    d = abs(d);
    return min(d, abs(d - 360.0));
}

float graticule(vec2 offset, out float major)
{
    major = 0.0;
    if (uGeoInfo.x < 0.5) return 0.0;
    vec3 g = geo(offset);
    if (g.z > 0.5) return 0.0;
    float lonW = lonStep(dFdx(g.x)) + lonStep(dFdy(g.x)), latW = fwidth(g.y);
    if (lonW > 20.0 || lonW <= 0.0) return 0.0;   // the longitude seam inside the ring gutter
    float lon = abs(fract(g.x / uGeoInfo.y + 0.5) - 0.5) * uGeoInfo.y / lonW;
    float lat = abs(fract(g.y / uGeoInfo.z + 0.5) - 0.5) * uGeoInfo.z / latW;
    float equator = abs(g.y) / latW;
    float tropic = min(abs(abs(g.y) - 23.44), abs(abs(g.y) - 66.56)) / latW;
    major = max(line(equator, 0.9), line(tropic, 0.55) * step(0.5, fract(dot(vScreen, vec2(1.0)) / 9.0)));
    return max(line(lon, 0.5), line(lat, 0.5));
}

// ---------------------------------------------------------------- chart style

const vec3 Ink = vec3(0.23, 0.16, 0.09);
const vec3 SeaInk = vec3(0.24, 0.30, 0.32);
const vec3 Wash = vec3(0.60, 0.72, 0.71);

vec3 paper(vec2 screen)
{
    vec2 s = screen - uViewport.xy;
    vec3 c = vec3(0.925, 0.872, 0.752);
    float mottle = fbm(s / 260.0);
    float grain = noise(s / 1.3) * 0.5 + noise(s / 3.1) * 0.5;
    c *= 0.94 + 0.10 * mottle + 0.035 * (grain - 0.5);
    // Aged, darker edges with an uneven burn.
    vec2 edge = min(s, uViewport.zw - s);
    float border = min(edge.x, edge.y) + 30.0 * (fbm(s / 90.0) - 0.5);
    c *= mix(0.72, 1.0, smoothstep(0.0, 110.0, border));
    return c;
}

vec4 chart(Terrain t, vec2 offset, float reveal)
{
    float ppb = uCamera.x, gui = uCamera.y;
    vec2 q0, q1; float ot;
    octaves(offset, q0, q1, ot);
    float wobble = (noise(q0 / 37.0) - 0.5) * 0.6 * gui;
    float coastPx = t.coast * ppb + wobble;
    float rangePx = CoastRange * uBaseInfo.y * ppb;
    vec3 c = vec3(1.0);
    float ink = 0.0;
    vec3 inkColor = Ink;
    // Blocks of height per pixel, outside the branches below (derivatives are undefined in non-uniform control flow).
    float hw = max(fwidth(t.height), 1e-3);
    // Water standing above the sea (lakes, rivers inland) is stored at its surface, so its depth is unknown.
    float lake = smoothstep(1.5, 4.0, t.height);
    if (coastPx < 0.0)
    {
        // Sea: a watercolour wash strongest along the coast, waterlines and soundings.
        float off = -coastPx;
        float depth = max(0.0, -t.height);
        float wash = 0.22 + 0.42 * exp(-off / (26.0 * gui)) - 0.08 * smoothstep(8.0, 60.0, depth);
        c = mix(c, Wash, clamp(wash, 0.0, 0.7));
        float lines = 0.0;
        for (int k = 0; k < 5; k++)
        {
            float fk = float(k);
            float at = (3.6 + fk * 4.4 + fk * fk * 0.7) * gui;
            float pressure = 0.75 + 0.5 * noise(q0 / 23.0 + fk * 7.0);
            float fade = (0.62 - fk * 0.11) * (1.0 - smoothstep(rangePx - 6.0, rangePx - 1.0, at));
            lines = max(lines, line(off - at, 0.42 * gui * pressure) * fade);
        }
        // Depth as charts show it: the shallows tinted in steps (under 12 and under 20 blocks) and depth contours, dense on
        // the shelves (8, 12, 16, 20 blocks; Earth Map keeps 6 blocks of water off its shores) and dotted every 10 blocks
        // beyond, faded where they would crowd together on screen.
        float shoal = 0.16 * (1.0 - clamp((depth - 12.0) / hw + 0.5, 0.0, 1.0)) + 0.10 * (1.0 - clamp((depth - 20.0) / hw + 0.5, 0.0, 1.0));
        c = mix(c, Wash * 0.9, shoal * (1.0 - lake));
        float crowd = 1.0 - smoothstep(0.8, 2.5, hw);
        float isobaths = 0.0;
        for (int k = 0; k < 8; k++)
        {
            float level = k < 4 ? 8.0 + 4.0 * float(k) : 10.0 * float(k - 1);
            float dotted = k >= 4 ? step(0.4, fract(dot(q0, vec2(0.7071)) / 5.0)) : 1.0;
            isobaths = max(isobaths, line((depth - level) / hw, 0.42 * gui) * dotted * (k == 3 ? 0.6 : 0.42));
        }
        // Shallows: stippled sandbanks; a lake's depth is unknown, so none there.
        float shallow = (1.0 - smoothstep(1.0, 7.0, depth)) * (1.0 - lake);
        float dots = mix(stipple(q0, shallow * 0.55, 0.55 * gui), stipple(q1, shallow * 0.55, 0.55 * gui), ot);
        ink = max(max(lines, dots * 0.6), isobaths * crowd * step(6.0 * gui, off));
        inkColor = SeaInk;
        if (t.ice > 0.02)
        {
            // Sea ice: a pale field with light hatching instead of waterlines and soundings.
            c = mix(c, vec3(0.97, 0.98, 0.98), t.ice * 0.85);
            float iceHatch = hatchOctaves(q0, q1, ot, normalize(vec2(1.0, 0.25)), 7.0 * gui, 0.3 * gui) * 0.3;
            ink = mix(ink, iceHatch, smoothstep(0.0, 0.6, t.ice));
        }
    }
    else
    {
        // Land: parchment with a faint warm wash. Plains stay blank; uplands get a sepia
        // relief wash and pen hatching on their shadowed (south-east) slopes.
        c = mix(c, vec3(0.97, 0.90, 0.74), 0.35);
        // Ice changes the paper tint, not the terrain: shade it before adding contours and hatching.
        c = mix(c, vec3(0.975, 0.975, 0.96), t.ice * 0.8);
        float hn = t.height / uHeightScale;
        vec3 n = normalize(vec3(-t.slope * 2.0, 1.0));
        vec3 sun = normalize(vec3(-0.6, -0.6, 0.55));
        float light = dot(n, sun), flat_ = sun.z;
        float relief = smoothstep(0.26, 0.50, hn);
        float shade = clamp((flat_ - light) * 3.5, 0.0, 1.0) * relief;
        float lit = clamp((light - flat_) * 2.5, 0.0, 1.0) * relief;
        c *= 1.0 - 0.26 * shade + 0.06 * lit - 0.07 * smoothstep(0.5, 0.8, hn);
        float h1 = hatchOctaves(q0, q1, ot, normalize(vec2(1.0, 1.0)), 4.0 * gui, 0.36 * gui);
        float h2 = hatchOctaves(q0, q1, ot, normalize(vec2(1.0, -1.0)), 4.0 * gui, 0.34 * gui);
        ink = max(h1 * smoothstep(0.15, 0.45, shade), h2 * smoothstep(0.55, 0.9, shade)) * 0.75;
        // Contours every 10 blocks, every fifth one stronger, where they stay apart on screen.
        float minor = line((fract(t.height / 10.0 + 0.5) - 0.5) * 10.0 / hw, 0.32 * gui) * (1.0 - smoothstep(1.5, 3.0, hw));
        float index = line((fract(t.height / 50.0 + 0.5) - 0.5) * 50.0 / hw, 0.5 * gui) * (1.0 - smoothstep(6.0, 12.0, hw));
        ink = max(ink, max(minor * 0.3, index * 0.5) * smoothstep(4.0, 6.0, t.height));
        if (t.ice > 0.02)
        {
            float iceHatch = hatchOctaves(q0, q1, ot, normalize(vec2(1.0, 0.25)), 7.0 * gui, 0.3 * gui) * 0.35;
            ink = max(ink, iceHatch * t.ice);
            inkColor = mix(Ink, SeaInk, t.ice);
        }
    }
    // Ice edge and coastline.
    float iceEdge = line((t.ice - 0.5) / max(fwidth(t.ice), 1e-3), 0.5) * 0.55;
    float pressure = 0.85 + 0.35 * noise(q0 / 61.0);
    float coast = line(coastPx, mix(0.64, 0.4, lake) * gui * pressure);
    // Rivers: an ink line widening to the carved channel at close zoom.
    float riverPx = t.river * ppb;
    float riverInk = (1.0 - smoothstep(0.05 * gui, 0.55 * gui, riverPx)) * (riverPx < -1.5 * gui ? 0.0 : 1.0);
    float riverBank = line(riverPx, 0.32 * gui);
    if (riverPx < -1.5 * gui && t.coast >= 0.0) c = mix(c, Wash, 0.5);
    // Generalisation: the river network appears from regional zoom inwards.
    float riverZoom = 1.0 - smoothstep(45.0, 110.0, 1.0 / ppb);
    float riverLine = max(riverInk, riverBank) * (t.coast < 0.0 ? 0.0 : 1.0) * riverZoom;
    // Compose: unexplored areas stay blank parchment.
    float a = clamp(ink, 0.0, 1.0) * reveal;
    vec3 col = paper(vScreen) * mix(vec3(1.0), c, reveal);
    col = mix(col, inkColor, a);
    col = mix(col, SeaInk, iceEdge * reveal);
    col = mix(col, vec3(0.30, 0.40, 0.47), riverLine * 0.8 * reveal);
    // Sea-ice edges are ragged; draw them lighter than rock coasts.
    float icy = smoothstep(0.2, 0.8, t.ice);
    vec3 shoreInk = mix(mix(Ink, vec3(0.42, 0.52, 0.58), icy), vec3(0.30, 0.40, 0.47), lake);
    col = mix(col, shoreInk, coast * reveal * (1.0 - 0.35 * icy));
    float major;
    float grat = graticule(offset, major);
    col = mix(col, vec3(0.55, 0.27, 0.18), max(grat * 0.42, major * 0.6));
    return vec4(col, 1.0);
}

// ---------------------------------------------------------------- terrain style

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

// Cross-blended hypsometric tints (Patterson and Jenny, as in Natural Earth): two height ramps, one for humid land (green
// lowlands, tan, brown, grey rock, snow) and one for arid land (sand, ochre, brown, grey rock, snow), mixed by how dry the
// place is, so colour still reads as height while deserts and wet lowlands differ. Forest deepens the humid greens; a cold
// ramp (tundra grey-green to snow by height) takes over where the year is very cold. Temperature and rain are adjusted for altitude as
// vanilla does (Climate.GetAdjustedTemperature, GetRainFall); the temperature byte is -20..40 C.
vec3 ramp6(float h, vec3 c0, vec3 c1, vec3 c2, vec3 c3, vec3 c4, vec3 c5)
{
    vec3 c = mix(c0, c1, smoothstep(0.00, 0.08, h));
    c = mix(c, c2, smoothstep(0.08, 0.20, h));
    c = mix(c, c3, smoothstep(0.20, 0.34, h));
    c = mix(c, c4, smoothstep(0.34, 0.50, h));
    return mix(c, c5, smoothstep(0.58, 0.78, h));
}

vec3 climateLand(vec2 offset, float aboveSea, float hn)
{
    vec4 cl = texture(uClimate, uCamUV + offset / uWorld.xy);
    float celsius = (cl.g * 255.0 - aboveSea / 1.5) / 4.25 - 20.0;
    float rain = clamp(cl.b * 255.0 + aboveSea / 2.0, 0.0, 255.0) / 255.0;
    vec3 humid = ramp6(hn, vec3(0.44, 0.60, 0.35), vec3(0.56, 0.68, 0.41), vec3(0.73, 0.76, 0.51), vec3(0.78, 0.69, 0.50), vec3(0.64, 0.54, 0.43), vec3(0.90, 0.90, 0.90));
    vec3 arid = ramp6(hn, vec3(0.87, 0.80, 0.61), vec3(0.85, 0.75, 0.56), vec3(0.80, 0.67, 0.49), vec3(0.72, 0.58, 0.44), vec3(0.62, 0.53, 0.46), vec3(0.90, 0.90, 0.90));
    // Forest darkens and deepens the humid lowlands and hills (rainforest deepest).
    float forest = smoothstep(0.2, 0.9, cl.r) * (1.0 - smoothstep(0.30, 0.55, hn));
    humid = mix(humid, humid * mix(vec3(0.74, 0.86, 0.72), vec3(0.62, 0.80, 0.62), smoothstep(18.0, 26.0, celsius)), forest);
    float aridity = 1.0 - smoothstep(0.06, 0.42, rain);
    vec3 c = mix(humid, arid, aridity);
    vec3 cold = mix(vec3(0.64, 0.66, 0.60), vec3(0.92, 0.93, 0.95), smoothstep(0.10, 0.45, hn));
    // Tundra where the year is very cold; snow only by height and from the ice data (glaciers), not by temperature:
    // Siberia's and Canada's cold lowlands are taiga and tundra, not ice.
    return mix(c, cold, 1.0 - smoothstep(-12.0, -6.0, celsius));
}

vec4 terrainStyle(Terrain t, vec2 offset, float reveal)
{
    float ppb = uCamera.x, gui = uCamera.y;
    vec3 n = normalize(vec3(-t.slope * 4.0, 1.0));
    float light = clamp(dot(n, normalize(vec3(-0.5, -0.6, 0.62))), 0.0, 1.0);
    vec3 c;
    if (t.coast < 0.0)
    {
        float depth = max(0.0, -t.height);
        c = mix(vec3(0.56, 0.78, 0.84), vec3(0.13, 0.30, 0.48), smoothstep(0.0, 55.0, depth));
        c *= 0.92 + 0.12 * light;
    }
    else
    {
        float hn = t.height / uHeightScale;
        vec3 land = hypsometric(hn);
        // With a climate raster: cross-blended tints, height and dryness together.
        if (uClimateInfo.x > 0.5) land = climateLand(offset, t.height, hn);
        c = mix(land, vec3(0.94, 0.96, 0.98), t.ice);
        c *= 0.58 + 0.62 * light;
    }
    float riverPx = t.river * ppb;
    float river = (1.0 - smoothstep(0.35 * gui, 0.95 * gui, riverPx)) * (1.0 - smoothstep(45.0, 110.0, 1.0 / ppb));
    c = mix(c, vec3(0.22, 0.45, 0.68), river * 0.9);
    c = mix(c, vec3(0.10, 0.18, 0.24), line(t.coast * ppb, 0.45 * gui) * 0.55);
    float major;
    float grat = graticule(offset, major);
    c = mix(c, vec3(1.0), max(grat * 0.3, major * 0.4));
    // Unexplored: dark, faintly cross-hatched.
    vec3 fog = vec3(0.13, 0.15, 0.17) + 0.03 * hatch(vScreen, normalize(vec2(1.0, 1.0)), 6.0, 0.5);
    return vec4(mix(fog, c, reveal), 1.0);
}

// ---------------------------------------------------------------- markers

float sdTriangle(vec2 p, vec2 a, vec2 b, vec2 c)
{
    vec2 e0 = b - a, e1 = c - b, e2 = a - c;
    vec2 v0 = p - a, v1 = p - b, v2 = p - c;
    vec2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
    vec2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
    vec2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);
    float s = sign(e0.x * e2.y - e0.y * e2.x);
    vec2 d = min(min(vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)), vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))), vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

vec4 marker()
{
    vec2 p = vScreen - uMarker.xy;
    float r = uMarker.w;
    float s = sin(uMarker.z), c = cos(uMarker.z);
    p = vec2(c * p.x + s * p.y, -s * p.x + c * p.y) / r;   // heading 0 points up (north)
    float body = min(sdTriangle(p, vec2(0.0, -1.0), vec2(0.62, 0.78), vec2(0.0, 0.42)), sdTriangle(p, vec2(0.0, -1.0), vec2(0.0, 0.42), vec2(-0.62, 0.78))) * r;
    float fill = 1.0 - smoothstep(-0.5, 0.5, body);
    float outline = 1.0 - smoothstep(1.0, 2.0, abs(body + 0.4));
    vec3 col = uStyle == 0 ? vec3(0.66, 0.13, 0.08) : vec3(0.95, 0.30, 0.15);
    if (uMarkerColor.r >= 0.0) col = uMarkerColor.rgb;
    col = mix(col, uStyle == 0 ? Ink : vec3(0.05), outline * 0.9);
    return vec4(col, max(fill, outline) * uMarkerColor.a);
}

vec4 compass()
{
    vec2 p = (vScreen - uMarker.xy) / uMarker.w;
    float r = length(p);
    float a = r > 1e-4 ? atan(p.x, -p.y) : 0.0;   // 0 = north, clockwise
    float px = 1.0 / uMarker.w;
    float ink = 0.0, fill = 0.0, red = 0.0;
    // Rings with degree ticks.
    ink = max(ink, line((r - 0.98) / px, 0.7));
    ink = max(ink, line((r - 0.90) / px, 0.5));
    float tick = abs(fract(a / 6.2831853 * 72.0 + 0.5) - 0.5) / 72.0 * 6.2831853 * r / px;
    ink = max(ink, line(tick, 0.45) * step(0.90, r) * step(r, 0.98));
    // Sixteen-point star: cardinal, intercardinal and minor points.
    for (int i = 0; i < 16; i++)
    {
        float fi = float(i);
        float angle = fi * 0.39269908;
        float len = (i % 4 == 0) ? 0.86 : (i % 2 == 0 ? 0.62 : 0.42);
        float wid = (i % 4 == 0) ? 0.12 : (i % 2 == 0 ? 0.09 : 0.06);
        vec2 dir = vec2(sin(angle), -cos(angle)), side = vec2(dir.y, -dir.x);
        float left = sdTriangle(p, dir * len, side * wid, vec2(0.0));
        float right = sdTriangle(p, dir * len, vec2(0.0), -side * wid);
        float l = 1.0 - smoothstep(-px, px, left), rr = 1.0 - smoothstep(-px, px, right);
        float edge = max(line(left / px, 0.5), line(right / px, 0.5));
        float shaded = (i % 4 == 0) ? l : rr;
        fill = max(fill, max(l, rr));
        ink = max(ink, max(edge, shaded * (i % 4 == 0 ? 0.85 : 0.55)));
        if (i == 0) red = max(red, l + rr);
    }
    ink = max(ink, line((r - 0.06) / px, 0.6));
    vec3 col = vec3(0.95, 0.91, 0.80);
    col = mix(col, Ink, clamp(ink, 0.0, 1.0));
    col = mix(col, vec3(0.62, 0.14, 0.09), clamp(red, 0.0, 1.0) * 0.85);
    float alpha = max(fill, ink) * (1.0 - smoothstep(1.0, 1.0 + px, r));
    return vec4(col, alpha * 0.92);
}

// ---------------------------------------------------------------- winds

// The wind at a point: blocks from the camera, and its place in the world grid. A local field (the barometer's
// weather around the player) is its own square of texels; it ends at its reach.
vec2 windAt(vec2 rel, vec2 worldUV)
{
    if (uWindInfo.w > 0.5) return textureLod(uWind, (rel - uWindRect.xy) / uWindRect.zw, 0.0).rg;
    return textureLod(uWind, worldUV, 0.0).rg;
}

float segment(vec2 p, vec2 a, vec2 b)
{
    vec2 pa = p - a, ba = b - a;
    return length(pa - ba * clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0));
}

// A pilot-chart wind arrow flying with the wind: longer and heavier with strength, and fletched at the tail
// with one feather pair per step (light, moderate, strong, gale). A small circle marks a calm.
// p: pixels from the arrow's centre; returns ink coverage (x) and its halo (y).
vec2 windArrow(vec2 p, vec2 w, float size, float gui)
{
    float s = length(w);
    if (s < 0.03)
    {
        float d = abs(length(p) - 2.4 * gui);
        return vec2(line(d, 0.55 * gui), 1.0 - smoothstep(1.4 * gui, 2.6 * gui, d));
    }
    vec2 dir = w / s;
    vec2 q = vec2(dot(p, dir), dot(p, vec2(-dir.y, dir.x)));   // along the wind, across it
    float k = clamp(s / 0.5, 0.0, 1.0);
    float half_ = size * mix(0.22, 0.5, k);
    float width = mix(0.45, 0.85, k) * gui;
    float head = 5.0 * gui, headW = 2.7 * gui;
    float d = segment(q, vec2(-half_, 0.0), vec2(half_ - head * 0.6, 0.0));
    int feathers = s < 0.1 ? 0 : s < 0.25 ? 1 : s < 0.5 ? 2 : 3;
    for (int i = 0; i < 3; i++)
    {
        if (i >= feathers) break;
        float x = -half_ + float(i) * 2.8 * gui;
        d = min(d, min(segment(q, vec2(x, 0.0), vec2(x - 2.6 * gui, 3.0 * gui)), segment(q, vec2(x, 0.0), vec2(x - 2.6 * gui, -3.0 * gui))));
    }
    float tri = sdTriangle(q, vec2(half_, 0.0), vec2(half_ - head, headW), vec2(half_ - head, -headW));
    float ink = max(line(d, width), 1.0 - smoothstep(-0.5, 0.5, tri));
    float halo = 1.0 - smoothstep(width + 1.0 * gui, width + 2.4 * gui, min(d, max(tri, 0.0)));
    return vec2(ink, halo);
}

vec4 winds()
{
    float ppb = uCamera.x, gui = uCamera.y, spacing = uWindInfo.y;
    vec2 offset = (vScreen - (uViewport.xy + uViewport.zw * 0.5)) / ppb;   // blocks from the camera
    float u = uCamUV.x + offset.x / uWorld.x, z = uCamUV.y * uWorld.y + offset.y;
    // Arrows sit on a world-anchored lattice of 2^n points around the world (so it closes over the seam),
    // spaced one to two `spacing` apart on screen. The next finer lattice fades in before each doubling;
    // its even points are the coarse lattice, so arrows never jump.
    float lg = log2(max(1.0, uWorld.x * ppb / spacing));
    float n = floor(lg), t = lg - n;
    vec2 cover = vec2(0.0);
    for (int layer = 0; layer < 2; layer++)
    {
        float count = exp2(n + float(layer)), cell = uWorld.x / count;
        vec2 index = floor(vec2(u * count, z / cell) + 0.5);
        float fade = 1.0;
        if (layer == 1)
        {
            if (mod(index.x, 2.0) < 0.5 && mod(index.y, 2.0) < 0.5) continue;
            fade = smoothstep(0.6, 0.95, t);
            if (fade <= 0.0) continue;
        }
        float pz = index.y * cell;
        if (pz < 0.0 || pz > uWorld.y) continue;
        vec2 point = vec2((index.x / count - uCamUV.x) * uWorld.x, pz - uCamUV.y * uWorld.y);
        if (uWindInfo.w > 0.5)
        {
            // Within the local field's reach only, fading out before its edge.
            fade *= 1.0 - smoothstep(0.80, 0.96, length(point - (uWindRect.xy + uWindRect.zw * 0.5)) / (uWindRect.z * 0.5));
            if (fade <= 0.0) continue;
        }
        vec2 w = windAt(point, vec2(index.x / count, pz / uWorld.y));
        cover = max(cover, windArrow((offset - point) * ppb, w, spacing * 0.6, gui) * fade);
    }
    // Belt boundaries of the prevailing winds, dashed: where the easterlies meet the westerlies (horse latitudes,
    // polar front), and where the trades of both hemispheres meet (the doldrums, following the sun).
    // Derivatives stay outside branches: they are undefined in non-uniform control flow.
    vec2 here = windAt(offset, vec2(u, z / uWorld.y));
    vec2 gradient = fwidth(here);
    vec2 change = max(gradient, vec2(1e-7));
    // An exactly calm region is a zero field, not a boundary. Otherwise 0/change covers the whole region
    // and the dash mask fills it with blue vertical stripes (Earth's monthly winds over land).
    float divide = line(here.x / change.x, 0.6 * gui) * step(1e-7, gradient.x);
    // The meridional wind also fades to zero toward the poles; only the tropics have a doldrums line.
    vec3 g = geo(offset);
    float tropics = uGeoInfo.x > 0.5 && g.z < 0.5 ? 1.0 - smoothstep(25.0, 35.0, abs(g.y)) : 0.0;
    float itcz = line(here.y / change.y, 0.6 * gui) * step(1e-7, gradient.y) * (1.0 - smoothstep(-0.12, -0.04, here.x)) * tropics;
    float dash = step(0.45, fract((vScreen.x - uViewport.x) / (14.0 * gui)));
    float inside = step(0.0, z) * step(z, uWorld.y);
    float belts = max(divide, itcz) * dash * 0.6 * inside * step(0.5, uWindInfo.z);
    // The local field's reach: a dashed circle.
    float reach = uWindInfo.w > 0.5 ? line((length(offset - (uWindRect.xy + uWindRect.zw * 0.5)) - uWindRect.z * 0.48) * ppb, 0.6 * gui) * step(0.5, fract(dot(vScreen, vec2(1.0)) / (12.0 * gui))) * 0.75 : 0.0;
    belts = max(belts, reach);
    cover.x = max(cover.x, belts);
    cover.y = max(cover.y, belts * 0.6);
    vec3 inkColor = uStyle == 0 ? vec3(0.13, 0.24, 0.40) : vec3(0.97, 0.98, 1.0);
    vec3 haloColor = uStyle == 0 ? vec3(0.96, 0.93, 0.84) : vec3(0.04, 0.07, 0.10);
    float haloAlpha = uStyle == 0 ? 0.7 : 0.55;
    return vec4(mix(haloColor, inkColor, cover.x), max(cover.x, cover.y * haloAlpha));
}

// ---------------------------------------------------------------- day and night

// Day, the three twilights and night, as on a day-and-night world map: by the sun's elevation, shaded in steps (civil
// twilight to -6 degrees, nautical to -12, astronomical to -18, then night), with a line where the sun is on the horizon.
// Called in uniform control flow: it takes derivatives.
vec3 dayNight(vec3 col, vec2 offset, float inside)
{
    vec2 uv = uCamUV + offset / uWorld.xy;
    float e = (texture(uDay, uv).r * 255.0 - 128.0) / 4.0;
    float w = max(fwidth(e), 1e-3);
    float shade = 0.0;
    shade += 0.22 * clamp((0.0 - e) / w + 0.5, 0.0, 1.0);
    shade += 0.16 * clamp((-6.0 - e) / w + 0.5, 0.0, 1.0);
    shade += 0.14 * clamp((-12.0 - e) / w + 0.5, 0.0, 1.0);
    shade += 0.12 * clamp((-18.0 - e) / w + 0.5, 0.0, 1.0);
    shade *= inside;
    col = mix(col, col * (uStyle == 1 ? vec3(0.22, 0.26, 0.42) : vec3(0.40, 0.45, 0.65)), shade / 0.64 * 0.85);
    float horizon = line(e / w, 0.6 * uCamera.y);
    return mix(col, uStyle == 1 ? vec3(0.95, 0.85, 0.45) : vec3(0.55, 0.36, 0.10), horizon * 0.8 * inside);
}

// ---------------------------------------------------------------- main

void main()
{
    // Nothing drawn, nothing written: a transparent fragment must not leave depth behind, or the markers' and the
    // wind layer's quads hide what other map layers draw under them afterwards (Magelan's ships).
    if (uMode == 2) { outColor = marker(); if (outColor.a < 0.004) discard; return; }
    if (uMode == 3) { outColor = compass(); if (outColor.a < 0.004) discard; return; }
    if (uMode == 4) { outColor = winds(); if (outColor.a < 0.004) discard; return; }
    vec2 offset = (vScreen - (uViewport.xy + uViewport.zw * 0.5)) / uCamera.x;   // blocks from the camera
    float z = uCamUV.y * uWorld.y + offset.y;
    Terrain t;
    float alpha = 1.0;
    if (uMode == 1)
    {
        t = sampleTile(offset);
        alpha = t.known * uTileInfo.w;
        // Not discarded here: derivatives below need the whole quad.
        if (uTileSlot.w < 0.5 && uRefInfo.w > 0.5 && !refines(offset)) alpha = 0.0;
        if (uBaseInfo.x > 0.5)
        {
            // A tile knows coasts within its sampling margin; farther out the world raster does.
            Terrain b = sampleBase(offset);
            float margin = 22.0 * uTileInfo.z;
            if (abs(t.coast) > margin) t.coast = sign(t.coast) * max(abs(b.coast), margin);
            // Local relief lives in the detail tile: replacing its slope with the regional slope blurred hills as soon
            // as the bake arrived. So do rivers: every detail tile, predicted or recorded, draws them from its own water
            // only, never from the world raster's river channel (decode leaves a tile's river Far). Predicted detail is drawn
            // only where a raster texel spans two pixels or more, and there the channel, interpolated from texels hundreds of
            // blocks wide on a large world (489 on Earth at 1:10), is no line: a narrow river shows only near texel
            // centres, and a wide one becomes a lens up to a texel long round the tile's own river, a second, darker
            // river beside it (the Niagara at 1:10; 0.4.3 had kept it from one-block tiles only). While tiles fade in,
            // the world raster under them still draws its river lines.
        }
    }
    else if (uBaseInfo.x > 0.5)
        t = sampleBase(offset);
    else
    {
        t = Terrain(Far, 0.0, 0.0, Far, 0.0, vec2(0.0));
    }
    // Earth's river courses run on through the lakes they cross; on water only the water is drawn.
    if (t.coast < 0.0) t.river = Far;
    // A detail tile's coverage is its alpha: fading its colour to blank parchment as well drew pale rims round recordings.
    float reveal = (uMode == 1 ? 1.0 : t.known) * explored(offset);
    // Beyond the poles, and beyond the east and west edges of a map that does not wrap.
    float x = uCamUV.x * uWorld.x + offset.x;
    if (z < 0.0 || z > uWorld.y || uWorld.z < 0.5 && (x < 0.0 || x > uWorld.x)) reveal = 0.0;
    float r = smoothstep(0.30, 0.70, reveal + (fbm((offset + uAnchor) * uCamera.x / 48.0) - 0.5) * 0.35);
    if (uWorld.w > 0.5) r = reveal;
    outColor = uStyle == 1 ? terrainStyle(t, offset, r) : chart(t, offset, r);
    if (uDayInfo.x > 0.5) outColor.rgb = dayNight(outColor.rgb, offset, z < 0.0 || z > uWorld.y ? 0.0 : 1.0);
    outColor.a *= alpha;
    if (outColor.a < 0.004) discard;
}
