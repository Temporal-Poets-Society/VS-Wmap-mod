using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Wmap.Client;
using Wmap.Integrations;
using Wmap.Server;

/// <summary>0.4.4: the recorder's water bed follows edits under water. Worldgen's terrain height is the bed of untouched
/// water only; a raised or dug-out bed is found by scanning down (0.4.3 kept the generated bed). Built in the air above a
/// loaded column of the test save, with stone and still water placed and removed in one main-thread step.</summary>
public static class BedChecks
{
    public static void Run(ICoreServerAPI api, WorldSource source, Action<string> done)
    {
        var t = source.Info.Topology();
        int x = (int)Math.Floor(t.StartX) + 40, z = (int)(t.Height / 2) + 1300;
        bool finished = false;
        void End(string text) { if (finished) return; finished = true; done(text); }
        AtlasServer.Land(api, x, z, (_, _, _) =>
        {
            var ba = api.World.BlockAccessor;
            var stone = api.World.GetBlock(new AssetLocation("game:rock-granite"));
            var water = api.World.GetBlock(new AssetLocation("game:water-still-7"));
            if (stone == null || water == null) { End("Water bed: not applicable (no granite or water block)"); return; }
            int y0 = api.WorldManager.MapSizeY - 24, surface = y0 + 8;
            var pos = new BlockPos(x, 0, z);
            void Solid(int y, Block block) { pos.Y = y; ba.SetBlock(block?.BlockId ?? 0, pos); ba.SetBlock(0, pos, BlockLayersAccess.Fluid); }
            void Water(int y) { pos.Y = y; ba.SetBlock(0, pos); ba.SetBlock(water.BlockId, pos, BlockLayersAccess.Fluid); }
            var worldgen = new ushort[] { (ushort)y0 };
            try
            {
                Solid(y0 - 1, stone); Solid(y0, stone);
                for (int y = y0 + 1; y <= surface; y++) Water(y);
                int untouched = ObservedTerrain.Bed(ba, worldgen, 0, x, surface, z);
                Solid(y0 + 1, stone);
                int raised = ObservedTerrain.Bed(ba, worldgen, 0, x, surface, z);
                Water(y0 + 1); Water(y0);
                int dug = ObservedTerrain.Bed(ba, worldgen, 0, x, surface, z);
                // A platform built in the water, the generated bed and water still under it.
                Solid(y0, stone); Water(y0 + 1); Water(y0 + 2); Solid(y0 + 3, stone);
                int platform = ObservedTerrain.Bed(ba, worldgen, 0, x, surface, z);
                // 0.4.5: kelp from the bed to the surface, its tip above the water, is water over the bed (0.4.4 went down
                // the plant to the bed and recorded land there: dark islands on the sea).
                string kelpError = null;
                var kelp = api.World.GetBlock(new AssetLocation("game:aquatic-kelp-section")) ?? api.World.GetBlock(new AssetLocation("game:seaweed-section"));
                if (kelp != null)
                {
                    Solid(y0, stone);
                    for (int y = y0 + 1; y <= surface; y++) { Water(y); ba.SetBlock(kelp.BlockId, pos); }
                    Solid(surface + 1, kelp);
                    foreach (int top in new[] { surface, surface + 1 })
                    {
                        var s = ObservedTerrain.Classify(ba, worldgen, 0, new BlockPos(x, top, z), out _);
                        if (s.Kind != 2 || s.Height != y0 || s.Depth != surface - y0)
                            kelpError ??= $"kelp from y={top}: kind {s.Kind} height {s.Height} depth {s.Depth} (want water, bed {y0}, depth {surface - y0})";
                    }
                }
                if (kelpError != null) End("FAIL water bed: " + kelpError);
                else if (untouched != y0 || raised != y0 + 1 || dug != y0 - 1 || platform != y0 + 3)
                    End($"FAIL water bed: untouched {untouched} (want {y0}), raised {raised} (want {y0 + 1}), dug out {dug} (want {y0 - 1}), platform {platform} (want {y0 + 3})");
                else End($"Water bed: PASS (generated bed {y0} while untouched; raised to {raised}, dug out to {dug} and a platform over water at {platform} found by scanning; {(kelp == null ? "no kelp block" : "kelp to the surface is water")})");
            }
            catch (Exception e) { End("FAIL water bed: " + e.Message); }
            finally { for (int y = y0 - 1; y <= surface + 1; y++) Solid(y, null); }
        });
        api.Event.RegisterCallback(_ => End("FAIL water bed: the column never loaded"), 60000);
    }
}
