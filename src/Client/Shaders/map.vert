// Quads in GUI pixel coordinates, transformed by the game's current GUI matrices.
layout(location = 0) in vec2 aPos;
uniform mat4 uProj;
uniform mat4 uView;
uniform float uDepth;
out vec2 vScreen;

void main()
{
    vScreen = aPos;
    gl_Position = uProj * uView * vec4(aPos, uDepth, 1.0);
}
