using System;
using System.Reflection;

namespace Wmap.Integrations;

/// <summary>
/// Earth's per-thread switch to its built-in heights, <c>EarthMap.Core.FineElevation.BuiltInOnly</c> (Earth 1.0.3 with
/// Copernicus GLO-90 heights, Earth decision 0051). On a world on GLO-90, a reading of the column's own height (Earth's
/// <c>ForestPotential</c> with local alpine ecology) decodes that place's GLO-90 tile. A map of the whole world samples
/// everywhere, so with the whole store on the machine it decoded tiles all over the globe: the climate raster took 116 s
/// against about 10 s on built-in heights. Such a map's samples ask for the built-in heights instead.
/// <para>The switch is set around each sample and the old value put back after it: the climate raster runs on shared
/// pool threads, and the same threads also sample nearby terrain (far terrain's biome tiles), which must read GLO-90.
/// Earth recognises the world chart's own threads by name ("Wmap world chart"). Never for anything that must match the
/// terrain as generated.</para>
/// </summary>
public static class EarthHeights
{
    /// <summary><paramref name="sample"/> on Earth's built-in heights, or null where <paramref name="earth"/> has no such
    /// switch (no Earth, or Earth before 1.0.3).</summary>
    public static Func<double, double, T> OnBuiltInHeights<T>(Assembly earth, Func<double, double, T> sample)
    {
        var property = earth?.GetType("EarthMap.Core.FineElevation")?.GetProperty("BuiltInOnly", BindingFlags.Public | BindingFlags.Static);
        if (sample == null || property?.PropertyType != typeof(bool) || property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true) return null;
        var get = property.GetMethod.CreateDelegate<Func<bool>>();
        var set = property.SetMethod.CreateDelegate<Action<bool>>();
        return (x, z) =>
        {
            bool was = get();
            set(true);
            try { return sample(x, z); }
            finally { set(was); }
        };
    }
}
