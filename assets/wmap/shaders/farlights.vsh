#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view (WM-P06 prototype): distant lights from the player's recordings (Core/LightPoints), one glow per
// merged light, drawn after the game's chunks so the ground in front hides them. They sink with the planet's curve like the
// ground (WM-D39), fade in the game's fog and fade out by day.

layout(location = 0) in vec2 cornerIn;    // -1..1
layout(location = 1) in vec4 lightIn;     // blocks east of the ring's start, world y, z, brightness 0..1

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;                  // camera at the origin
uniform vec4 wmapLightCamera;             // camera: blocks east of the ring's start, z, world y; ring width (0: no wrap)
uniform vec4 wmapLightRange;              // from, to (blocks from the camera); 1 / (2 x the planet's radius); where the curve starts
uniform vec4 wmapLightView;               // viewport width, height (pixels); daylight; eye height above the sea surface
uniform float fogMinIn;
uniform float fogDensityIn;

out vec2 corner;
out float intensity;
out float fogAmount;

#include vertexflagbits.ash
#include fogandlight.vsh

void main()
{
    float dx = lightIn.x - wmapLightCamera.x;
    if (wmapLightCamera.w > 0.0) dx -= wmapLightCamera.w * floor(dx / wmapLightCamera.w + 0.5);
    vec3 pos = vec3(dx, lightIn.y - wmapLightCamera.z, lightIn.z - wmapLightCamera.y);
    float dist = length(pos.xz);
    float over = max(0.0, dist - wmapLightRange.w);
    pos.y -= wmapLightRange.z * over * over;
    // Behind the sea's bulge, as far terrain discards ground there (farterrain.fsh).
    float eye = wmapLightView.w;
    if (wmapLightRange.z > 0.0 && eye > 0.0)
    {
        float t = sqrt(wmapLightRange.w * wmapLightRange.w + eye / wmapLightRange.z), o = t - wmapLightRange.w;
        float grazing = -(wmapLightRange.z * o * o + eye) / t;
        if (dist > t && pos.y < grazing * dist) { gl_Position = vec4(0.0, 0.0, 2.0, 1.0); return; }
    }
    float night = 1.0 - smoothstep(0.35, 0.75, wmapLightView.z);
    float fade = smoothstep(wmapLightRange.x, wmapLightRange.x + 48.0, dist) * (1.0 - smoothstep(wmapLightRange.y - 256.0, wmapLightRange.y, dist));
    intensity = lightIn.w * night * fade;
    // A little toward the camera, so the light's own building does not hide its glow.
    pos -= normalize(pos) * min(0.5 * dist, 2.0 + 0.004 * dist);
    vec4 clip = projectionMatrix * viewMatrix * vec4(pos, 1.0);
    if (intensity <= 0.002 || clip.w <= 0.0) { gl_Position = vec4(0.0, 0.0, 2.0, 1.0); return; }
    // At least a few pixels across at any distance, larger for a brighter light.
    float radius = 2.5 + 5.0 * lightIn.w;
    clip.xy += cornerIn * radius * 2.0 / wmapLightView.xy * clip.w;
    corner = cornerIn;
    fogAmount = getFogLevel(vec4(pos, 1.0), fogMinIn, fogDensityIn);
    gl_Position = clip;
}
