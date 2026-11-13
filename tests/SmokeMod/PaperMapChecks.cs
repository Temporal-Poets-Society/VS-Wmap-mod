using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;
using Wmap;
using Wmap.Client;
using Wmap.Core;
using Wmap.Server;

/// <summary>Paper maps end to end (0.4.8, WM-P05): player A's client packs their charted area and recordings, the server
/// checks the upload, adds A's waypoints, automatic marker and reading from the game's own layers, keeps the map in the
/// save and turns A's blank map into a drawn one. The item goes to player B, who reads it: the server makes B's own
/// waypoints and reading, B's client takes the charted area and terrain and keeps B's own recorded cell. A second read
/// adds nothing; a damaged upload and reading without the map are refused.</summary>
public static class PaperMapChecks
{
    // IServerPlayer cannot be wrapped by DispatchProxy (see ServerAccessChecks); only the id is read here.
    sealed class MapPlayer : ServerPlayer
    {
        public string Uid;
        public MapPlayer() : base(null, null) { }
        public override string PlayerUID => Uid;
        // Not playing: the game's layers send nothing to it.
        public override EnumClientState ConnectionState => EnumClientState.Offline;
    }

    static MapPlayer Player(string uid)
    {
        var p = (MapPlayer)RuntimeHelpers.GetUninitializedObject(typeof(MapPlayer));
        p.Uid = uid;
        // The waypoint layer reads the player's groups when it sends them their list.
        p.serverdata = new ServerPlayerData();
        return p;
    }

    static void Wait(Func<bool> done, string what)
    {
        for (int n = 0; n < 2000; n++) { if (done()) return; Thread.Sleep(5); }
        throw new Exception("timed out: " + what);
    }

    public static string Run(ICoreServerAPI api, string folder)
    {
        var blank = api.World.GetItem(new AssetLocation("wmap", PaperMapServer.BlankCode)) ?? throw new Exception("no blank map item");
        var drawnItem = api.World.GetItem(new AssetLocation("wmap", PaperMapServer.DrawnCode)) ?? throw new Exception("no drawn map item");
        if (blank is not PaperMapItem || drawnItem is not PaperMapItem) throw new Exception("the map items are not of Wmap's item class");
        if (blank.MaxStackSize != 16 || drawnItem.MaxStackSize != 1) throw new Exception($"stack sizes {blank.MaxStackSize}, {drawnItem.MaxStackSize}");
        int recipes = api.World.GridRecipes.Count(r => r.Output?.Code?.Domain == "wmap" && r.Output.Code.Path.StartsWith("papermap-"));
        if (recipes != 2) throw new Exception($"{recipes} map recipes loaded, not 2 (blank map, copy)");
        var blankRecipe = api.World.GridRecipes.Single(r => r.Output?.Code?.ToString() == "wmap:papermap-blank");
        var blankIngredients = blankRecipe.ResolvedIngredients.Where(i => i != null).ToList();
        int parchment = blankIngredients.Where(i => i.ResolvedItemStack.Collectible.Code.ToString() == "game:paper-parchment").Sum(i => i.Quantity);
        if (parchment != 8 || blankRecipe.Width != 3 || blankRecipe.Height != 3 || blankRecipe.Shapeless ||
            blankIngredients.Count(i => i.ResolvedItemStack.Collectible.Code.ToString() == "game:inkandquill" &&
                !i.ConsumeProperties.Consume && i.ConsumeProperties.DurabilityCost == 0) != 1)
            // The ink and quill has no durability, so an isTool ingredient (cost 1) would block the craft (0.5.1)
            throw new Exception("Blank map must use eight parchments around one ink and quill that is kept and costs no durability");
        var maps = api.ModLoader.GetModSystem<WorldMapManager>();
        var layer = maps?.MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault() ?? throw new Exception("no waypoint layer on the server");
        var ores = maps.MapLayers.OfType<OreMapLayer>().FirstOrDefault() ?? throw new Exception("no prospecting layer on the server");
        var a = Player("paper-a"); var b = Player("paper-b");
        var info = new AtlasInfo { Session = "paper", Width = 2048, Height = 1024, Allowed = true, SeaLevel = 110 };
        var t = info.Topology();
        var config = new WmapConfig(); config.Validate();
        var toClient = new List<(MapPiece Piece, IServerPlayer To)>();
        var serverChannel = GuiServiceProxy.Make<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket" && args[0] is MapPiece p && args[1] is IServerPlayer[] { Length: 1 } to) toClient.Add((p, to[0]));
            return GuiServiceProxy.Default(m);
        });
        var server = new PaperMapServer(api, serverChannel, config, () => t);
        var slots = new Dictionary<string, ItemSlot>();
        var told = new List<string>();
        server.Held = p => slots.TryGetValue(p.PlayerUID, out var s) ? s : null;
        server.Tell = (p, text) => told.Add(p.PlayerUID + ": " + text);
        server.Background = (work, done) => { object r; try { r = work(); } catch (Exception e) { r = e; } done(r); };
        // The check draws and reads again at once; the intervals are checked below.
        server.DrawInterval = server.ReadInterval = 0;
        string Told() => string.Join(" | ", told);

        string auto = AutoMarkCatalog.TagFor(AutoMarkCatalog.Find("berries"));
        Waypoint W(string uid, double x, string title, string text, string icon, bool temporary = false) => new()
        {
            Position = new Vec3d(x, 120, 300), Title = title, Text = text, Icon = icon, Color = -1, OwningPlayerUid = uid, Guid = Guid.NewGuid().ToString(), Temporary = temporary
        };
        var aBerries = W(a.Uid, 200.5, "Blueberry bush", auto, "berries");
        layer.Waypoints.AddRange(new[] { W(a.Uid, 100.5, "Base", "", "home"), aBerries, W(a.Uid, 300.5, "Here", "", "circle", true), W(b.Uid, 100.7, "Base", "", "home") });
        var reading = new PropickReading { Position = new Vec3d(400.5, 90, 500.5), Guid = Guid.NewGuid().ToString() };
        reading.OreReadings["nativecopper"] = new OreReading { DepositCode = "nativecopper", TotalFactor = 0.3, PartsPerThousand = 2.5 };
        ores.getOrLoadReadings(a).Add(reading);
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            string dirA = Path.Combine(folder, "a"), dirB = Path.Combine(folder, "b");
            // A recorded land at 130 over a four-block tile and one block-by-block tile; B recorded one cell of the same
            // four-block tile, at 150.
            var coarse = new TileKey(1, 2, 0, 0); var fine = new TileKey(1, 0, 3, 2);
            int ownCell = 5 * TileData.Padded + 5, filledCell = 9 * TileData.Padded + 9;
            var aTile = new TileData();
            for (int z = 1; z <= 20; z++) for (int x = 1; x <= 20; x++) aTile.Set(z * TileData.Padded + x, new MapSample(130, 0, TileData.Land));
            var bTile = new TileData(); bTile.Set(ownCell, new MapSample(150, 0, TileData.Land));
            TileCodec.WriteAtomic(Path.Combine(dirA, coarse.FileName), TileCodec.Encode(aTile));
            // Colours that do not compress make the map larger than one piece each way.
            var noisy = aTile.Copy();
            var random = new Random(5);
            for (int i = 0; i < TileData.Count; i++) if (noisy.Kinds[i] != TileData.Unknown) noisy.Colors[i] = random.Next();
            for (int z = 21; z <= TileData.Size; z++) for (int x = 1; x <= TileData.Size; x++) noisy.Set(z * TileData.Padded + x, new MapSample(120, 0, TileData.Land, random.Next()));
            TileCodec.WriteAtomic(Path.Combine(dirA, fine.FileName), TileCodec.Encode(noisy));
            TileCodec.WriteAtomic(Path.Combine(dirB, coarse.FileName), TileCodec.Encode(bTile));
            // The recorder's marker: without it the client drops old one- and two-block recordings as 0.4.3's.
            File.WriteAllText(Path.Combine(dirA, TileClient.RecorderMarker), ""); File.WriteAllText(Path.Combine(dirB, TileClient.RecorderMarker), "");

            // A draws.
            using var tilesA = new TileClient(DispatchProxy.Create<IClientNetworkChannel, FakeChannel>(), info, config, Path.Combine(folder, "cache-a"), dirA);
            var maskA = new ExploredMask(t);
            maskA.Reveal(500, 300, 100);
            var sentA = new List<object>();
            var clientA = new PaperMapClient(tilesA, maskA, GuiServiceProxy.Make<IClientNetworkChannel>((m, args) => { if (m.Name == "SendPacket") sentA.Add(args[0]); return GuiServiceProxy.Default(m); }), text => told.Add("client a: " + text));
            Wait(() => { tilesA.Tick(); return tilesA.RecordingCount == 2; }, "A's recordings indexed");
            slots[a.Uid] = new DummySlot(new ItemStack(blank));
            clientA.Draw("Smoke map", MapParts.All);
            Wait(() => { clientA.Tick(); return !clientA.Drawing; }, "A's map packed and uploaded");
            int pieces = sentA.Count(m => m is MapPiece);
            if (pieces < 2) throw new Exception($"the upload took {pieces} piece, so putting pieces together went unchecked");
            foreach (var m in sentA) { if (m is MapDraw d) server.Draw(a, d); else if (m is MapPiece p) server.Piece(a, p); }
            var drawn = slots[a.Uid].Itemstack;
            if (drawn?.Collectible?.Code?.Path != PaperMapServer.DrawnCode) throw new Exception("A's blank map did not become a drawn map: " + Told());
            var desc = drawn.Attributes.GetTreeAttribute(PaperMapServer.Attribute) ?? throw new Exception("the drawn map has no description");
            string id = desc.GetString("id");
            if (desc.GetString("title") != "Smoke map" || (MapParts)desc.GetInt("holds") != MapParts.All || desc.GetInt("tiles") != 2 || desc.GetInt("waypoints") != 1
                || desc.GetInt("automarks") != 1 || desc.GetInt("readings") != 1 || !(desc.GetDouble("km2") > 0))
                throw new Exception("the drawn map's description: " + PaperMapItem.Contents(desc) + " titled " + desc.GetString("title"));
            byte[] stored = api.WorldManager.SaveGame.GetData(PaperMap.SaveKey(id)) ?? throw new Exception("the map is not in the save");

            // The map goes to B, who reads it.
            slots[b.Uid] = new DummySlot(drawn); slots[a.Uid] = new DummySlot();
            using var tilesB = new TileClient(DispatchProxy.Create<IClientNetworkChannel, FakeChannel>(), info, config, Path.Combine(folder, "cache-b"), dirB);
            var maskB = new ExploredMask(t);
            var sentB = new List<object>();
            var clientB = new PaperMapClient(tilesB, maskB, GuiServiceProxy.Make<IClientNetworkChannel>((m, args) => { if (m.Name == "SendPacket") sentB.Add(args[0]); return GuiServiceProxy.Default(m); }), text => told.Add("client b: " + text));
            Wait(() => { tilesB.Tick(); return tilesB.RecordingCount == 1; }, "B's recording indexed");
            int downloaded = 0;
            void ReadAsB()
            {
                sentB.Clear();
                clientB.Read(id, MapParts.All);
                foreach (var r in sentB.OfType<MapRead>()) server.Read(b, r);
                Wait(() =>
                {
                    server.Tick();
                    foreach (var (piece, to) in toClient) if (to == b) { clientB.Receive(piece); downloaded++; }
                    toClient.Clear();
                    clientB.Tick(); tilesB.Tick();
                    return !clientB.Reading;
                }, "B's copy merged: " + Told());
            }
            ReadAsB();
            var bOwn = layer.Waypoints.Where(w => w.OwningPlayerUid == b.Uid).ToList();
            if (bOwn.Count(w => w.Title == "Base") != 1) throw new Exception($"B has {bOwn.Count(w => w.Title == "Base")} Base waypoints, not 1 (B had one where A's is)");
            var bBerries = bOwn.SingleOrDefault(w => w.Title == "Blueberry bush") ?? throw new Exception("B did not get A's automatic marker");
            if (bBerries.Text != auto || bBerries.Guid == aBerries.Guid || bOwn.Any(w => w.Title == "Here")) throw new Exception("B's copies: an automatic marker keeps its tag and gets its own id; a temporary waypoint is not drawn");
            var bReadings = ores.getOrLoadReadings(b);
            if (bReadings.Count != 1 || bReadings[0].Guid == reading.Guid || !(bReadings[0].OreReadings.TryGetValue("nativecopper", out var ore) && Math.Abs(ore.TotalFactor - 0.3) < 1e-9))
                throw new Exception($"B's readings: {bReadings.Count}");
            if (!(maskB.At(500, 300) > 0) || maskB.At(1500, 900) != 0) throw new Exception("B's charted area is not A's");
            var bCoarse = tilesB.Get(coarse)?.Data ?? throw new Exception("B's four-block recording is not loaded");
            if (bCoarse.Heights[ownCell] != 150 || bCoarse.Kinds[filledCell] != TileData.Land || bCoarse.Heights[filledCell] != 130) throw new Exception("B's own cell must stay and A's fill the rest");
            if (!tilesB.HasObservation(fine) || tilesB.Get(fine)?.Data?.Heights[filledCell] != 130) throw new Exception("B did not get A's block-by-block recording");
            int merged = clientB.Merged;
            if (downloaded < 2) throw new Exception($"B's copy came in {downloaded} piece, so putting pieces together went unchecked");

            // A second read adds nothing.
            ReadAsB();
            if (layer.Waypoints.Count(w => w.OwningPlayerUid == b.Uid) != bOwn.Count || ores.getOrLoadReadings(b).Count != 1) throw new Exception("a second read added markers again");

            // Refusals: reading without the map, and a damaged upload.
            int before = told.Count;
            server.Read(a, new MapRead { Id = id, Parts = (int)MapParts.All });
            if (!told.Skip(before).Any(s => s.Contains("hold the map"))) throw new Exception("A read a map A no longer holds: " + Told());
            slots[a.Uid] = new DummySlot(new ItemStack(blank));
            before = told.Count;
            server.Draw(a, new MapDraw { Id = "bad", Parts = (int)MapParts.All, Total = 12 });
            server.Piece(a, new MapPiece { Id = "bad", Offset = 0, Total = 12, Data = new byte[12] });
            if (slots[a.Uid].Itemstack?.Collectible?.Code?.Path != PaperMapServer.BlankCode || !told.Skip(before).Any(s => s.Contains("could not be drawn"))) throw new Exception("a damaged upload was drawn: " + Told());
            // A drawing started within the interval of the last is not taken: its pieces go nowhere.
            server.DrawInterval = 60_000;
            before = told.Count;
            server.Draw(a, new MapDraw { Id = "soon", Parts = (int)MapParts.All, Total = 12 });
            server.Piece(a, new MapPiece { Id = "soon", Offset = 0, Total = 12, Data = new byte[12] });
            if (told.Skip(before).Any(s => s.Contains("could not be drawn"))) throw new Exception("a drawing within the interval of the last was taken: " + Told());
            return $"Paper maps: PASS (A drew {stored.Length / 1024.0:0.#} KiB from a {pieces}-piece upload: {PaperMapItem.Contents(desc)}; B read it in {downloaded} pieces: own copies of the waypoint, automatic marker and reading, "
                + $"the charted area, {merged} tiles merged keeping B's own cell; a second read added nothing; damaged upload and reading without the map refused; 2 recipes)";
        }
        finally
        {
            layer.Waypoints.RemoveAll(w => w.OwningPlayerUid == a.Uid || w.OwningPlayerUid == b.Uid);
            ores.PropickReadingsByPlayer.Remove(a.Uid); ores.PropickReadingsByPlayer.Remove(b.Uid);
        }
    }
}
