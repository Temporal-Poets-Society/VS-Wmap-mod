namespace EarthMap.Core
{
    /// <summary>Stands in for Earth's per-thread switch to its built-in heights (Earth 1.0.3, decision 0051), for
    /// <c>Wmap.Integrations.EarthHeights</c>'s checks.</summary>
    public static class FineElevation
    {
        [ThreadStatic] static bool builtInOnly;
        public static bool BuiltInOnly { get => builtInOnly; set => builtInOnly = value; }
    }
}
