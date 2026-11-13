#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view: a distant light's glow, a bright core in a soft halo, added to what is behind it (and to the
// game's glow buffer, so it blooms like the game's own lights).

in vec2 corner;
in float intensity;
in float fogAmount;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

void main()
{
    float r = length(corner);
    if (r > 1.0) discard;
    float core = 1.0 - smoothstep(0.18, 0.4, r);
    float halo = (1.0 - r) * (1.0 - r) * 0.45;
    float a = (core + halo) * intensity * (1.0 - fogAmount);
    outColor = vec4(vec3(1.0, 0.80, 0.50) * a, 0.0);
    outGlow = vec4(0.6 * a, 0.0, 0.0, 0.0);
#if SSAOLEVEL > 0
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
#endif
}
