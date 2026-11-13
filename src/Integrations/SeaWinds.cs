using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common;
using Func3 = System.Func<double, double, double, (double X, double Z)>;

namespace Wmap.Integrations;

/// <summary>
/// The Sea mod's wind model, read without a compile-time dependency. Sea computes the wind as a pure function of
/// position and game time on every side, so the atlas can sample it for any month without network traffic.
/// Winds are in vanilla units toward (X, Z): +X east, +Z south; 0.15 light breeze, 0.3 medium, 0.6 strong, 1 storm.
/// </summary>
public sealed class SeaWinds
{
    readonly object system;
    readonly PropertyInfo modelProperty;
    readonly MethodInfo beaufortName;
    object model;
    Func3 prevailing, weather;
    System.Func<double, double, string> beltName;

    SeaWinds(object system)
    {
        this.system = system;
        modelProperty = system.GetType().GetProperty("Model");
        beaufortName = system.GetType().Assembly.GetType("Sea.Beaufort")?.GetMethod("Name", new[] { typeof(double) });
    }

    /// <summary>Sea's mod system on this side, or null when Sea is not installed.</summary>
    public static SeaWinds Find(ICoreAPI api)
    {
        object system = api?.ModLoader?.Systems?.FirstOrDefault(s => s?.GetType().FullName == "Sea.SeaSystem");
        return system?.GetType().GetProperty("Model") == null ? null : new SeaWinds(system);
    }

    /// <summary>The model instance, rebinding when Sea replaces it (clients get theirs from the server's parameters).</summary>
    public object Model
    {
        get
        {
            object m = modelProperty.GetValue(system);
            if (m != model)
            {
                model = m;
                prevailing = m == null ? null : Bind(m, "Prevailing", typeof(double), typeof(double), typeof(double));
                weather = m == null ? null : Bind(m, "At", typeof(double), typeof(double), typeof(double), typeof(bool));
                var belt = m?.GetType().GetMethod("BeltName", new[] { typeof(double), typeof(double) });
                beltName = belt == null ? null : (System.Func<double, double, string>)belt.CreateDelegate(typeof(System.Func<double, double, string>), m);
            }
            return m;
        }
    }

    public bool Ready => Model != null && prevailing != null;

    /// <summary>"Earthlike", "Steady" or "Off": with "Off" Sea leaves the wind to vanilla, which has no direction.</summary>
    public string Mode => WorldSource.Member(Model, "Mode")?.ToString() ?? "";

    public double DaysPerYear => Shape("DaysPerYear", 108);
    public double HoursPerDay => Shape("HoursPerDay", 24);
    double Shape(string name, double fallback) => WorldSource.Member(Model, "Shape") is { } s && WorldSource.Member(s, name) is double v && v > 0 ? v : fallback;

    /// <summary>The prevailing (climatological) wind: Sea's belts by latitude and season, or a map mod's own source.</summary>
    public (double X, double Z) Prevailing(double x, double z, double hours) => Ready ? prevailing(x, z, hours) : default;

    /// <summary>The weather at that time: prevailing wind, drift and travelling storms, without gusts.</summary>
    public (double X, double Z) Weather(double x, double z, double hours) => Ready && weather != null ? weather(x, z, hours) : Prevailing(x, z, hours);

    /// <summary>The current model's prevailing or weather function, captured for a background thread.</summary>
    public Func3 Sampler(bool includeWeather) => Ready ? (includeWeather ? weather ?? prevailing : prevailing) : null;

    public string BeltName(double z, double hours) => Ready ? beltName?.Invoke(z, hours) : null;

    public string Beaufort(double strength)
    {
        try { return beaufortName?.Invoke(null, new object[] { strength }) as string; }
        catch (Exception) { return null; }
    }

    /// <summary>Compiles model.method(x, z, hours[, false]) returning Sea's Wind into a tuple of its X and Z.</summary>
    static Func3 Bind(object m, string name, params Type[] signature)
    {
        var method = m.GetType().GetMethod(name, signature);
        if (method == null) return null;
        ParameterExpression x = Expression.Parameter(typeof(double)), z = Expression.Parameter(typeof(double)), h = Expression.Parameter(typeof(double));
        var args = signature.Length == 4 ? new Expression[] { x, z, h, Expression.Constant(false) } : new Expression[] { x, z, h };
        var wind = Expression.Variable(method.ReturnType);
        var body = Expression.Block(new[] { wind }, Expression.Assign(wind, Expression.Call(Expression.Constant(m), method, args)),
            Expression.New(typeof((double, double)).GetConstructor(new[] { typeof(double), typeof(double) }), Expression.Field(wind, "X"), Expression.Field(wind, "Z")));
        return Expression.Lambda<Func3>(body, x, z, h).Compile();
    }
}
