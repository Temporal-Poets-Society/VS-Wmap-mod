using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Records the terrain the world holds around the player, for the atlas to draw over its prediction. Every chunk gets
/// four-block samples (as before 0.4.3); a 4 x 4-block cell gets every block only where the world differs from the
/// prediction (story locations, edits, prediction errors) and next to a shore it misses (WM-D33), or where the game
/// reports a block changed. Chunks are
/// recorded when they load or change and revisited every few minutes, not in an endless loop; heights skip trees and
/// plants, and a sample whose colour alone changed is not a change (WM-D29).
/// With the mid-distance view (WM-P06 prototype, <see cref="WmapConfig.MidDistanceRecorded"/>) it records every loaded
/// chunk out to the view distance, not only <see cref="WmapConfig.ObservedRadiusChunks"/>, and keeps the game's colour and
/// the block light of each sample (<see cref="RecordedColor"/>). The chunks around the player are scanned a slice at a time
/// within the frame budget, so a large radius costs no more per frame.
/// </summary>
public sealed class ObservedTerrain : IDisposable
{
    readonly ICoreClientAPI api;
    readonly TileClient tiles;
    readonly WmapConfig config;
    public long Samples, DetailCells, Chunks;
    public delegate bool Prediction(double x, double z, out double height, out bool water);
    /// <summary>The prediction around the player (far terrain's near band), when there is one.</summary>
    public Prediction Predict;
    readonly Dictionary<(int X, int Z), long> recordedAt = new();
    readonly ConcurrentDictionary<(int X, int Z), byte> changedChunks = new();
    readonly ConcurrentDictionary<(int X, int Z, int Cell), byte> changedCells = new();
    (int X, int Z)? current;
    int cursor, detailCursor, predicted;
    readonly bool[] flagged = new bool[64], shore = new bool[64];
    readonly List<int> detail = new();
    (int X, int Z)[] offsets;
    int offsetsFor = -1;
    // The scan over the chunks around the player resumes here; it starts again when the player enters another chunk or a
    // change is reported, and rests a second after finding nothing.
    int scan, changes, scannedChanges = -1;
    (int X, int Z) scanCentre = (int.MinValue, int.MinValue);
    long restUntil;
    /// <summary>A recorded chunk is recorded again after this long, and after a reported change no sooner than this.</summary>
    public const long RevisitMs = 300_000, ChangedAfterMs = 5000;
    /// <summary>A chunk whose recordings were still loading is tried again after this long.</summary>
    public const long RetryMs = 1000;
    readonly Dictionary<(int X, int Z), long> retryAt = new();
    bool incomplete;
    long pruneAt;
    readonly BlockChangedDelegate onBlock;
    readonly ChunkDirtyDelegate onChunk;

    public ObservedTerrain(ICoreClientAPI api, TileClient tiles, WmapConfig config)
    {
        this.api = api; this.tiles = tiles; this.config = config;
        onBlock = (pos, _) =>
        {
            if (pos == null) return;
            changedChunks[(pos.X >> 5, pos.Z >> 5)] = 0;
            changedCells[(pos.X >> 5, pos.Z >> 5, ((pos.Z & 31) >> 2) * 8 + ((pos.X & 31) >> 2))] = 0;
            System.Threading.Interlocked.Increment(ref changes);
        };
        onChunk = (coord, _, _) => { if (coord != null) { changedChunks[(coord.X, coord.Z)] = 0; System.Threading.Interlocked.Increment(ref changes); } };
        if (api?.Event != null) { api.Event.BlockChanged += onBlock; api.Event.ChunkDirty += onChunk; }
    }

    public void Dispose()
    {
        if (api?.Event != null) { api.Event.BlockChanged -= onBlock; api.Event.ChunkDirty -= onChunk; }
    }

    public void Tick()
    {
        var pos = api.World.Player?.Entity?.Pos;
        if (pos == null || pos.Dimension != 0 || !config.RecordObservedTerrain) return;
        timer.Restart();
        var ba = api.World.BlockAccessor;
        long now = Environment.TickCount64;
        int budget = 256;
        while (budget > 0 && timer.Elapsed.TotalMilliseconds < 1)
        {
            if (current == null)
            {
                current = NextChunk((int)pos.X >> 5, (int)pos.Z >> 5, now, timer);
                if (current == null) break;
                cursor = 0; detailCursor = 0; predicted = 0; incomplete = false; detail.Clear(); Array.Clear(flagged); Array.Clear(shore);
            }
            var (cx, cz) = current.Value;
            var map = ba.GetMapChunk(cx, cz);
            if (map?.RainHeightMap == null) { current = null; continue; }
            if (cursor < 64)
            {
                // Four-block samples at the cells' centres; each says whether its cell differs from the prediction.
                int cell = cursor++, lx = (cell % 8) * 4 + 2, lz = (cell / 8) * 4 + 2;
                budget--;
                if (Record(ba, map, cx, cz, lx, lz, false) is not { } s) continue;
                if (Predict != null && Predict(cx * 32 + lx + 0.5, cz * 32 + lz + 0.5, out double height, out bool water))
                {
                    predicted++;
                    if (RecordingRules.Differs(s, tiles.Info.SeaLevel, height, water)) flagged[cell] = true;
                    if (RecordingRules.Shore(s, tiles.Info.SeaLevel, water)) shore[cell] = true;
                }
                continue;
            }
            if (cursor == 64)
            {
                cursor++;
                budget -= 16;
                // Without a prediction here: cells whose top differs from the generated terrain by more than plants,
                // trees or water (later structures, edits).
                if (predicted == 0) FlagChangedSinceWorldgen(ba, map, cx, cz);
                for (int cell = 0; cell < 64; cell++)
                {
                    if (changedCells.ContainsKey((cx, cz, cell))) flagged[cell] = true;
                    if (flagged[cell]) continue;
                    // A cell recorded block by block before is recorded again: the change it showed may be gone. While that
                    // recording is still loading, the cell is recorded block by block anyway (its samples wait for it).
                    if (tiles.HasFineRecording(cx * 32 + (cell % 8) * 4, cz * 32 + (cell / 8) * 4) != false) flagged[cell] = true;
                }
                FlagShores(cx, cz);
                for (int cell = 0; cell < 64; cell++) if (flagged[cell]) detail.Add(cell);
                // Taken now; a sample not kept below reports them again (Defer).
                for (int cell = 0; cell < 64; cell++) changedCells.TryRemove((cx, cz, cell), out _);
                continue;
            }
            if (detailCursor < detail.Count * 16)
            {
                int cell = detail[detailCursor / 16], k = detailCursor % 16;
                detailCursor++; budget--;
                Record(ba, map, cx, cz, (cell % 8) * 4 + k % 4, (cell / 8) * 4 + k / 4, true);
                continue;
            }
            if (incomplete) Defer(current.Value, now);
            else
            {
                recordedAt[(cx, cz)] = now; retryAt.Remove((cx, cz));
                Chunks++; DetailCells += detail.Count;
            }
            current = null;
        }
        int side = 2 * Radius() + 1;
        if ((recordedAt.Count > Math.Max(4096, 2 * side * side) || changedChunks.Count > 1024 || changedCells.Count > 8192) && now >= pruneAt)
        {
            pruneAt = now + 10_000;
            Prune((int)pos.X >> 5, (int)pos.Z >> 5);
        }
    }

    /// <summary>The neighbours of a cell on a shore (<see cref="RecordingRules.Shore"/>) are recorded block by block too.
    /// Across the chunk's edge the next chunk records them, as cells reported changed, while they have no recording of
    /// their own; so two chunks sharing a shore do not ask each other again and again.</summary>
    void FlagShores(int cx, int cz)
    {
        for (int cell = 0; cell < 64; cell++)
        {
            if (!shore[cell]) continue;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cell % 8 + dx, nz = cell / 8 + dz;
                if (nx >= 0 && nx < 8 && nz >= 0 && nz < 8) { flagged[nz * 8 + nx] = true; continue; }
                var chunk = (X: cx + (nx < 0 ? -1 : nx > 7 ? 1 : 0), Z: cz + (nz < 0 ? -1 : nz > 7 ? 1 : 0));
                int lx = (nx + 8) % 8, lz = (nz + 8) % 8;
                if (chunk.X < 0 || chunk.Z < 0 || tiles.HasFineRecording(chunk.X * 32 + lx * 4, chunk.Z * 32 + lz * 4) != false) continue;
                changedCells[(chunk.X, chunk.Z, lz * 8 + lx)] = 0;
                changedChunks[chunk] = 0;
            }
        }
    }

    /// <summary>A sample was not kept (a recording it belongs in was still loading; <see cref="TileClient.Observe"/> asked
    /// for it): the chunk is recorded again soon, with the cells it recorded block by block. What was kept stays; taking
    /// a sample again changes nothing where it was kept.</summary>
    void Defer((int X, int Z) chunk, long now)
    {
        retryAt[chunk] = now + RetryMs;
        changedChunks[chunk] = 0;
        foreach (int cell in detail) changedCells[(chunk.X, chunk.Z, cell)] = 0;
    }

    /// <summary>Chunks recorded around the player: <see cref="WmapConfig.ObservedRadiusChunks"/>, or with the mid-distance
    /// view every loaded chunk out to the view distance (at most 48 chunks, the game's 1536 blocks).</summary>
    int Radius()
    {
        int radius = config.ObservedRadiusChunks;
        if (!config.MidDistanceRecorded) return radius;
        var data = api.World.Player?.WorldData;
        int view = data == null ? 0 : data.LastApprovedViewDistance > 0 ? Math.Min(data.LastApprovedViewDistance, data.DesiredViewDistance) : data.DesiredViewDistance;
        return Math.Clamp((view + 31) / 32, radius, 48);
    }

    /// <summary>The nearest chunk around the player that changed, was never recorded, or is due again. The scan resumes
    /// where it stopped (the frame's budget ran out, or it returned a chunk), so a radius of 48 chunks (about 7,200 of them)
    /// is spread over frames.</summary>
    (int X, int Z)? NextChunk(int px, int pz, long now, Stopwatch timer)
    {
        int radius = Radius();
        if (offsetsFor != radius)
        {
            var list = new List<(int X, int Z)>();
            // Within the configured radius a square, as before; beyond it the game's own disc of loaded chunks.
            for (int dz = -radius; dz <= radius; dz++) for (int dx = -radius; dx <= radius; dx++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) <= config.ObservedRadiusChunks || dx * dx + dz * dz <= (radius + 0.5) * (radius + 0.5)) list.Add((dx, dz));
            offsets = list.OrderBy(o => o.X * o.X + o.Z * o.Z).ToArray();
            offsetsFor = radius;
            scan = 0;
        }
        int reported = System.Threading.Volatile.Read(ref changes);
        if ((px, pz) != scanCentre || reported != scannedChanges) { scan = 0; scanCentre = (px, pz); scannedChanges = reported; restUntil = 0; }
        else if (now < restUntil) return null;
        var ba = api.World.BlockAccessor;
        for (; scan < offsets.Length; scan++)
        {
            if ((scan & 63) == 0 && timer.Elapsed.TotalMilliseconds >= 1) return null;
            var (dx, dz) = offsets[scan];
            var key = (X: px + dx, Z: pz + dz);
            if (key.X < 0 || key.Z < 0) continue;
            if (retryAt.TryGetValue(key, out long retry) && now < retry) continue;
            bool seen = recordedAt.TryGetValue(key, out long at);
            bool changed = changedChunks.ContainsKey(key) && (!seen || now - at >= ChangedAfterMs);
            if (!changed && seen && now - at < RevisitMs) continue;
            if (ba.GetMapChunk(key.X, key.Z)?.RainHeightMap == null) continue;
            changedChunks.TryRemove(key, out _);
            return key;
        }
        // Nothing due: rest a second, then look again from the nearest chunk (revisits fall due, chunks load).
        scan = 0; restUntil = now + 1000;
        return null;
    }

    void Prune(int px, int pz)
    {
        int keep = Radius() + 8;
        bool Far(int x, int z) => Math.Abs(x - px) > keep || Math.Abs(z - pz) > keep;
        foreach (var key in recordedAt.Keys.Where(k => Far(k.X, k.Z)).ToList()) recordedAt.Remove(key);
        foreach (var key in retryAt.Keys.Where(k => Far(k.X, k.Z)).ToList()) retryAt.Remove(key);
        // Changes reported where the player no longer is: a chunk recorded on a later visit compares its cells with the
        // prediction (or the generated terrain) again, so travelling cannot pile them up.
        foreach (var key in changedChunks.Keys) if (Far(key.X, key.Z)) changedChunks.TryRemove(key, out _);
        foreach (var key in changedCells.Keys) if (Far(key.X, key.Z)) changedCells.TryRemove(key, out _);
    }

    void FlagChangedSinceWorldgen(IBlockAccessor ba, IMapChunk map, int cx, int cz)
    {
        var worldgen = map.WorldGenTerrainHeightMap;
        if (worldgen == null) return;
        var at = new BlockPos(0, 0, 0);
        for (int i = 0; i < 1024; i++)
        {
            int cell = ((i >> 5) >> 2) * 8 + ((i & 31) >> 2);
            if (flagged[cell]) continue;
            int top = map.RainHeightMap[i];
            if (Math.Abs(top - worldgen[i]) < 3) continue;
            at.Set(cx * 32 + (i & 31), top, cz * 32 + (i >> 5));
            var block = ba.GetBlock(at);
            if (IsGrowth(block) || block.BlockMaterial == EnumBlockMaterial.Snow || block.IsLiquid() || ba.GetBlock(at, BlockLayersAccess.Fluid)?.Id > 0) continue;
            flagged[cell] = true;
        }
    }

    readonly BlockPos top = new(0, 0, 0);
    readonly Stopwatch timer = new();

    MapSample? Record(IBlockAccessor ba, IMapChunk map, int cx, int cz, int lx, int lz, bool fine)
    {
        int y = map.RainHeightMap[lz * 32 + lx], x = cx * 32 + lx, z = cz * 32 + lz;
        if (y < 1 || ba.GetChunk(cx, y / 32, cz) == null) { incomplete = true; return null; }
        top.Set(x, y, z);
        var sample = Classify(ba, map.WorldGenTerrainHeightMap, lz * 32 + lx, top, out var colored);
        if (sample.Kind == TileData.Unknown) { incomplete = true; return null; }
        top.Set(x, sample.Height + sample.Depth, z);
        // The game's colour and the block light for the mid-distance view; without it no colour, as before (nothing else
        // draws it, and Block.GetColor costs main-thread time per sample).
        if (config.MidDistanceRecorded) sample = sample with { Color = RecordedColor.Pack(ColorOf(colored, top), BlockLight(ba, top),
            RecordingRules.Structure(sample, map.WorldGenTerrainHeightMap?[lz * 32 + lx] ?? 0)) };
        if (sample.Kind == TileData.Land)
        {
            // The treetops over land (0.5.3-rc.4): the rain height above the ground found under them, in the depth land had
            // no use for, so far terrain stands forests at their real height with their real gaps.
            int canopy = RecordingRules.CanopyDepth(y, sample.Height);
            // A four-block sample of open ground stands for its whole cell: its height is the cell's mean generated ground
            // (0.5.3, WM-0021, WM-D61), from the chunk's height map (no block reads), so far terrain draws discovered land at
            // its height. Not under trees: their canopy is measured from the column's own ground, and the far view draws
            // ground plus canopy, so the treetops stay where they are (the re-review of 14 Nov: a canopy measured from the
            // mean made bare banks and ledges into trees).
            if (!fine && canopy == RecordingRules.CanopyDepth(0, 0) && RecordingRules.CellMean(map.WorldGenTerrainHeightMap, lx, lz, sample.Height) is int mean)
                sample = sample with { Height = mean };
            sample = sample with { Depth = canopy };
        }
        if (!tiles.Observe(x, z, sample, fine)) incomplete = true;
        Samples++;
        return sample;
    }

    int ColorOf(Block block, BlockPos pos)
    {
        try
        {
            if (block == null) return 0;
            // Grassy soil: far terrain's own grass colours, which follow Earth's grass seasons (RecordedColor.GrassMarker).
            // RecordingRules.TopColor without its closure: one per sample was allocated here (0.5.3, WM-0035).
            return RecordingRules.GrassTop(block.Variant?["grasscoverage"]) ? RecordedColor.GrassMarker : block.GetColor(api, pos);
        }
        catch (Exception) { return 0; }
    }

    /// <summary>The block light at a column's top, where it is seen from afar: the brighter of the top block and the one above
    /// it (a torch or lantern standing there, or the light falling on a roof). Light inside a building is not seen.</summary>
    public static int BlockLight(IBlockAccessor ba, BlockPos top)
    {
        int at = ba.GetLightLevel(top, EnumLightLevelType.OnlyBlockLight);
        top.Y++;
        try { return Math.Max(at, ba.GetLightLevel(top, EnumLightLevelType.OnlyBlockLight)); }
        finally { top.Y--; }
    }

    // Positions reused per thread: the recorder classifies hundreds of samples per tick (0.5.0-pre.2: it allocated them).
    [ThreadStatic] static BlockPos belowScratch, underScratch, bedScratch;
    static BlockPos Scratch(ref BlockPos slot, BlockPos from) => Scratch(ref slot, from.X, from.Y, from.Z);
    static BlockPos Scratch(ref BlockPos slot, int x, int y, int z) { slot ??= new BlockPos(0, 0, 0); slot.Set(x, y, z); return slot; }

    /// <summary>Trees and plants: not ground. Naturally grown logs only; placed logs and planks are buildings.</summary>
    public static bool IsGrowth(Block block) => block.BlockMaterial is EnumBlockMaterial.Leaves or EnumBlockMaterial.Plant
        || block.BlockMaterial == EnumBlockMaterial.Wood && block.Code?.Path.Contains("-grown-") == true;

    /// <summary>What the recorder stores for the column whose top block (the rain height) is at pos, without its
    /// colour; colored is the block that gives it.</summary>
    public static MapSample Classify(IBlockAccessor ba, ushort[] worldgen, int index, BlockPos pos, out Block colored)
    {
        var block = ba.GetBlock(pos);
        var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
        // Snow on a canopy is still canopy. Starting at the snow used to skip the growth scan and record the treetop
        // as white ground, then extrude it as a pillar outside the real chunks.
        if (block.BlockMaterial == EnumBlockMaterial.Snow)
        {
            var probe = Scratch(ref underScratch, pos);
            var covered = block;
            while (covered.BlockMaterial == EnumBlockMaterial.Snow && probe.Y > 1 && pos.Y - probe.Y < 8)
            {
                probe.Y--;
                if (ba.GetChunkAtBlockPos(probe) == null) { colored = null; return default; }
                covered = ba.GetBlock(probe);
            }
            if (IsGrowth(covered)) { pos.Set(probe.X, probe.Y, probe.Z); block = covered; fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid); }
        }
        // Trees and plants stand on the ground; the map shows the ground (canopies drew bumps at close zoom). A plant in
        // water (kelp and seaweed reaching the surface) is water: 0.4.4 went down to the seabed and recorded it as land,
        // dark islands on the sea.
        static bool Wet(Block fluid) => fluid != null && fluid.Id != 0;
        if (IsGrowth(block) && !Wet(fluid))
        {
            var below = Scratch(ref belowScratch, pos);
            // Down through the canopy, the air under it and the trunk, to the first other block (ground, water, a building)
            // or plant in water.
            for (int k = 0; k < 48 && (IsGrowth(block) || block.Id == 0) && !Wet(fluid) && below.Y > 1; k++)
            {
                below.Y--;
                if (ba.GetChunkAtBlockPos(below) == null) { colored = null; return default; }
                block = ba.GetBlock(below);
                fluid = ba.GetBlock(below, BlockLayersAccess.Fluid);
            }
            pos = below;
        }
        // A missing lower chunk, or a canopy taller than the scan, is not a surface sample.
        if ((block.Id == 0 && (fluid == null || fluid.Id == 0)) || IsGrowth(block) && (fluid == null || fluid.Id == 0))
        { colored = null; return default; }
        var surface = block;
        // Snow takes the kind of what it covers: Cylinder World drifts snow over polar sea ice and the Wall's top.
        // Its height still counts, as in predictions of the polar ice surface.
        var under = Scratch(ref underScratch, pos);
        for (int k = 0; k < 8 && block.BlockMaterial == EnumBlockMaterial.Snow && under.Y > 1; k++)
        {
            under.Y--;
            if (ba.GetChunkAtBlockPos(under) == null) { colored = null; return default; }
            block = ba.GetBlock(under);
            fluid = ba.GetBlock(under, BlockLayersAccess.Fluid);
        }
        bool wall = block.Code?.Path.Contains("wallice") == true;
        // Sea and lake ice can occupy the fluid layer as a solid, non-liquid block.
        bool fluidIce = fluid != null && fluid.Id != 0 && !fluid.IsLiquid() && fluid.BlockMaterial == EnumBlockMaterial.Ice;
        bool ice = block.BlockMaterial == EnumBlockMaterial.Ice || block.Code?.Path.Contains("glacier") == true || wall || fluidIce;
        bool water = !ice && (fluid?.IsLiquid() == true || block.IsLiquid());
        colored = surface != block ? surface : fluidIce || water && fluid?.IsLiquid() == true ? fluid : block;
        // Like predictions, water records its bed as the height and the water above it as the depth.
        int ground = water ? Bed(ba, worldgen, index, pos.X, pos.Y, pos.Z) : pos.Y;
        return new MapSample(ground, pos.Y - ground, (byte)(water ? 2 : wall ? 4 : ice ? 3 : 1));
    }

    /// <summary>The top solid block under the water surface at y: scanned down through the liquid, so a bed raised, dug
    /// out or built on (a platform with water under it) since generation counts. 0.4.3 took worldgen's terrain height,
    /// which is the bed of untouched water only. Worldgen's height still answers for water deeper than the scan reaches.</summary>
    public static int Bed(IBlockAccessor ba, ushort[] worldgen, int index, int x, int y, int z)
    {
        var pos = Scratch(ref bedScratch, x, y, z);
        int floor = Math.Max(1, y - 256);
        for (int below = y - 1; below >= floor; below--)
        {
            pos.Y = below;
            // A chunk below that is not loaded reads as air, which would put the bed at its top: worldgen's height answers.
            if ((below & GlobalConstants.ChunkSize - 1) == GlobalConstants.ChunkSize - 1 && ba.GetChunkAtBlockPos(pos) == null)
            {
                floor = below + 1;
                break;
            }
            var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid == null || fluid.Id == 0 || !fluid.IsLiquid()) return below;
        }
        int recorded = worldgen?[index] ?? 0;
        return recorded > 0 && recorded < floor ? recorded : floor;
    }
}
