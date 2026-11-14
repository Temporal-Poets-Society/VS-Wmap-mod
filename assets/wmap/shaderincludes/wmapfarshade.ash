// Wmap far terrain's colour, shared by farterrain.fsh (the smooth mesh) and farcells.fsh (terraces and recorded
// structures): coloured like the atlas's Terrain style, or with the game's own climate tints where forest and climate are
// known (Earth Map), or with the game's own colours where the player recorded the ground (the mid-distance view); lit by
// the game's sun and daylight and faded into the game's own sky colour along the view ray, so its far edge disappears
// into the horizon. Terraces and structures bring their colour from their cell (farcellmap.fsh, RecordedTiles' boxes).
// Include after wmapfarvaryings.ash, fogandlight.fsh, dither.fsh, skycolor.fsh, wmapfartiles.ash and wmapfaralbedo.ash.

uniform vec4 wmapRange;                   // draw from, draw to, push-down, 1 when the world wraps east-west
uniform vec2 wmapCurve;                   // 1 / (2 x the planet's radius) (0: flat); blocks where the curve starts (wmapfarheight.ash)
uniform vec4 wmapPush;                    // push-down fade, far terrain's far edge, where far terrain starts (wmapfarheight.ash); haze runs between them; where the terraces end
uniform vec3 wmapAmbient;                 // the game's ambient light colour (AmbientManager.BlendedAmbientColor)
uniform float wmapMode;                   // 0 the smooth mesh, 1 terraces, 2 recorded structures
uniform sampler2D wmapLoaded;             // chunk columns around the camera (Core/FarStart.Scan): R 1 loaded, 0 not yet
uniform vec4 wmapLoadedInfo;              // blocks from the mask's north-west corner to the camera (x, z); columns per side; 1 when on
uniform vec4 wmapLight;                   // the game's brightness of full sunlight (SunLightLevels[SunBrightness], 0 unknown); debug view (0 off, 1 albedo only, 2 lit without haze or fog); unused x 2
uniform vec4 wmapCover;                   // the pass (0 one pass, 1 depth, 2 colour, 3 cover; FarFrame.Pass); where the game fades its blocks (blocks, 0 unknown); blocks up to the clouds' underside (0 or less: none); unused
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;                  // camera at the origin

// Forest crowns as a height field over the canopy (0.5.3-rc.10): a smooth value noise of crowns about Crown blocks across,
// relief blocks high, on the world grid (world: blocks; the lattice is wrapped every 4096 crowns to keep float precision).
// Returns the normal tilted by its slope where a crown spans more than about two pixels (footprint: blocks per pixel), by
// share (how much forest). (rc.10 also darkened forest by up to a tenth for the gaps too small to draw; the canopy's walls
// do that now, wmapCanopyWalls.)
float wmapCrownHash(vec2 i) { return fract(sin(dot(mod(i, 4096.0), vec2(127.1, 311.7))) * 43758.5453); }
vec3 wmapCrowns(vec3 n, vec2 world, float share, float footprint)
{
    const float Crown = 5.0, Relief = 2.5;
    float pixels = Crown / max(footprint, 1e-3);
    float drawn = smoothstep(2.0, 5.0, pixels);
    if (drawn <= 0.0) return n;
    vec2 p = world / Crown, i = floor(p), f = p - i;
    float a = wmapCrownHash(i), b = wmapCrownHash(i + vec2(1.0, 0.0)), c = wmapCrownHash(i + vec2(0.0, 1.0)), d = wmapCrownHash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f), du = 6.0 * f * (1.0 - f);
    vec2 slope = Relief / Crown * du * vec2((b - a) + (a - b - c + d) * u.y, (c - a) + (a - b - c + d) * u.x);
    return normalize(n + vec3(-slope.x, 0.0, -slope.y) * share * drawn);
}

// How much of a forest's canopy is seen as walls rather than tops (0.5.3-rc.12, WM-0020). The terraces stand forest as
// two-block cells a block or so up or down from their neighbours (farcellmap.fsh, wmapRoughCanopy), and seen at a slant
// their walls fill most of the view; walls are lit as the game lights a block's sides, about half as bright as its top
// at noon. Past the terraces forest was a smooth canopy lit as tops alone, a quarter brighter than the terraced forest in
// front of it, with a line where they met (Latvia, rc.11 look). Here the same canopy's share of walls: per cell its step
// (Step blocks) against its top (Cell blocks), as seen from the eye (sinView: the sine of the view's angle to the ground).
float wmapCanopyWalls(float sinView)
{
    const float Cell = 2.0, Step = 1.0;
    float cosView = sqrt(max(0.0, 1.0 - sinView * sinView));
    return Step * cosView / max(Step * cosView + Cell * sinView, 1e-4);
}

// A face's light as the game lights its chunks (chunkopaque.vsh, chunkopaque.fsh): its shade from the light's direction,
// with the floor the shadow setting gives, and in shadow where turned less than about 15 degrees to the light.
float wmapFaceShade(vec3 n, vec3 lightDir, float minShade, float shadows)
{
    float nb = max(max(minShade, 0.5 + 0.5 * dot(n, lightDir)), n.y * 0.95);
    float sunLit = smoothstep(0.0, 0.25, dot(n, lightDir));
    return min(nb, 1.0 - 0.5 * clamp(shadows, 0.0, 1.0) * (1.0 - sunLit));
}

// Writes the pixel's colour and glow, and blanks the game's SSAO buffers (far terrain has no ambient occlusion; 0.5.3,
// WM-0042: written out at each of five returns).
void wmapOut(vec4 color, vec4 glow)
{
    outColor = color;
    outGlow = glow;
#if SSAOLEVEL > 0
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
#endif
}

void main()
{
    if (wmapDistance < wmapRange.x || wmapDistance > wmapRange.y) discard;
    if (wmapUV.y < 0.0 || wmapUV.y > 1.0 || (wmapRange.w < 0.5 && (wmapUV.x < 0.0 || wmapUV.x > 1.0))) discard;
    // Nearer than where the game's chunks end, only over chunk columns not loaded yet (0.5.0-pre.3, FarStart.Draws): far
    // terrain fills them while they load, and nowhere else covers the real terrain. From 0.5.3-rc.5 out to where the game
    // starts fading its blocks (0.8 of its view distance; before, 0.75), past which far terrain covers them (below).
    float gameFade = wmapCover.y;
    if (wmapDistance < max(wmapPush.z, gameFade))
    {
        ivec2 column = ivec2(floor((wmapLoadedInfo.xy + wmapPos.xz) / 32.0));
        int side = int(wmapLoadedInfo.z + 0.5);
        if (wmapLoadedInfo.w < 0.5 || column.x < 0 || column.y < 0 || column.x >= side || column.y >= side || texelFetch(wmapLoaded, column, 0).r > 0.5) discard;
    }
    // Behind the planet's bulge (0.4.11): past the sea's horizon (at t, where the line from the eye grazes the curved sea),
    // ground below that line is hidden by the sea in front. On screen it would lie below the far sea's near edge, where
    // only the game's chunks cover it, and not before they load.
    // The ground is kept HorizonSkirt radians below that line (play-test fix, 25 October): the drawn sea's top edge lay
    // a pixel or two under it, so a row of sky ran beneath distant coasts and a rising moon showed through it as if under
    // the waves (seen in Portugal). Ground there is farther than the sea, which still hides it wherever it is drawn. At
    // most half way down to the far sea's near edge, so a low eye never sees it through the game's own water.
    const float HorizonSkirt = 0.004;
    float eyeHeight = wmapOrigin.z - wmapOrigin.w;
    if (wmapCurve.x > 0.0 && eyeHeight > 0.0)
    {
        float t = sqrt(wmapCurve.y * wmapCurve.y + eyeHeight / wmapCurve.x), over = t - wmapCurve.y;
        float grazing = -(wmapCurve.x * over * over + eyeHeight) / t;
        float nearEdge = -eyeHeight / max(wmapPush.z, 1.0);
        float skirt = clamp(0.5 * (grazing - nearEdge), 0.0, HorizonSkirt);
        if (wmapDistance > t && wmapPos.y + 0.5 < (grazing - skirt) * wmapDistance) discard;
    }

    // In front of the game's blocks where it fades them (0.5.3-rc.5 and rc.6, WM-0022): chunkopaque.vsh blends every block from
    // 0.8 to 0.85 of the view distance into the sky colour by day and then drops it, so past the real ground a pale ring stood,
    // pale trees on it, against the darker far land. The cover pass (FarTerrain: after the depth and colour passes) writes, where
    // far land lies past that point and below the clouds, the depth of the point at the fade's start along the ray: every game
    // block from there on lies behind it, every nearer one in front. Below the clouds no cloud can lie between the eye and that
    // land, so none is hidden; far land up in the clouds keeps its own depth.
    if (wmapCover.x > 2.5)
    {
        if (gameFade <= 0.0 || wmapDistance <= gameFade || wmapCover.z <= 0.0 || wmapPos.y >= wmapCover.z) discard;
        vec4 clip = projectionMatrix * viewMatrix * vec4(wmapPos * (gameFade / wmapDistance), 1.0);
        gl_FragDepth = ((gl_DepthRange.far - gl_DepthRange.near) * clip.z / clip.w + gl_DepthRange.near + gl_DepthRange.far) * 0.5;
        wmapOut(vec4(0.0), vec4(0.0));
        return;
    }
    gl_FragDepth = gl_FragCoord.z;
    // The depth pass: nothing more (its colour is masked).
    if (wmapCover.x > 0.5 && wmapCover.x < 1.5)
    {
        wmapOut(vec4(0.0), vec4(0.0));
        return;
    }

    vec3 n = normalize(wmapNormal);
    vec3 albedo;
    bool water;
    // Forest past the terraces: the share of its canopy seen as walls, and the walls' normal (toward the eye).
    float wallShare = 0.0;
    vec3 wallNormal = vec3(0.0, 1.0, 0.0);
    // Blocks per pixel here, for the waves (outside any branch: derivatives need every pixel of the quad).
    float footprint = length(fwidth(wmapPos.xz));
    // The relief between the mesh's vertices (0.5.3-rc.4): in the near band, the band's four-block heights and the forest's
    // canopy shade every pixel, so slopes and forest edges finer than a mesh cell (8 to 32 blocks there) show.
    // Steep faces turn to rock by this normal too (below). The sea's own normal replaces it.
    if (wmapMode > 0.5)
    {
        // A terrace's water is its cell's, and only its top: the walls down to a lower shore are the shore's colour. Its
        // cell brings the tint and how deep it is.
        water = wmapFlat.a > 0.75 && n.y > 0.5;
        albedo = wmapFlat.a > 0.75 ? wmapWaterAlbedo((wmapFlat.a - 0.8) / 0.2, wmapFlat.rgb) : wmapFlat.rgb;
    }
    else
    {
        vec4 chart = texture(wmapChart, wmapUV);
        vec4 band, bandBiome, bandTile;
        float w = wmapBandLookup(wmapLocal, band, bandBiome, bandTile);
        vec4 detail = wmapDetailNormalFrom(bandTile, w, footprint);
        if (detail.w > 0.0) n = normalize(mix(n, detail.xyz, detail.w));
        vec2 climateUV;
        float climateReady = wmapClimateTile(wmapLocal, climateUV);
        // The four-block recordings over the prediction, with their colour (the mid-distance view).
        float recorded;
        vec4 recordedColor;
        // One lookup of the four-block recording for its height and its canopy (0.5.3, WM-0032).
        vec3 recTexel = wmapRecordedTexel(wmapLocal, 0);
        float recFade = wmapRecordedFade(wmapLocal);
        vec3 surface = wmapWithRecordedFrom(recTexel, recFade, wmapSurface(chart, band, w), recorded, recordedColor);
        float coast = surface.x, height = surface.y, ice = surface.z;
        // Rivers narrower than a chart texel come from the chart; the near band resolves wider ones as water.
        float river = wmapDecode(chart.a) * 12.0 * wmapChartInfo.x;
        vec4 biome = wmapBiomeFrom(climateReady, climateUV, w, bandBiome);
        // Where the recorder saw the treetops (0.5.3-rc.4), the forest is what it saw: its share of the ground from the mean
        // canopy around (cover x height), for the leaf colour and how much recorded ground shows; the prediction elsewhere.
        vec2 canopyRec = wmapRecordedCanopyFrom(recTexel, recFade, 1);
        float recForest = smoothstep(1.0, 6.0, canopyRec.x);
        vec4 landBiome = vec4(mix(biome.r, recForest, canopyRec.y), biome.gba);
        float forestShare = mix(biome.r * biome.a, recForest, canopyRec.y);
        // Water only where the drawn surface lies at the sea: a mesh cell (8 to 64 blocks) spanning from the sea up a
        // cliff is a ramp, and its part above the water is shore even where the 4-block coast distance still says sea.
        // Colouring it as sea drew the water climbing up distant coasts.
        // Water above the sea (a lake) is stored at its surface (Core/Semantic.cs) and drawn there.
        // Measured before the planet's curve lowered it (farterrain.vsh).
        float curveOver = max(0.0, wmapDistance - wmapCurve.y);
        float above = wmapPos.y + wmapOrigin.z - wmapOrigin.w + wmapCurve.x * curveOver * curveOver;
        // Up to the water texels' own level, not the filtered height (wmapWaterLevel); the recordings' where they lie.
        if (coast < 0.0)
        {
            float level = wmapWaterLevel(wmapChart, wmapUV);
            if (w > 0.0) level = mix(level, wmapBandWaterLevelFrom(bandTile), w);
            water = above < mix(level, max(height, 0.0), recorded) + 0.75;
        }
        else water = false;
        if (water) albedo = wmapWaterAlbedo(smoothstep(0.0, 55.0, -height), wmapWaterTintAt(biome));
        else
        {
            // Shore drawn above the water where the coast distance says sea: as low land at the height drawn.
            if (coast < 0.0) height = max(0.0, above);
            albedo = wmapLandAlbedo(height, landBiome, wmapSeasonFrom(climateReady, climateUV));
            // Rivers narrower than a texel stay visible as thin water lines while they are a few pixels wide.
            // smoothstep needs edge0 < edge1: fwidth is 0 where the distance does not change across the pixel.
            float riverWidth = max(fwidth(river), 1e-4);
            // Only where the drawn surface lies on the chart's own ground (its finest level) there (0.5.0-pre.10): past the
            // near band a mesh cell of 16 to 64 blocks smooths a narrow valley into a slope, and the line was painted on it,
            // tens of blocks up the hillside above its river, as water-coloured specks in coastal hills.
            float riverGround = max(0.0, wmapDecode(textureLod(wmapChart, wmapUV, 0.0).g) * wmapChartInfo.w);
            float onGround = 1.0 - smoothstep(2.0, 6.0, above - riverGround);
            albedo = mix(albedo, wmapWater.a > 0.5 ? wmapWater.rgb : vec3(0.22, 0.42, 0.60), (1.0 - smoothstep(0.0, riverWidth * 1.5, river)) * 0.8 * (1.0 - w) * onGround);
            // Steep faces are bare rock, as cliffs are in the game.
            albedo = mix(albedo, vec3(0.47, 0.44, 0.40), 1.0 - smoothstep(0.45, 0.75, n.y));
            // Recorded ground in the game's colours, under the forest canopy where there is forest (recorded, or predicted).
            if (recorded > 0.0 && coast >= 0.0) albedo = mix(albedo, recordedColor.rgb, recorded * recordedColor.a * (1.0 - forestShare));
            // Crowns on forest (0.5.3-rc.10, WM-0020): past the terraces a forest is a smooth plateau of canopy, which reads as
            // flat olive land. Its crowns (about five blocks across, a value noise on the world grid, so they stay put) tilt
            // the light where they are a few pixels wide or more; where they are smaller, the shade between them darkens it.
            float crownShare = smoothstep(0.35, 0.75, forestShare) * (1.0 - ice);
            if (crownShare > 0.0)
            {
                // Seen at a slant, the canopy is mostly its walls, lit as the terraces' walls are (below; 0.5.3-rc.12).
                vec3 toEye = normalize(-wmapPos);
                vec2 across = toEye.xz;
                if (dot(across, across) > 1e-8) wallNormal = vec3(normalize(across).x, 0.0, normalize(across).y);
                wallShare = crownShare * wmapCanopyWalls(clamp(dot(toEye, n), 0.0, 1.0));
                n = wmapCrowns(n, wmapOriginWorld + wmapLocal, crownShare, footprint);
            }
        }
        albedo = mix(albedo, vec3(0.94, 0.96, 0.98), ice);
    }
    // The sea is flat; with a water mod's waves (Sea), its normal follows them, fading out with distance.
    float waveRough = 0.0;
    if (water) n = seaWaveCount > 0 ? wmapWaveNormal(wmapPos.xz, footprint, waveRough) : vec3(0.0, 1.0, 0.0);

    // Debug view: the colour alone, unlit and without haze or fog, to compare with the game's own ground (.wmap fardebug).
    if (wmapLight.y > 0.5 && wmapLight.y < 1.5)
    {
        wmapOut(vec4(albedo, 1.0), vec4(0.0, 0.0, 0.0, 1.0));
        return;
    }

    // Lit as the game lights its own chunks under the open sky: its ambient colour, which carries the day and night
    // (rgbaAmbientIn in chunkopaque.vsh), shaded by the face's normal as chunkopaque.vsh does. The sun colour is not
    // used: in polar twilight it is a deep red, and far snow glowed red-brown beyond grey-blue real snow. At night no darker
    // than a water mod's night floor (Sea), as its near water is.
    vec3 sunDir = normalize(sunPosition);
    // The sea's shade, as calibrated; land's follows below.
    float nb = water ? max(max(0.45, 0.5 + 0.5 * dot(n, sunDir)), n.y * 0.95) : 1.0;
    float sunLevel = 1.0;
    // The game also darkens what its shadow map has in shadow, to half at most (fogandlight.fsh
    // getBrightnessFromShadowMap, taken as min(shadow, normal) in chunkopaque.fsh); far terrain has no shadow map, and
    // was lit as open sunlit ground at any hour: a coast with the sun behind it stood as bright as the sky at sunset.
    // Land turned less than about 15 degrees to the sun counts as in shadow (with a low sun the relief shades it), so
    // flat ground goes into shadow as the sun sets and slopes facing away from the sun are dark by day. Gone with the
    // sun, and softer under cloud, as the game's shadows are. (0.5.0-pre.6; the sea is left as calibrated.)
    if (!water)
    {
        // Land lit with the numbers the game lights its chunks with (0.5.3-rc.4; before, far land was about 6 % too dark on
        // clear days with shadows on and up to 15 % too bright under cloud or with shadows off):
        // - the face's shade from the game's light (sun or moon), with the floor its shadow setting gives (chunkopaque.vsh);
        // - full sunlight at the brightness its sun-light level has (SunLightLevels[SunBrightness], 0.866 in vanilla: the
        //   vertex light chunkopaque.vsh multiplies the ambient colour by);
        // - shade from the shadow map's strength (shadowIntensity, which the game sets on every program with
        //   fogandlight.fsh, also with shadows off), and its clear-day boost (applyFogAndShadowFromBrightness).
        vec3 lightDir = normalize(lightPosition);
#if SHADOWQUALITY > 0
        float minShade = 0.34 + (1.0 - shadowIntensity) / 8.0;
        float shadows = shadowIntensity;
#else
        float minShade = 0.45;
        float shadows = smoothstep(-0.02, 0.03, sunDir.y) * (1.0 - 0.7 * clamp(horizonFog, 0.0, 1.0));
#endif
        nb = wmapFaceShade(n, lightDir, minShade, shadows);
        // Forest past the terraces: tops and walls in the terraces' mix (wmapCanopyWalls).
        if (wallShare > 0.0) nb = mix(nb, wmapFaceShade(wallNormal, lightDir, minShade, shadows), wallShare);
        nb *= 1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;
        if (wmapLight.x > 0.0) sunLevel = wmapLight.x;
    }
    vec4 color = vec4(albedo * max(clamp(wmapAmbient, 0.0, 1.0) * sunLevel, seaNightFloor) * nb, 1.0);
    // Debug view: lit, without the haze and fog.
    if (wmapLight.y > 1.5)
    {
        wmapOut(vec4(color.rgb, 1.0), vec4(0.0, 0.0, 0.0, 1.0));
        return;
    }

    // Aerial perspective: blend toward the game's sky colour along this ray (terrain below the eye
    // takes the horizon colour), stronger with distance and complete at the far edge. Land starts a little hazed, as
    // the game fades its own land into the sky beyond it (0.8 to 0.85 of the view distance), and thickens more slowly
    // than the sea's (0.5.0-pre.6: it started at 0.3 and rose as the sea's, so far land read as pale as the sky); the
    // sea starts clear, next to the game's water, and keeps its calibrated curve.
    vec4 sky = vec4(1.0), skyGlow = vec4(1.0);
    vec3 ray = normalize(vec3(wmapPos.x, max(wmapPos.y, 0.0), wmapPos.z)) * 250.0;
    getSkyColorAt(ray, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, sky, skyGlow);
    // The sky's sunset glow, as the dome has it (0.5.0-pre.6): far land and sea toward a low sun fade into its warm haze.
    sky.rgb = wmapSunsetGlow(sky.rgb, normalize(ray), sunDir, getFogAmountForSky(ray, normalize(ray), 0.25, horizonFog));
    float f = clamp((wmapDistance - wmapPush.z) / max(1.0, wmapPush.y - wmapPush.z), 0.0, 1.0);
    // (0.5.3-rc.5: land from no haze, as the sea; up to rc.4 from 0.15, to meet the game's blocks fading into the sky, which
    // far terrain now covers.)
    float haze = water ? smoothstep(0.0, 1.0, pow(f, 0.7)) : smoothstep(0.0, 1.0, f);
    // At night the sky colour function returns the fog colour, not the dark sky, so terrain blended toward it
    // glows. The game's own chunks take the sky colour by at most the daylight (chunkopaque.fsh); so does this.
    haze = min(haze, clamp(dayLight, 0.0, 1.0));
    color.rgb = mix(color.rgb, sky.rgb, haze);
    // The sea reflects the sky by Fresnel along the ray its waves reflect, as a water mod's near water does (Sea's
    // seaSkyReflect: 0 without one). The dome as it reaches the screen: at night the sky colour function returns the fog
    // colour with alpha fogAmount over dark space, so rgb x alpha (Sea 0.4.6).
    if (water && (seaSkyReflect > 0.0 || seaGlint > 0.0))
    {
        vec3 eye = normalize(wmapPos);
        float fresnel = 0.02 + 0.98 * pow(1.0 - clamp(dot(-eye, n), 0.0, 1.0), 5.0);
        vec3 bounce = reflect(eye, n);
        if (seaSkyReflect > 0.0)
        {
            vec4 mirrored = vec4(1.0), mirroredGlow = vec4(1.0);
            vec3 bounceSky = normalize(vec3(bounce.x, max(bounce.y, 0.0), bounce.z));
            getSkyColorAt(bounceSky * 250.0, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, mirrored, mirroredGlow);
            mirrored.rgb = wmapSunsetGlow(mirrored.rgb, bounceSky, sunDir, getFogAmountForSky(bounceSky * 250.0, bounceSky, 0.25, horizonFog));
            color.rgb = mix(color.rgb, mirrored.rgb * clamp(mirrored.a, 0.0, 1.0), clamp(seaSkyReflect * fresnel, 0.0, 1.0));
        }
        // The glitter path continues from the near water to the horizon (Sea 0.4.7-pre.7 passes its glint), after the haze as
        // the near water's is, then fogged with it.
        if (seaGlint > 0.0) color.rgb = wmapSeaGlints(color.rgb, bounce, smoothstep(-0.06, -0.02, bounce.y) * (0.55 + 0.45 * fresnel), waveRough);
    }
    color = applyFog(color, fogAmount);

    wmapOut(vec4(color.rgb, 1.0), vec4(0.0, skyGlow.y * haze, 0.0, 1.0));
}
