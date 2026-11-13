using System;
using System.Linq;
using System.Text.Json.Nodes;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Wmap.Client;

/// <summary>
/// The Jonas barometer as the game loads it: the item and its wind-instrument attribute, its shape and every texture
/// that shape names, its crafting recipe with all ingredients resolved, and its English names.
/// </summary>
public static class BarometerChecks
{
    public static string Run(ICoreServerAPI api)
    {
        var item = api.World.GetItem(new AssetLocation("wmap:jonasbarometer")) ?? throw new Exception("Jonas barometer item not registered");
        if (!WindSight.IsInstrument(new ItemStack(item))) throw new Exception("Jonas barometer is not a wind instrument");
        // InventoryItemRenderer uses GUI's downward Y for items (only blocks get an extra 180-degree X turn).
        // Server item instances omit client transforms; read the same source asset the client loader uses.
        var rotation = Vintagestory.API.Datastructures.JsonObject.FromJson(api.Assets.Get(new AssetLocation("wmap:itemtypes/jonasbarometer.json")).ToText())["guiTransform"]["rotation"];
        var gui = new Matrixf().RotateXDeg(rotation["x"].AsFloat()).RotateYDeg(rotation["y"].AsFloat()).RotateZDeg(rotation["z"].AsFloat());
        if (gui.Values[5] >= 0) throw new Exception("Jonas barometer's model up vector points down in the inventory");
        var shapeAsset = api.Assets.TryGet(new AssetLocation("wmap:shapes/item/jonasbarometer.json")) ?? throw new Exception("Jonas barometer shape missing");
        var shape = JsonNode.Parse(shapeAsset.ToText());
        int textures = 0;
        foreach (var texture in shape["textures"].AsObject())
        {
            var path = new AssetLocation(texture.Value.ToString());
            if (!api.Assets.Exists(new AssetLocation(path.Domain, "textures/" + path.Path + ".png"))) throw new Exception("Jonas barometer texture missing: " + path);
            textures++;
        }
        var recipes = api.World.GridRecipes.Where(r => r.Output?.Code?.ToString() == "wmap:jonasbarometer").ToList();
        if (recipes.Count != 1) throw new Exception($"Expected one Jonas barometer recipe, found {recipes.Count}");
        var recipe = recipes[0];
        if (recipe.Output.ResolvedItemStack == null || recipe.ResolvedIngredients == null) throw new Exception("Jonas barometer recipe unresolved");
        // The game drops the keyed ingredients once resolved; the grid of resolved ones stays (empty cells are null).
        var used = recipe.ResolvedIngredients.Where(i => i != null).ToList();
        if (used.Count != 7 || used.Any(i => i.ResolvedItemStack == null)) throw new Exception($"Jonas barometer recipe: {used.Count(i => i.ResolvedItemStack != null)} of 7 ingredients resolved");
        var lang = JsonNode.Parse(api.Assets.Get(new AssetLocation("wmap:lang/en.json")).ToText());
        foreach (string key in new[] { "item-jonasbarometer", "itemdesc-jonasbarometer", "handbook-jonasbarometer-title", "handbook-jonasbarometer-text" })
            if (string.IsNullOrWhiteSpace(lang[key]?.GetValue<string>())) throw new Exception("Missing English text " + key);
        return $"Jonas barometer: PASS (item, wind-instrument attribute, shape with {shape["elements"].AsArray().Count} elements and {textures} game textures, recipe with {used.Count} resolved ingredients, English names)";
    }
}
