using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Wmap;
using Wmap.Client;
using Wmap.Core;

/// <summary>Production detail loading/drawing across zoom changes and arrival of the world chart.</summary>
public static class DetailZoomChecks
{
    public static string Run(string output, int width, int height)
    {
        var topology = new Topology { Width = 16384, Height = 8192, Wraps = true };
        var camera = new MapCamera { X = 8448, Z = 4352, Scale = .5 };
        var frame = new MapFrame { Width = 320, Height = 240, Camera = camera, Topology = topology, Style = 1, Development = true, HeightScale = 200 };
        float[] projection = { 2f / width, 0, 0, 0, 0, -2f / height, 0, 0, 0, 0, -.01f, 0, -1, 1, 0, 1 };
        float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var info = new AtlasInfo { Session = "detail-zoom", Width = topology.Width, Height = topology.Height, Wraps = true, CanPredict = true, CanDevelop = true, SeaLevel = 110 };
        var folder = Path.Combine(output, "detail-zoom-cache"); Directory.CreateDirectory(folder);
        using var client = new TileClient(DispatchProxy.Create<IClientNetworkChannel, FakeChannel>(), info, new WmapConfig { ResidentTiles = 128 }, folder);
        using var atlas = new TileAtlas(client, 1);
        using var renderer = new MapRenderer();
        var coarse = TileSelection.Visible(topology, camera, frame.Width, frame.Height, 3).ToArray();
        var fine = TileSelection.Visible(topology, camera, frame.Width, frame.Height, 2).ToArray();
        MapSample Hills(double x, double z) => new((int)(175 + 20 * Math.Sin(x / 24) + 15 * Math.Cos(z / 32)), 0, TileData.Land);
        void Load(IEnumerable<TileKey> keys)
        {
            var all = keys.Distinct().ToArray();
            foreach (var key in all)
                File.WriteAllBytes(Path.Combine(folder, key.FileName), SemanticTile.Pack(SemanticTile.Sample(topology, key, Hills, 110, CancellationToken.None)));
            client.SetView(all);
            for (int n = 0; n < 3000 && all.Any(key => client.Get(key)?.Rgba == null); n++) { client.Tick(); Thread.Sleep(2); }
            if (all.Any(key => client.Get(key)?.Rgba == null)) throw new Exception("Detail zoom test did not load disk tiles");
        }
        byte[] Draw(IReadOnlyList<TileInstance> requested)
        {
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace);
            GL.Viewport(0, 0, width, height); GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
            if (!renderer.Begin(projection, identity, frame, false)) throw new Exception(renderer.Error);
            renderer.DrawWorld(51);
            int n = 0;
            foreach (var tile in atlas.Display(requested, topology, camera, frame.Width, frame.Height, renderer.BaseReady ? renderer.BaseTexel : 0)) atlas.Draw(tile, renderer, 1, 51.05f + .005f * n++);
            renderer.End();
            var pixels = new byte[320 * 240 * 4];
            GL.ReadPixels(0, height - 240, 320, 240, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            return pixels;
        }
        Load(coarse.Select(tile => tile.Key)); atlas.Upload(coarse.Select(tile => tile.Key), 1000);
        if (!atlas.Display(fine, topology, camera, frame.Width, frame.Height).Any(tile => tile.Key.Level == 3))
            throw new Exception("Cached parent detail vanished while the finer level was loading");
        var fallback = Draw(fine);
        var zoomedOut = TileSelection.Visible(topology, camera, frame.Width, frame.Height, 4).ToArray();
        if (!atlas.Display(zoomedOut, topology, camera, frame.Width, frame.Height).Any(tile => tile.Key.Level == 3))
            throw new Exception("Cached child detail vanished while zooming out");
        Load(fine.Select(tile => tile.Key)); atlas.Upload(fine.Select(tile => tile.Key), 1000);
        var veryCoarse = new TileKey(0, 8, 0, 0);
        Load(new[] { veryCoarse }); atlas.Upload(new[] { veryCoarse }, 1000);
        var before = Draw(fine);
        var raster = BaseRaster.Bake(topology, 128, 64, 110, () => (x, z) => new MapSample(175, 0, TileData.Land), null, 2, CancellationToken.None);
        renderer.SetBase(raster, topology); while (!renderer.BaseReady) renderer.PumpBase();
        if (atlas.Display(fine, topology, camera, frame.Width, frame.Height, renderer.BaseTexel).Any(tile => tile.Key.Level == 8))
            throw new Exception("A fallback coarser than the finished world chart covered it");
        var after = Draw(fine);
        double difference = before.Zip(after, (a, b) => Math.Abs(a - b)).Average();
        if (difference > .5) throw new Exception($"World chart replaced detailed relief: mean channel difference {difference:0.00}");
        if (fallback.Zip(after, (a, b) => Math.Abs(a - b)).Average() < 1) throw new Exception("Finer terrain never replaced cached parent detail");
        // The CPU cache is smaller than this atlas. Eviction must preserve the uploaded copy.
        Load(Enumerable.Range(0, 128).Select(x => new TileKey(0, 0, x, 0)));
        if (fine.Any(tile => client.Get(tile.Key) != null)) throw new Exception("Detail zoom test did not exercise CPU eviction");
        if (fine.Any(tile => !atlas.Has(tile.Key))) throw new Exception("CPU eviction erased reusable GPU detail");
        var revisited = Draw(fine);
        if (!after.SequenceEqual(revisited)) throw new Exception("Returning to a cached zoom changed the map or required reloading");
        client.SetView(fine.Select(tile => tile.Key));
        for (int n = 0; n < 3000 && fine.Any(tile => client.Get(tile.Key)?.Rgba == null); n++)
        {
            client.Tick();
            if (fine.Any(tile => !atlas.Has(tile.Key))) throw new Exception("Reloading identical disk data blanked surviving GPU detail");
            Thread.Sleep(2);
        }
        if (fine.Any(tile => client.Get(tile.Key)?.Rgba == null)) throw new Exception("Detail reload test did not finish");
        if (!after.SequenceEqual(Draw(fine))) throw new Exception("Identical disk reload changed cached rendering");
        // A real content change must still remove the old uploaded image.
        var changed = new TileKey(1, 2, 0, 0);
        client.SetView(new[] { changed });
        void Wait(Func<bool> done)
        {
            for (int n = 0; n < 3000 && !done(); n++) { client.Tick(); Thread.Sleep(2); }
            if (!done()) throw new Exception("Changed-content test did not finish");
        }
        Wait(() => client.Get(changed)?.Data != null);
        client.Observe(10, 10, new MapSample(150, 0, TileData.Land));
        Wait(() => client.Get(changed).RenderedRevision == client.Get(changed).Revision);
        atlas.Upload(new[] { changed }, 1000);
        if (!atlas.Has(changed)) throw new Exception("Recorded terrain was not uploaded");
        client.Observe(10, 10, new MapSample(220, 0, TileData.Land));
        Wait(() => client.Get(changed).RenderedRevision == client.Get(changed).Revision);
        if (atlas.Has(changed)) throw new Exception("Changed terrain did not invalidate its GPU copy");
        // A cached observation corrects a newly requested prediction even when the request contains no observed keys.
        // This is the old-chunk/new-generator case: changing the zoom must not put the new river over known dry land.
        var nearObservation = new TileKey(1, 2, (int)(camera.X / 512), (int)(camera.Z / 512));
        var oldChunk = new TileData(); oldChunk.Set(65 * TileData.Padded + 65, new MapSample(155, 0, TileData.Land));
        File.WriteAllBytes(Path.Combine(folder, nearObservation.FileName), TileCodec.Encode(oldChunk));
        client.SetView(new[] { nearObservation });
        Wait(() => client.Get(nearObservation)?.Rgba != null);
        atlas.Upload(new[] { nearObservation }, 1000);
        var incoming = TileSelection.Visible(topology, camera, frame.Width, frame.Height, 0).ToArray();
        var withObservedFallback = atlas.Display(incoming, topology, camera, frame.Width, frame.Height, renderer.BaseTexel).ToArray();
        int observedAt = Array.FindIndex(withObservedFallback, tile => tile.Key == nearObservation);
        // Finer predictions draw over it, but each is checked cell by cell against it (WM-D41).
        var shown = withObservedFallback.Where(tile => tile.Key.Layer == 1).Select(tile => tile.Key).ToHashSet();
        // The incoming one-block tiles are not uploaded yet: the cached predictions of its own level and coarser draw under it.
        bool Inside(TileInstance tile) => tile.Key.X >> (2 - tile.Key.Level) == nearObservation.X && tile.Key.Z >> (2 - tile.Key.Level) == nearObservation.Z;
        var over = withObservedFallback.Skip(observedAt + 1).Where(tile => tile.Key.Layer == 0 && tile.Key.Level < 2 && Inside(tile))
            .Concat(incoming.Where(Inside)).ToArray();
        if (observedAt < 0 || withObservedFallback.Skip(observedAt + 1).Any(tile => tile.Key.Layer == 0 && tile.Key.Level >= 2)
            || over.Length == 0 || over.Any(tile => ViewTiles.Reference(tile, shown.Contains, topology.MaxLevel)?.Key != nearObservation))
            throw new Exception("An uploaded observation does not check the finer predictions drawn over it");
        if (atlas.Display(incoming, topology, camera, frame.Width, frame.Height, renderer.BaseTexel, false).Any(tile => tile.Key.Layer == 1))
            throw new Exception("An observations-disabled view drew observed fallback tiles");
        if (GL.GetError() is var error && error != ErrorCode.NoError) throw new Exception("Detail zoom OpenGL error: " + error);
        return "detail zoom: cached parent/child fallback, finer-level promotion, no predicted fallback coarser than the chart, identical relief before/after chart arrival, immediate GPU reuse after CPU eviction/disk reload, changed-content invalidation, observed fallback under, and checking, finer incoming predictions";
    }
}
