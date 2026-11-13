using System;
using Vintagestory.API.Client;
using Wmap.Client;

public static class MapKeyChecks
{
    public static void Run()
    {
        int atlas = 0, vanilla = 0;
        var mapping = new KeyCombination { KeyCode = (int)GlKeys.N, Shift = true };
        var key = new HotKey { Code = MapKeyBinding.Code, Name = "Original map", CurrentMapping = mapping, Handler = _ => { vanilla++; return true; } };
        var original = key.Handler;
        using (var binding = new MapKeyBinding(key, () => atlas++))
        {
            if (!ReferenceEquals(key.CurrentMapping, mapping)) throw new Exception("Wmap overwrote the user's map binding");
            if (!key.Handler(mapping) || atlas != 1 || vanilla != 0) throw new Exception("Map action did not open Wmap exclusively");
            if (!binding.OpenVanilla() || vanilla != 1) throw new Exception("Original map fallback failed");
        }
        if (key.Handler != original || key.Name != "Original map") throw new Exception("Original map action was not restored");
        using (var binding = new MapKeyBinding(key, () => atlas++)) key.Handler = _ => false;
        if (key.Handler(mapping)) throw new Exception("Cleanup overwrote a later mod's handler");
        var unbound = new HotKey { CurrentMapping = mapping };
        using (var binding = new MapKeyBinding(unbound, () => atlas++))
            if (binding.OpenVanilla()) throw new Exception("Missing original map reported as opened");
    }
}
