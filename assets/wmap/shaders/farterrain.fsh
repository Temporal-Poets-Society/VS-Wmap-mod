#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Wmap far terrain, the smooth mesh: colour, light and haze in shaderincludes/wmapfarshade.ash (shared with farcells.fsh).

#include wmapfarvaryings.ash
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include wmapfartiles.ash
#include wmapfaralbedo.ash
#include wmapfarshade.ash
