#version 330 core

// Wmap mid-distance view: one triangle covering the cell map (farcellmap.fsh), from the vertex id alone.

void main()
{
    vec2 p = vec2(float((gl_VertexID & 1) << 2), float((gl_VertexID & 2) << 1)) - 1.0;
    gl_Position = vec4(p, 0.0, 1.0);
}
