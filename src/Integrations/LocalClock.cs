using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Wmap.Core;

namespace Wmap.Integrations;

/// <summary>The hour the coordinates HUD shows beside the coordinates (WM-0010, 0.5.3), from the first of:
/// <list type="number">
/// <item>Astra Terra (for Earth, or the original): the observer longitude its sky turns by
/// (<c>AstraTerra.Astronomy.ObserverLongitude.ForObserver</c>, zero while its server keeps one sun for the whole world),
/// put through its own local solar time formula (<see cref="LocalTime.SolarHours"/>), so the clock agrees with the sun
/// it draws. Where its player has told the character panel to keep the hour back (its <c>CalendarDisplay</c>), no hour.</item>
/// <item>Earth: local solar time at the sky meridian of the player's X (<c>EarthMap.EarthMapSystem.LongitudeAt</c>).</item>
/// <item>A world with geography (Cylinder World's planet layout): local solar time at the map's longitude.</item>
/// <item>Otherwise the game's own hour of day.</item>
/// </list>
/// All by reflection (WM-D04): World Map references none of these mods.</summary>
public sealed class LocalClock
{
    public const string AstraTerraSource = "astraterra", EarthSource = "earth", GeographySource = "geography", VanillaSource = "vanilla";

    readonly ICoreAPI api;
    bool resolved;
    global::System.Func<double, IWorldAccessor, double> astraLongitude;
    global::System.Func<bool> astraShowsHour;
    global::System.Func<double, double?> earthLongitude;

    public LocalClock(ICoreAPI api) { this.api = api; }

    /// <summary>Where the last <see cref="Read"/> took its hour from (one of the <c>…Source</c> names).</summary>
    public string Source { get; private set; } = VanillaSource;

    /// <summary>Whether the last <see cref="Read"/>'s hour is the world's one clock: the game's hour of day, or Astra
    /// Terra's sky at longitude 0, which it gives everywhere while its server keeps one sun for the whole world.</summary>
    public bool OneClock { get; private set; } = true;

    /// <summary>The world's universal clock (WM-0016): the solar time at longitude 0, by Astra Terra's formula
    /// (<c>CelestialMath.GetUniversalSolarTimeHours</c>: total days × hours per day). It is the game's hour of day, except
    /// that it follows the sky's <c>timelapse</c> offset as Astra Terra and the sun do.</summary>
    public double? Universal()
    {
        var calendar = api.World?.Calendar;
        return calendar == null ? null : LocalTime.UniversalHours(calendar.TotalDays, Math.Max(1.0, calendar.HoursPerDay));
    }

    /// <summary>The hour (0..hours per day) at world <paramref name="x"/>, <paramref name="z"/>, or null where it is kept back.
    /// <paramref name="x"/> as the entity has it: Astra Terra and Earth are asked with the X their sky is given.</summary>
    public double? Read(double x, double z, global::System.Func<double, double, GeoPosition> geography, global::System.Func<double, double> canonical)
    {
        var calendar = api.World?.Calendar;
        if (calendar == null) return null;
        Resolve();
        double hoursPerDay = Math.Max(1.0, calendar.HoursPerDay), totalDays = calendar.TotalDays;
        if (astraLongitude != null)
        {
            try
            {
                if (astraShowsHour != null && !astraShowsHour()) { Source = AstraTerraSource; return null; }
                double longitude = astraLongitude(x, api.World);
                Source = AstraTerraSource;
                OneClock = longitude == 0;
                return LocalTime.SolarHours(totalDays, hoursPerDay, longitude);
            }
            catch (Exception e) { Drop(ref astraLongitude, "Astra Terra", e); astraShowsHour = null; }
        }
        if (earthLongitude != null)
        {
            try
            {
                if (earthLongitude(x) is double longitude) { Source = EarthSource; OneClock = false; return LocalTime.SolarHours(totalDays, hoursPerDay, longitude); }
            }
            catch (Exception e) { Drop(ref earthLongitude, "Earth", e); }
        }
        if (geography != null)
        {
            var p = geography(canonical(x), z);
            if (!double.IsNaN(p.Longitude)) { Source = GeographySource; OneClock = false; return LocalTime.SolarHours(totalDays, hoursPerDay, p.Longitude); }
        }
        Source = VanillaSource;
        OneClock = true;
        return calendar.HourOfDay;
    }

    void Drop<T>(ref T source, string mod, Exception e) where T : class
    {
        source = null;
        api.Logger?.Warning("[wmap] The HUD clock stops asking {0} for the hour: {1}", mod, (e as TargetInvocationException)?.InnerException?.Message ?? e.Message);
    }

    /// <summary>Once per session, after the mods have started (their systems do not change in a session).</summary>
    void Resolve()
    {
        if (resolved) return;
        resolved = true;
        try
        {
            var astra = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "AstraTerra.AstraTerraModSystem")?.GetType().Assembly;
            var observer = astra?.GetType("AstraTerra.Astronomy.ObserverLongitude")?.GetMethod("ForObserver", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(double), typeof(IWorldAccessor) }, null);
            if (observer != null && observer.ReturnType == typeof(double))
            {
                astraLongitude = (global::System.Func<double, IWorldAccessor, double>)observer.CreateDelegate(typeof(global::System.Func<double, IWorldAccessor, double>));
                var display = astra.GetType("AstraTerra.Client.Calendar.VanillaCalendarHooks")?.GetProperty("Display", BindingFlags.Public | BindingFlags.Static);
                var shows = display == null ? null : astra.GetType("AstraTerra.Config.CalendarDisplayParser")?.GetMethod("ShowsHour", BindingFlags.Public | BindingFlags.Static, null, new[] { display.PropertyType }, null);
                if (shows != null && shows.ReturnType == typeof(bool)) astraShowsHour = () => (bool)shows.Invoke(null, new[] { display.GetValue(null) });
            }
        }
        catch (Exception e) { astraLongitude = null; astraShowsHour = null; api.Logger?.Warning("[wmap] Astra Terra's sky longitude was not found for the HUD clock: {0}", e.Message); }
        try
        {
            var earth = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthMapSystem");
            var at = earth?.GetType().GetMethod("LongitudeAt", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(double) }, null);
            if (at != null && at.ReturnType == typeof(double?)) earthLongitude = (global::System.Func<double, double?>)at.CreateDelegate(typeof(global::System.Func<double, double?>), earth);
        }
        catch (Exception e) { earthLongitude = null; api.Logger?.Warning("[wmap] Earth's sky longitude was not found for the HUD clock: {0}", e.Message); }
    }
}
