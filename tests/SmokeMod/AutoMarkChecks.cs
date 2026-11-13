using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;
using Wmap.Core;

/// <summary>Every automatic marker pattern names blocks or creatures the game has: a pattern left behind by a renamed
/// block would silently mark nothing (0.4.6).</summary>
static class AutoMarkChecks
{
    public static string Run(ICoreServerAPI api)
    {
        var blocks = api.World.Blocks.Where(b => b?.Code != null && b.Code.Domain == "game").Select(b => b.Code.Path).ToList();
        var entities = api.World.EntityTypes.Where(e => e?.Code != null && e.Code.Domain == "game").Select(e => e.Code.Path).ToList();
        var failures = new List<string>();
        var counts = new List<string>();
        foreach (var kind in AutoMarkCatalog.All)
        {
            int n = 0;
            foreach (string pattern in kind.Blocks)
            {
                int matched = blocks.Count(code => AutoMarkCatalog.Matches(pattern, code));
                if (matched == 0) failures.Add($"{kind.Code}: no block matches {pattern}");
                n += matched;
            }
            foreach (string pattern in kind.Entities)
            {
                int matched = entities.Count(code => AutoMarkCatalog.Matches(pattern, code));
                if (matched == 0) failures.Add($"{kind.Code}: no creature matches {pattern}");
                n += matched;
            }
            // A block of one kind must not also be of another: the first kind would always win.
            foreach (string code in blocks.Where(c => kind.Blocks.Any(p => AutoMarkCatalog.Matches(p, c))))
                if (AutoMarkCatalog.ForBlock(code) != kind) failures.Add($"{code} is {kind.Code} and {AutoMarkCatalog.ForBlock(code)?.Code}");
            counts.Add($"{kind.Code} {n}");
        }
        if (failures.Count > 0) throw new Exception("Automatic markers: " + string.Join("; ", failures.Distinct().Take(12)));
        return "Automatic markers: PASS (every pattern matches the game's blocks and creatures: " + string.Join(", ", counts) + ")";
    }
}
