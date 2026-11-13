using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;

namespace Wmap.Integrations;

/// <summary>Earth's air pressure for the Jonas barometer (WM-0006), read without a compile-time dependency (WM-D04).
/// Earth's weather is a pure function of place and game time on every side (its decision 0017):
/// <c>EarthMap.EarthWeather.Model.Sample(x, z, totalHours).Pressure</c>, hPa at sea level, low under storms.</summary>
public sealed class EarthPressure
{
    /// <summary>Hours back the tendency compares with, as a barometer's three-hour tendency.</summary>
    public const double TendencyHours = 3;
    /// <summary>A change smaller than this (hPa over <see cref="TendencyHours"/>) reads as steady.</summary>
    public const double SteadyHpa = 1;

    readonly object system;
    readonly PropertyInfo modelProperty;
    object model;
    MethodInfo sample;
    FieldInfo pressure;

    EarthPressure(object system, PropertyInfo modelProperty) { this.system = system; this.modelProperty = modelProperty; }

    /// <summary>Earth's weather on this side, or null without Earth.</summary>
    public static EarthPressure Find(ICoreAPI api)
    {
        object system = api?.ModLoader?.Systems?.FirstOrDefault(s => s?.GetType().FullName == "EarthMap.EarthWeather");
        var property = system?.GetType().GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);
        return property == null ? null : new EarthPressure(system, property);
    }

    /// <summary>Sea-level pressure in hPa at a place and game time; null while Earth's weather is off or not ready.</summary>
    public double? At(double x, double z, double hours)
    {
        try
        {
            object m = modelProperty.GetValue(system);
            if (m == null) return null;
            if (m != model)
            {
                model = m;
                sample = m.GetType().GetMethod("Sample", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(double), typeof(double), typeof(double) }, null);
                pressure = sample?.ReturnType.GetField("Pressure", BindingFlags.Public | BindingFlags.Instance);
                if (pressure?.FieldType != typeof(double)) { sample = null; pressure = null; }
            }
            if (sample == null) return null;
            double hpa = (double)pressure.GetValue(sample.Invoke(m, new object[] { x, z, hours }));
            return double.IsFinite(hpa) && hpa > 0 ? hpa : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"1004 hPa, falling": the pressure now and its three-hour tendency; null without a reading.</summary>
    public string Reading(double x, double z, double hours)
    {
        if (At(x, z, hours) is not double now) return null;
        string text = $"{now:0} hPa";
        return At(x, z, hours - TendencyHours) is double before ? text + ", " + Tendency(now - before) : text;
    }

    public static string Tendency(double change) => Math.Abs(change) < SteadyHpa ? "steady" : change > 0 ? "rising" : "falling";
}
