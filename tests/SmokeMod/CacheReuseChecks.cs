using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Client;
using Wmap;
using Wmap.Client;
using Wmap.Core;

/// <summary>0.4.4, with the GPU check's GL context: what a still view keeps instead of working out every frame, and the
/// far-terrain disk cache of a server without biome tiles.</summary>
public static class CacheReuseChecks
{
    static MapSample Ground(double x, double z) => new(125 + (int)(x / 512) % 7, 0, TileData.Land);

    /// <summary>A map view (atlas, minimap) selects its tiles once while it stands still, again when it moves, and again
    /// when another view replaced the tile client's wanted tiles; its draw list is the atlas's.</summary>
    public static string TileViews(string output, Topology t, int width, int height)
    {
        var channel = DispatchProxy.Create<IClientNetworkChannel, FakeChannel>();
        var info = new AtlasInfo { Session = "views", Width = t.Width, Height = t.Height, StartX = t.StartX, Wraps = t.Wraps, CanDevelop = true, CanPredict = true, HasOverview = true, SeaLevel = 110 };
        string cache = Path.Combine(output, "view-tiles");
        using var tiles = new TileClient(channel, info, new WmapConfig(), cache, Path.Combine(output, "view-observed"));
        using var atlas = new TileAtlas(tiles, 2);
        var camera = new MapCamera { X = t.StartX + t.Width / 3, Z = t.Height / 2, Scale = 0.5 };
        int asked = 0;
        // Observations on: the selection asks whether each recorded tile exists, which counts the selections.
        var policy = new ViewPolicy(0, true, true, _ => { asked++; return false; }, 200);
        var keys = new List<TileInstance>();
        ViewTiles.Select(keys, t, camera, width, height, policy);
        foreach (var key in keys.Select(k => k.Key).Where(k => k.Layer == 0).Distinct())
            TileCodec.WriteAtomic(Path.Combine(cache, key.FileName), SemanticTile.Pack(SemanticTile.Sample(t, key, Ground, 110, CancellationToken.None)));
        var view = new TileView();
        for (int n = 0; n < 3000 && (view.Visible.Count == 0 || view.Visible.Any(v => v.Key.Layer == 0 && !atlas.Has(v.Key))); n++)
        {
            tiles.Tick();
            view.Update(tiles, atlas, t, camera, width, height, policy, 0);
            Thread.Sleep(2);
        }
        if (view.Visible.Count == 0 || view.Visible.Any(v => v.Key.Layer == 0 && !atlas.Has(v.Key))) throw new Exception("Tile view: tiles did not load and upload");
        var expected = atlas.Display(view.Visible, t, camera, width, height, 0).ToList();
        if (!expected.SequenceEqual(view.Display)) throw new Exception("Tile view: the draw list differs from the atlas's");
        int before = asked, wanted = tiles.WantedVersion;
        for (int n = 0; n < 200; n++) view.Update(tiles, atlas, t, camera, width, height, policy, 0);
        if (asked != before || tiles.WantedVersion != wanted) throw new Exception($"Tile view: a still view selected again ({asked - before} lookups)");
        camera.X += 200 / camera.Scale;
        view.Update(tiles, atlas, t, camera, width, height, policy, 0);
        if (asked == before) throw new Exception("Tile view: a moved view kept its old tiles");
        before = asked;
        // Another view (the atlas over the minimap) takes the tile client's wanted tiles; this one asks again for its own.
        tiles.SetView(new[] { new TileKey(0, 2, 0, 0) });
        view.Update(tiles, atlas, t, camera, width, height, policy, 0);
        if (asked == before) throw new Exception("Tile view: did not ask again after another view replaced its tiles");
        return $"tile view (still: no selection in 200 frames; moved and replaced: selected again; {view.Display.Count} tiles drawn)";
    }

    /// <summary>Near-band tiles from a server without biome tiles come from the disk cache in a later session (0.4.3
    /// wanted a biome part on disk too, so it asked the server again for every tile).</summary>
    public static string BiomelessFarCache(string output, Topology t)
    {
        string folder = Path.Combine(output, "far-biomeless");
        var pending = new Queue<TileRequest>();
        int requests = 0;
        double x = t.StartX + t.Width / 3, z = t.Height / 2;
        var mesh = FarMesh.Build(8192, FarMesh.NearBandCell);
        var (ox, _) = FarTerrainDraw.Origin(mesh, x, z);
        double inner = FarTerrain.StartFor(512);
        FarTiles Session() => new(t, "biomeless", r => { requests++; pending.Enqueue(r); }, folder, 512, 1536, 8192, true, false, null);
        bool Fill(FarTiles tiles)
        {
            tiles.CreateTextures();
            for (int n = 0; n < 6000; n++)
            {
                tiles.Update(x, z, ox, inner, 64);
                while (pending.Count > 0)
                {
                    var r = pending.Dequeue();
                    var key = new TileKey(0, r.Level, r.X, r.Z);
                    // Semantic tiles only: no biome sampler on this server.
                    tiles.Receive(new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = 1, Content = r.Content,
                        Data = SemanticTile.Pack(SemanticTile.Sample(t, key, Ground, 110, CancellationToken.None)) });
                }
                if (tiles.Band != null && tiles.Band.Wanted > 0 && tiles.Band.Ready == tiles.Band.Wanted) return true;
                Thread.Sleep(5);
            }
            return false;
        }
        using (var first = Session()) { if (first.Band == null || !Fill(first)) throw new Exception("Biomeless far cache: the first session's band did not fill: " + first.LastError); }
        int firstRequests = requests;
        requests = 0;
        long hits;
        using (var second = Session())
        {
            if (!Fill(second)) throw new Exception("Biomeless far cache: the second session's band did not fill: " + second.LastError);
            hits = second.DiskHits;
        }
        if (firstRequests == 0 || requests != 0 || hits == 0) throw new Exception($"Biomeless far cache: the second session asked the server for {requests} tiles ({hits} from disk; the first asked {firstRequests})");
        return $"biomeless far cache ({firstRequests} tiles asked once, then {hits} from disk)";
    }
}
