#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap far terrain: a static clipmap mesh (Core/FarMesh.cs) displaced from the world chart, the same
// semantic texture the atlas draws (Core/Semantic.cs: G = height relative to the sea, signed square-root curve),
// nearer the camera from the near band's 4-block detail tiles (shaderincludes/wmapfartiles.ash), and with the mid-distance
// view from the player's own recordings over them (shaderincludes/wmapfarheight.ash).

layout(location = 0) in vec4 vertexIn;    // local x, z from the snapped origin; stitch offset x, z

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;                  // camera at the origin

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

out vec3 wmapPos;                         // relative to the camera
out vec2 wmapUV;
out vec2 wmapLocal;                       // blocks from the snapped origin
out vec3 wmapNormal;
out float wmapDistance;
out float fogAmount;
out vec4 rgbaFog;
flat out vec4 wmapFlat;                   // terraces and structures only (farcells.vsh)

#include vertexflagbits.ash
#include fogandlight.vsh
// Declares the shadow varyings fogandlight.fsh expects at higher shadow settings; far terrain casts and takes no shadows.
#include shadowcoords.vsh
#include wmapfartiles.ash
#include wmapfarheight.ash

void main()
{
    vec2 local = vertexIn.xy;

    // Level of detail from position only, so vertices shared by two rings agree.
    float cheb = max(abs(local.x), abs(local.y));
    float ring = max(0.0, ceil(log2(max(cheb, 1.0) / wmapChartInfo.y) - 1e-4));
    float cell = wmapChartInfo.z * exp2(ring);
    float lod = log2(max(1.0, cell / wmapChartInfo.x));

    float y;
    if (vertexIn.z != 0.0 || vertexIn.w != 0.0)
    {
        // Midway along a coarser ring's edge: lie exactly on that edge.
        y = 0.5 * (wmapFinalY(local - vertexIn.zw, lod) + wmapFinalY(local + vertexIn.zw, lod));
    }
    else y = wmapFinalY(local, lod);

    vec2 dx = vec2(cell, 0.0), dz = vec2(0.0, cell);
    float hx = wmapGround(local + dx, lod) - wmapGround(local - dx, lod);
    float hz = wmapGround(local + dz, lod) - wmapGround(local - dz, lod);
    wmapNormal = normalize(vec3(-hx, 2.0 * cell, -hz));

    vec3 pos = vec3(local.x + wmapOrigin.x, y, local.y + wmapOrigin.y);

    wmapPos = pos;
    wmapUV = wmapChartUV.xy + local * wmapChartUV.zw;
    wmapLocal = local;
    wmapDistance = length(pos.xz);
    wmapFlat = vec4(0.0);
    fogAmount = getFogLevel(vec4(pos, 1.0), fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;
#if SHADOWQUALITY > 0
    shadowCoordsFar = vec4(0.0);
    blockBrightness = 0.0;
#endif
#if SHADOWQUALITY > 1
    shadowCoordsNear = vec4(0.0);
#endif
    gl_Position = projectionMatrix * viewMatrix * vec4(pos, 1.0);
}
