#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view (WM-P06 prototype): the top and colour of every terrace cell around the camera, one texel per
// cell, for farcells.vsh; drawn again only when the map moves or its tiles change (Client/MidDistanceDraw.cs). A cell takes
// the two-block recording (recorded block by block: the world differs from the prediction there); else the four-block
// recording (each sample holds its cell's mean generated ground, WM-D61); else the prediction, with forest canopy. Land is rounded to a whole
// block, water lies at its surface (Core/TerracePatch.CellTop). Its colour is worked out here once, so
// the terraces' fragments only light and haze it. Cells are worked out 64 blocks beyond the terraces' ring on both sides,
// so the camera can move within its patch meanwhile.

uniform vec4 wmapCells;                   // unused; cell size (blocks); the map's north-west corner (blocks from the snapped origin)

layout(location = 0) out vec4 outTop;     // R: top (world y); -10000 outside the terraces
layout(location = 1) out vec4 outColor;   // RGB the cell's colour (water: its tint); A land 0, water 0.8 + 0.2 x how deep

#include wmapfartiles.ash
#include wmapfarheight.ash
#include wmapfaralbedo.ash

// Predicted forest as a canopy of crowns, not a plate (0.5.3-rc.9, WM-0020): a block or two up or down per cell and per pair
// of cells, as recorded forest stands. rc.8 drew it flat, so beside the ragged recorded forest it read as land.
float wmapRoughCanopy(vec2 local, float cell, float canopy)
{
    if (canopy < 2.0) return canopy;
    vec2 c = floor(local / cell);
    float a = fract(sin(dot(c, vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
    float b = fract(sin(dot(floor(c / 2.0), vec2(39.3468, 11.135))) * 24634.6345) - 0.5;
    return max(2.0, canopy + 2.0 * a + 2.0 * b);
}

void main()
{
    float cell = wmapCells.y;
    vec2 local = wmapCells.zw + (floor(gl_FragCoord.xy) + 0.5) * cell;
    float dist = wmapCameraDistance(local);
    if (dist < wmapRange.x - 64.0 - 3.0 * cell || dist > wmapRange.y + 64.0 + 3.0 * cell) { outTop = vec4(-10000.0); outColor = vec4(0.0); return; }
    // The prediction: for the canopy, the colours, and where nothing is recorded.
    vec4 chart = textureLod(wmapChart, wmapChartUV.xy + local * wmapChartUV.zw, 0.0);
    vec4 band, bandBiome;
    float w = wmapBandAt(local, band, bandBiome);
    vec3 predicted = wmapSurface(chart, band, w);
    vec2 climateUV;
    float climateReady = wmapClimateTile(local, climateUV);
    vec4 biome = wmapBiomeFrom(climateReady, climateUV, w, bandBiome);
    vec4 season = wmapSeasonFrom(climateReady, climateUV);
    float h0 = max(0.0, predicted.y);
    vec4 h, c, coarseH, coarseC;
    float top;
    // Recorded, finest first; recorded land keeps the game's colour unless the predicted forest's canopy stands on it.
    bool fine = wmapRecordedAt(local, 1, false, h, c) >= 0.5;
    bool coarse = !fine && wmapRecordedAt(local, 0, false, coarseH, coarseC) >= 0.5;
    if (coarse) { h = coarseH; c = coarseC; }
    if (fine || coarse)
    {
        if (h.a > 0.5) { outTop = vec4(h.r); outColor = vec4(wmapWaterTintAt(biome), 0.8 + 0.2 * smoothstep(0.0, 55.0, -predicted.y)); return; }
        // The treetops the recorder saw over this cell's texel (0.5.3-rc.4, alpha: a quarter block a step, 255 not known), so
        // a discovered forest stands at its height with its gaps. Where it is not known: cells recorded block by block are what
        // the world has there (structures, edits), no canopy on them; four-block cells take the canopy around them.
        float recordedCanopy = c.a < 0.995 ? floor(c.a * 255.0 / 4.0 + 0.5) : -1.0;
        // A tree cell stands at the canopy of the twelve blocks around it, give or take a block (0.5.3-rc.7, WM-0020): each
        // two-block cell at its own treetop drew the recorded forest as a city of grey boxes, its walls most of what was seen.
        // Clearings (no canopy) stay clearings, so gaps and forest edges keep their place.
        if (recordedCanopy >= 2.0)
        {
            vec2 around = wmapRecordedCanopy(local, 1);
            float jitter = fract(sin(dot(floor(local / cell), vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
            if (around.y > 0.0) recordedCanopy = max(2.0, floor(around.x + jitter * 2.0 + 0.5));
        }
        float canopy = recordedCanopy >= 0.0 ? recordedCanopy : fine ? 0.0 : floor(wmapRoughCanopy(local, cell, wmapCanopy(local, predicted, h0, w, bandBiome)) + 0.5);
        // Recorded land stands at its recorded height (0.5.3, WM-0021, WM-D61): a four-block sample holds its cell's mean
        // generated ground, and a two-block cell between four of them all land takes their bilinear mean. Until then coarse
        // land took the near band's prediction (WM-D29, WM-D33), whose heights are 8-bit steps of up to 3.7 blocks on high
        // ground: on Earth's GLO-90 relief only about half the discovered ground lay within a block of the real one.
        float landTop = h.r;
        if (coarse)
        {
            vec4 hf, cf;
            if (wmapRecordedAt(local, 0, true, hf, cf) > 0.99 && hf.a < 0.01) landTop = floor(hf.r + 0.5);
        }
        top = landTop + canopy;
        // A cell with a canopy is leaves: the climate's leaf colour, as a full forest has it.
        vec3 land = wmapLandAlbedo(top - wmapOrigin.w, canopy >= 2.0 ? vec4(1.0, biome.gba) : vec4(0.0, biome.gba), season);
        outTop = vec4(top);
        outColor = vec4(canopy < 2.0 && h.b > 0.5 ? c.rgb : land, 0.0);
        return;
    }
    if (predicted.x < 0.0)
    {
        // Water at its surface: the sea at the sea surface, a lake at its own.
        outTop = vec4(wmapOrigin.w + h0);
        outColor = vec4(wmapWaterTintAt(biome), 0.8 + 0.2 * smoothstep(0.0, 55.0, -predicted.y));
        return;
    }
    top = floor(wmapOrigin.w + h0 + wmapRoughCanopy(local, cell, wmapCanopy(local, predicted, h0, w, bandBiome)) + 0.5);
    outTop = vec4(top);
    outColor = vec4(mix(wmapLandAlbedo(top - wmapOrigin.w, biome, season), vec3(0.94, 0.96, 0.98), predicted.z), 0.0);
}
