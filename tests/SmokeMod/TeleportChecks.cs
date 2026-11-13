using System;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Wmap.Core;
using Wmap.Integrations;
using Wmap.Server;

/// <summary>Right-click teleport on the server: a map position across the seam becomes a column on the ring's own
/// lap, which is loaded (generated here) and landed on just above its top; a loaded column lands at once.</summary>
public static class TeleportChecks
{
    public static void Run(ICoreServerAPI api, WorldSource source, Action<string> done)
    {
        var t = source.Info.Topology();
        bool finished = false;
        void End(string text) { if (finished) return; finished = true; done(text); }
        // East of the ring's end: on a wrapping world this is the same column as just east of its start.
        double requestX = t.Wraps ? t.EndX + 7.3 : t.StartX + 7.3, requestZ = t.Height / 2 + 1234.6;
        if (Teleport.Column(t, requestX, requestZ, api.WorldManager.MapSizeX, api.WorldManager.MapSizeZ) is not { } column) { End("FAIL teleport: no column for an in-world position"); return; }
        if (column.X != (int)Math.Floor(t.StartX) + 7) { End($"FAIL teleport: x {requestX} became column {column.X}, not the ring's own lap"); return; }
        string first = null;
        string Check(double x, double y, double z)
        {
            var ba = api.World.BlockAccessor;
            if (x != column.X + 0.5 || z != column.Z + 0.5) return $"landed at x {x} z {z}, not the column's centre";
            var top = new BlockPos((int)x, (int)y - 1, (int)z);
            bool solidOrFluid = ba.GetBlock(top).Id != 0 || ba.GetBlock(top, Vintagestory.API.Common.BlockLayersAccess.Fluid).Id != 0;
            // The rain height passes plants such as tall grass: the arrival block need only be passable.
            var at = ba.GetBlock(new BlockPos((int)x, (int)y, (int)z));
            bool clear = at.Id == 0 || at.CollisionBoxes == null || at.CollisionBoxes.Length == 0;
            if (!solidOrFluid || !clear) return $"landed at y {y}: below {ba.GetBlock(top).Code}, at {ba.GetBlock(new BlockPos((int)x, (int)y, (int)z)).Code}";
            return null;
        }
        AtlasServer.Land(api, column.X, column.Z, (x, y, z) =>
        {
            first = Check(x, y, z);
            if (first != null) { End("FAIL teleport (unloaded column): " + first); return; }
            int loadedY = (int)y;
            AtlasServer.Land(api, column.X, column.Z, (x2, y2, z2) =>
            {
                string again = Check(x2, y2, z2);
                if (again != null || (int)y2 != loadedY) { End($"FAIL teleport (loaded column): {again ?? $"y {y2} != {loadedY}"}"); return; }
                End($"Teleport: PASS (map x {requestX:0.0}{(t.Wraps ? " across the seam" : "")} lands on column {column.X}, {column.Z}; generated then loaded, y {loadedY} above {api.World.BlockAccessor.GetBlock(new BlockPos(column.X, loadedY - 1, column.Z)).Code})");
            });
        });
        api.Event.RegisterCallback(_ => End("FAIL teleport: the column never loaded"), 60000);
    }
}
