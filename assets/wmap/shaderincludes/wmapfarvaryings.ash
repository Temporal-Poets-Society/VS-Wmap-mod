// Wmap far terrain's fragment inputs, outputs and the game's uniforms it sets, shared by farterrain.fsh and farcells.fsh.
// Include first: the game's includes (fogandlight.fsh, skycolor.fsh) read some of them.

in vec3 wmapPos;                          // relative to the camera
in vec2 wmapUV;
in vec2 wmapLocal;                        // blocks from the snapped origin
in vec3 wmapNormal;
in float wmapDistance;
in float fogAmount;
in vec4 rgbaFog;
flat in vec4 wmapFlat;                    // terraces and structures: RGB their colour; A 1 water, else land

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif
