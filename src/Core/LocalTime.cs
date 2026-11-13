using System;
using System.Globalization;

namespace Wmap.Core;

/// <summary>The local time the coordinates HUD shows (WM-0010, 0.5.3). Local apparent solar time at a longitude, by the
/// formula Astra Terra for Earth turns its sky with (<c>CelestialMath.GetLocalSolarTimeHours</c>): the world's one
/// universal clock moved by the longitude's share of a day, so one trip round the world is one whole world day, also on
/// worlds whose day is not 24 hours. Kept identical to it so the clock and the sun agree.</summary>
public static class LocalTime
{
    /// <summary>The hour of the world's universal clock (0..<paramref name="hoursPerDay"/>), the game's own hour of day.</summary>
    public static double UniversalHours(double totalDays, double hoursPerDay) => Wrap(totalDays * hoursPerDay, hoursPerDay);

    /// <summary>Local solar hour at <paramref name="longitude"/> degrees east.</summary>
    public static double SolarHours(double totalDays, double hoursPerDay, double longitude) =>
        Wrap(UniversalHours(totalDays, hoursPerDay) + longitude / 360.0 * hoursPerDay, hoursPerDay);

    /// <summary>The box's clocks (WM-0016): the local time ("16:46 local") and the world's universal clock, the solar
    /// time at longitude 0 that Astra Terra calls universal and the box labels UTC ("12:15 UTC"). Where the two are one
    /// clock (<paramref name="oneClock"/>: no longitude, or a sky that keeps one sun for the whole world) only the first,
    /// unlabelled ("12:15"), and no second.</summary>
    public static (string Local, string Universal) Clocks(double local, double universal, bool oneClock, double hoursPerDay = 24) =>
        oneClock ? (Format(universal, hoursPerDay), null) : (Format(local, hoursPerDay) + " local", Format(universal, hoursPerDay) + " UTC");

    /// <summary>"HH:MM" on a 24-hour face, minutes cut rather than rounded, as the game's character panel and Astra
    /// Terra's clock print it (so 13:59.9 is 13:59, never 13:60).</summary>
    public static string Format(double hours, double hoursPerDay = 24)
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours)) return "--:--";
        hours = Wrap(hours, Math.Max(1, hoursPerDay));
        // Cut to whole minutes, after a millionth of a minute for rounding: total days times hours per day gives
        // 15.999999999999998 at 16:00, which must not read 15:59 (WM-0016).
        long minutes = (long)Math.Floor(hours * 60 + 1e-6);
        int hour = (int)(minutes / 60), minute = (int)(minutes % 60);
        if (hour >= hoursPerDay) hour = 0;
        return hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
    }

    static double Wrap(double value, double period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period), period, "Hours per day must be positive.");
        double r = value % period;
        return r < 0 ? r + period : r;
    }
}
