#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view (WM-P06 prototype): terraces and recorded structures, built on the GPU.
// Terraces (wmapMode 1): instanced patches of Core/TerracePatch.cs cells. Each cell is a flat top at its cell's top from
// the cell map (farcellmap.fsh: whole blocks on land) with walls on its east and south edges down or up to its
// neighbour's top, so every edge between two cells is closed once.
// Structures (wmapMode 2): one box per two-block recording (Client/RecordedTiles.cs), from its top down into the smooth
// mesh's ground, where the terraces end; boxes no higher than the ground are not drawn.

layout(location = 0) in vec4 cellIn;      // terraces: cell i, j in the patch, corner x, z; structures: corner x, y, z (0..1), face
layout(location = 1) in vec2 sideIn;      // terraces: 0 top, 1 east wall, 2 south wall; 1 at the neighbour's top
layout(location = 2) in vec4 patchIn;     // terraces, per patch: its first cell in the cell map (x, z)
layout(location = 3) in vec4 boxIn;       // structures, per box: west edge (blocks east of the ring's start), north edge, top (world y), size
layout(location = 4) in vec4 boxColorIn;  // structures: the game's colour

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;                  // camera at the origin
uniform float wmapMode;                   // 1 terraces, 2 structures
uniform vec4 wmapCells;                   // unused; cell size (blocks); the cell map's north-west corner (blocks from the snapped origin)
uniform sampler2D wmapCellTop;            // per cell: R top (world y); below -9000 outside the terraces
uniform sampler2D wmapCellColor;          // per cell: RGB its colour (farcellmap.fsh); A 1 water, else land

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

out vec3 wmapPos;
out vec2 wmapUV;
out vec2 wmapLocal;
out vec3 wmapNormal;
out float wmapDistance;
out float fogAmount;
out vec4 rgbaFog;
flat out vec4 wmapFlat;

#include vertexflagbits.ash
#include fogandlight.vsh
#include shadowcoords.vsh
#include wmapfartiles.ash
#include wmapfarheight.ash

const vec4 Nowhere = vec4(0.0, 0.0, 2.0, 1.0);

void place(vec2 local, float top, float wet, vec3 normal, vec4 flatColor)
{
    float dist = wmapCameraDistance(local);
    // Terraces past their edge sink under the smooth mesh (wmapJoinPush); structures stand on it.
    float join = wmapMode < 1.5 ? wmapJoinPush(dist, 1.0) : 0.0;
    float y = top - wmapPushAt(dist, max(0.0, top - wmapOrigin.w), wet) - join - wmapDrop(dist) - wmapOrigin.z;
    vec3 pos = vec3(local.x + wmapOrigin.x, y, local.y + wmapOrigin.y);
    wmapPos = pos;
    wmapUV = wmapChartUV.xy + local * wmapChartUV.zw;
    wmapLocal = local;
    wmapNormal = normal;
    wmapDistance = length(pos.xz);
    wmapFlat = flatColor;
    fogAmount = getFogLevel(vec4(pos, 1.0), fogMinIn, fogDensityIn);
    gl_Position = projectionMatrix * viewMatrix * vec4(pos, 1.0);
}

void terrace()
{
    float cell = wmapCells.y;
    ivec2 size = textureSize(wmapCellTop, 0);
    ivec2 own = ivec2(patchIn.xy + cellIn.xy);
    // A cell wholly outside the draw range is not drawn (0.5.5, WM-0054): the cell map holds 64 blocks more on either side of
    // the terraces' ring (farcellmap.fsh), and every fragment of those cells was discarded (wmapfarshade.ash: a fragment's
    // distance is its corners', interpolated). Corners and walls lie within 0.75 of a cell of its centre.
    float centre = wmapCameraDistance(wmapCells.zw + (vec2(own) + 0.5) * cell);
    if (centre + 0.75 * cell < wmapRange.x || centre - 0.75 * cell > wmapRange.y) { gl_Position = Nowhere; return; }
    int side = int(sideIn.x + 0.5);
    ivec2 next = own + (side == 1 ? ivec2(1, 0) : side == 2 ? ivec2(0, 1) : ivec2(0));
    float ownTop = texelFetch(wmapCellTop, own, 0).r;
    if (ownTop < -9000.0) { gl_Position = Nowhere; return; }
    vec4 ownColor = texelFetch(wmapCellColor, own, 0);
    float nextTop = ownTop;
    vec4 nextColor = ownColor;
    if (side != 0 && next.x < size.x && next.y < size.y)
    {
        float t = texelFetch(wmapCellTop, next, 0).r;
        if (t >= -9000.0) { nextTop = t; nextColor = texelFetch(wmapCellColor, next, 0); }
    }
    bool atNext = sideIn.y > 0.5;
    float top = atNext ? nextTop : ownTop;
    float wet = (atNext ? nextColor.a : ownColor.a) > 0.75 ? 1.0 : 0.0;
    // A wall's foot reaches half a block below the lower cell's top, out of sight under it (0.5.3, WM-0028): its corners take
    // their push-down and the planet's drop at their own distance, not quite where the lower top's corners lie, and a pixel of
    // sky showed through the sliver between them at coasts.
    if (side != 0 && ownTop != nextTop && top == min(ownTop, nextTop)) top -= 0.5;
    vec3 normal = vec3(0.0, 1.0, 0.0);
    // A wall faces the lower cell and takes the higher cell's colour; walls between equal tops have no area.
    vec4 flatColor = ownColor;
    if (side != 0)
    {
        float s = ownTop >= nextTop ? 1.0 : -1.0;
        normal = side == 1 ? vec3(s, 0.0, 0.0) : vec3(0.0, 0.0, s);
        flatColor = ownTop >= nextTop ? ownColor : nextColor;
    }
    // Walls reach a little past the cell's corners: where two walls meet at a corner their edges are not shared (their
    // heights differ), and without the overlap single pixels of the sky showed through there.
    vec2 corner = cellIn.zw;
    if (side == 1) corner.y = corner.y < 0.5 ? -0.02 : 1.02;
    else if (side == 2) corner.x = corner.x < 0.5 ? -0.02 : 1.02;
    vec2 local = wmapCells.zw + (vec2(own) + corner) * cell;
    place(local, top, wet, normal, flatColor);
}

void structure()
{
    // The box's corner, west and north edges counted from the snapped origin; across the seam, the nearest lap.
    float dx = boxIn.x - wmapOriginWorld.x;
    if (wmapWorld.z > 0.5) dx -= wmapWorld.x * floor(dx / wmapWorld.x + 0.5);
    vec2 corner = vec2(dx, boxIn.y - wmapOriginWorld.y);
    vec2 centre = corner + 0.5 * boxIn.w;
    float dist = wmapCameraDistance(centre);
    float w;
    vec4 bandBiome;
    float ground = wmapOrigin.w + max(0.0, wmapGroundAt(centre, 0.0, w, bandBiome).y);
    // Only what stands above the smooth ground, between the terraces' edge and the band's.
    if (boxIn.z < ground + 0.5 || dist < wmapRange.x - boxIn.w || dist > wmapRange.y + boxIn.w) { gl_Position = Nowhere; return; }
    int face = int(cellIn.w + 0.5);
    vec3 normal = face == 0 ? vec3(0.0, 1.0, 0.0) : face == 1 ? vec3(-1.0, 0.0, 0.0) : face == 2 ? vec3(1.0, 0.0, 0.0) : face == 3 ? vec3(0.0, 0.0, -1.0) : vec3(0.0, 0.0, 1.0);
    float top = mix(ground - 2.0, boxIn.z, cellIn.y);
    place(corner + cellIn.xz * boxIn.w, top, 0.0, normal, vec4(boxColorIn.rgb, 0.5));
}

void main()
{
    rgbaFog = rgbaFogIn;
#if SHADOWQUALITY > 0
    shadowCoordsFar = vec4(0.0);
    blockBrightness = 0.0;
#endif
#if SHADOWQUALITY > 1
    shadowCoordsNear = vec4(0.0);
#endif
    if (wmapMode < 1.5) terrace(); else structure();
}
