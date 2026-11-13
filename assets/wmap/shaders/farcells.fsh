#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap mid-distance view: terraces and recorded structures (farcells.vsh), coloured as far terrain is
// (shaderincludes/wmapfarshade.ash).

#include wmapfarvaryings.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include wmapfartiles.ash
#include wmapfaralbedo.ash
#include wmapfarshade.ash
