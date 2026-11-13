using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Wmap.Client;
using Wmap.Core;
using Wmap.Integrations;

// Deliberately generates chunks in the isolated test save, then compares their actual
// surfaces with the pre-chunk Wmap sampler. Production Wmap never loads these chunks.
public static class GeneratedPolarChecks
{
    public static void Run(ICoreServerAPI api, WorldSource source, Action<string> done)
    {
        object cylinder = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "CylinderWorld.CylinderWorldCore");
        object p = WorldSource.Member(cylinder, "Params");
        object earth = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthMapSystem");
        if (p == null || earth == null) { done("Generated Earth polar and seam checks: not applicable"); return; }
        bool polar = WorldSource.Member(p, "EarthPolarIce") is true;
        object geometry = WorldSource.Member(WorldSource.Member(earth, "Model"), "Geometry");
        int N(string name) => Convert.ToInt32(WorldSource.Member(p, name));
        (string name, int x, int z) Geo(string name, double lat, double lon)
        {
            object[] args = { lat, lon, 0d, 0d };
            geometry.GetType().GetMethod("FromGeo").Invoke(geometry, args);
            return (name, (int)(double)args[2], (int)(double)args[3]);
        }
        (string name, int x, int z)[] polarPlaces = {
            ("north wall", N("WorldWidth") / 2, 10),
            ("north border ice", N("WorldWidth") / 2, N("WallDepth") + 500),
            Geo("Arctic beyond old strip", 80, -135), Geo("Arctic Atlantic margin", 76, 10),
            Geo("Greenland", 75, -45), Geo("Antarctic plateau", -85, 20),
            Geo("Antarctic peninsula", -70, -65),
            ("south border", N("WorldWidth") / 2, N("WorldLength") - N("WallDepth") - 500),
            ("south wall", N("WorldWidth") / 2, N("WorldLength") - 10)
        };
        // Cylinder's seam band over Earth's Pacific gutter, in either polar mode: its identical part next to the jump
        // line, its inner part, and (layout v10) the ramp just past the band's half-width H.
        int half = N("SeamHalfWidth"), equator = N("WorldLength") / 2;
        (string name, int x, int z)[] seamPlaces = {
            ("seam identical part", N("JumpInset") + 64, equator + 40),
            ("seam inner band", N("WorldWidth") - (half - 100), equator - 700),
            ("seam past H", half + 96, equator + 1300)
        };
        // A forest (central Germany, Earth's forest potential 249): the recorder finds the ground under the trees. Only the
        // recorder is compared there; the prediction checks are for the polar and seam places.
        const string Forest = "forest (recorder only)";
        var places = (polar ? polarPlaces : Array.Empty<(string name, int x, int z)>()).Concat(seamPlaces).Append(Geo(Forest, 50.6, 10.6)).ToArray();
        int index = 0, compared = 0, distantIce = 0, recorded = 0, recordedSnow = 0, treeColumns = 0;
        bool finished = false;
        var details = new List<string>();
        void End(string text) { if (finished) return; finished = true; done(text); }
        void Next()
        {
            if (finished) return;
            if (index == places.Length)
            {
                if (polar && distantIce == 0) { End("FAIL generated polar checks did not cover Arctic ice outside the old strip"); return; }
                if (polar && recordedSnow == 0) { End("FAIL generated polar checks did not cover snow on sea ice or the Wall"); return; }
                if (treeColumns < 5) { End($"FAIL generated terrain checks found only {treeColumns} tree columns in the forest"); return; }
                End($"Generated {(polar ? "polar and seam" : "seam")} terrain: PASS ({compared} columns, {places.Length} locations, including {distantIce} Arctic ice columns beyond the old strip; "
                    + $"the recorder agrees on {recorded} sea-ice and Wall columns, {recordedSnow} of them under snow, and finds the ground under {treeColumns} tree columns)\n" + string.Join("\n", details));
                return;
            }
            var place = places[index];
            api.WorldManager.LoadChunkColumnPriority(place.x / 32, place.z / 32, new ChunkLoadOptions {
                OnLoaded = () => api.Event.EnqueueMainThreadTask(Analyze, "wmap-generated-polar-check")
            });
        }
        void Analyze()
        {
            if (finished) return;
            try
            {
                var place = places[index];
                var ba = api.World.BlockAccessor;
                int errors = 0, maxHeightError = 0, ice = 0, maxSnow = 0;
                string firstError = null;
                for (int dz = 2; dz < 32; dz += 4)
                for (int dx = 2; dx < 32; dx += 4)
                {
                    int x = place.x / 32 * 32 + dx, z = place.z / 32 * 32 + dz;
                    var predicted = source.Sample(x, z);
                    int actualY = 0; byte kind = 0; string code = "air";
                    for (int y = api.WorldManager.MapSizeY - 2; y > 0; y--)
                    {
                        var pos = new BlockPos(x, y, z);
                        var block = ba.GetBlock(pos, BlockLayersAccess.Solid);
                        var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
                        // Cylinder 0.11 sea ice sits in the fluid layer as a solid, non-liquid block.
                        if (block.Id == 0 && fluid.Id != 0 && !fluid.IsLiquid() && fluid.BlockMaterial == EnumBlockMaterial.Ice) { actualY = y; kind = 3; code = fluid.Code.ToString(); break; }
                        if (block.Id == 0 && fluid.IsLiquid()) { actualY = y; kind = 2; code = fluid.Code.ToString(); break; }
                        // Earth's permanent snow replaces the top terrain blocks; include
                        // it in the mapped surface, while ignoring loose snow-layer cover.
                        if (block.Id == 0 || block.Code.Path.StartsWith("snowlayer") || block.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood) continue;
                        actualY = y; code = block.Code.ToString();
                        kind = (byte)(code.Contains("wallice") ? 4 : block.BlockMaterial is EnumBlockMaterial.Ice or EnumBlockMaterial.Snow || code.Contains("glacier") ? 3 : 1);
                        break;
                    }
                    int expectedY = predicted.Height + (predicted.Kind == 2 ? predicted.Depth : 0);
                    int baseY = actualY;
                    if (kind == 3 && predicted.Kind == 3)
                        while (baseY > 1 && ba.GetBlock(new BlockPos(x, baseY, z), BlockLayersAccess.Solid).BlockMaterial == EnumBlockMaterial.Snow) baseY--;
                    maxSnow = Math.Max(maxSnow, actualY - baseY);
                    // Earth replaces its top blocks with permanent snow, and vanilla
                    // also deposits snow above ice. Compare the predicted ice surface
                    // with that actual continuous ice/snow envelope, not tree/cover tops.
                    int heightError = expectedY < baseY ? baseY - expectedY : Math.Max(0, expectedY - actualY);
                    maxHeightError = Math.Max(maxHeightError, heightError);
                    if (place.name == Forest)
                    {
                        // The recorder from the rain height (a treetop where trees stand) must give the ground the
                        // oracle finds below leaves, logs and plants (0.4.3 test builds recorded the canopy).
                        var forestChunk = ba.GetMapChunk(x / 32, z / 32);
                        int column = z % 32 * 32 + x % 32;
                        var top = new BlockPos(x, forestChunk.RainHeightMap[column], z);
                        if (!ObservedTerrain.IsGrowth(ba.GetBlock(top))) { compared++; continue; }
                        var ground = ObservedTerrain.Classify(ba, forestChunk.WorldGenTerrainHeightMap, column, top, out _);
                        treeColumns++;
                        if (Math.Abs(ground.Height + ground.Depth - actualY) > 1)
                        {
                            errors++;
                            firstError ??= $"x={x} z={z}: recorder ground {ground.Height + ground.Depth} under {ba.GetBlock(top).Code} at y={top.Y}; generated ground {actualY} ({code})";
                        }
                        compared++;
                        continue;
                    }
                    if (predicted.Kind != kind || heightError > 1)
                    {
                        errors++;
                        firstError ??= $"x={x} z={z}: predicted kind {predicted.Kind} y={expectedY}; generated {code} kind {kind} y={actualY}";
                    }
                    // The client's recorder over the same column, from its rain height: snow drifted over sea ice or the
                    // Wall is still ice (0.4.2 recorded it as land: green islands on the polar sea).
                    if (Semantic.IsSeaIce(predicted.Kind, Semantic.RelativeHeight(predicted.Height, predicted.Depth, predicted.Kind, api.World.SeaLevel)) || predicted.Kind == 4)
                    {
                        var mapChunk = ba.GetMapChunk(x / 32, z / 32);
                        int column = z % 32 * 32 + x % 32;
                        var top = new BlockPos(x, mapChunk.RainHeightMap[column], z);
                        var observed = ObservedTerrain.Classify(ba, mapChunk.WorldGenTerrainHeightMap, column, top, out _);
                        recorded++;
                        if (ba.GetBlock(top).BlockMaterial == EnumBlockMaterial.Snow) recordedSnow++;
                        if (observed.Kind != predicted.Kind)
                        {
                            errors++;
                            firstError ??= $"x={x} z={z}: predicted kind {predicted.Kind}; recorder kind {observed.Kind} at y={top.Y} under {ba.GetBlock(top).Code}";
                        }
                    }
                    if (kind == 3) ice++;
                    if (kind == 3 && place.name == "Arctic beyond old strip" && z > N("PolarZoneDepth")) distantIce++;
                    compared++;
                }
                details.Add($"  {place.name}: {ice}/64 ice columns; max terrain-height error {maxHeightError}; snow cover up to {maxSnow}; mismatches {errors}" + (firstError == null ? "" : "; " + firstError));
                if (errors > 0) { End("FAIL generated polar terrain\n" + string.Join("\n", details)); return; }
                index++;
                api.Event.RegisterCallback(_ => Next(), 50);
            }
            catch (Exception e) { End("FAIL generated polar terrain: " + e); }
        }
        api.Event.RegisterCallback(_ => End("FAIL generated polar terrain timed out"), 180000);
        Next();
    }
}
