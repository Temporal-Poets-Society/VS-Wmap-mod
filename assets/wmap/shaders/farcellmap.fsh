#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view (WM-P06 prototype): the top and colour of every terrace cell around the camera, one texel per
// cell, for farcells.vsh; drawn again only when the map moves or its tiles change (Client/MidDistanceDraw.cs). A cell takes
// the two-block recording (recorded block by block: the world differs from the prediction there); else the prediction where
// the near band predicts land and the four-block recording says land too (that sample only stands for its 4 x 4 cell; see
// wmapWithRecorded); else the four-block recording; else the prediction, with forest canopy. Land is rounded to a whole
// block, water lies at its surface (Core/TerracePatch.CellTop). Its colour is worked out here once, so
// the terraces' fragments only light and haze it. Cells are worked out 64 blocks beyond the terraces' ring on both sides,
// so the camera can move within its patch meanwhile.

uniform vec4 wmapCells;                   // unused; cell size (blocks); the map's north-west corner (blocks from the snapped origin)

layout(location = 0) out vec4 outTop;     // R: top (world y); -10000 outside the terraces
layout(location = 1) out vec4 outColor;   // RGB the cell's colour (water: its tint); A land 0, water 0.8 + 0.2 x how deep

#include wmapfartiles.ash
#include wmapfarheight.ash
#include wmapfaralbedo.ash

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
    vec4 biome = wmapBiomeAt(local, w, bandBiome);
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
        // Cells recorded block by block are what the world has there (structures, edits): no canopy on them.
        float canopy = fine ? 0.0 : floor(wmapCanopy(local, predicted, h0, w, bandBiome) + 0.5);
        bool asPredicted = !fine && w > 0.5 && predicted.x >= 0.0;
        top = (asPredicted ? floor(wmapOrigin.w + h0 + 0.5) : h.r) + canopy;
        vec3 land = wmapLandAlbedo(top - wmapOrigin.w, canopy >= 2.0 ? biome : vec4(0.0, biome.gba));
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
    top = floor(wmapOrigin.w + h0 + wmapCanopy(local, predicted, h0, w, bandBiome) + 0.5);
    outTop = vec4(top);
    outColor = vec4(mix(wmapLandAlbedo(top - wmapOrigin.w, biome), vec3(0.94, 0.96, 0.98), predicted.z), 0.0);
}
