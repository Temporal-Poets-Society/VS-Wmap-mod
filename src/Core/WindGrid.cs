using System;
using System.Threading;

namespace Wmap.Core;

/// <summary>
/// The wind layer: which winds to show, and the world-wide grid of wind vectors the map shader draws arrows from.
/// Winds come from Sea's model (vanilla units toward +X east, +Z south).
/// </summary>
public static class WindGrid
{
    public const int Width = 512, Height = 256;

    /// <summary>Layer choices: off, the weather now, the prevailing wind now, then the prevailing wind of each month.</summary>
    public const int Off = 0, WeatherNow = 1, PrevailingNow = 2, FirstMonth = 3, Choices = FirstMonth + 12;

    static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    public static string Name(int choice) => choice switch
    {
        WeatherNow => "weather now",
        PrevailingNow => "prevailing now",
        >= FirstMonth and < Choices => Months[choice - FirstMonth],
        _ => "off",
    };

    public static bool IsWeather(int choice) => choice == WeatherNow;

    /// <summary>For now a barometer grants all worldwide wind layers, including every month's prevailing winds.</summary>
    public static bool Visible(int choice, bool development, bool hasInstrument) =>
        choice > Off && choice < Choices && (development || hasInstrument);

    /// <summary>
    /// Game hours to evaluate a choice at: now, or the middle of that month in the current year
    /// (the game calendar has twelve equal months; Sea's seasons follow the fraction of the year).
    /// </summary>
    public static double Hours(int choice, double now, double daysPerYear, double hoursPerDay)
    {
        if (choice < FirstMonth || choice >= Choices) return now;
        double yearStart = Math.Floor(now / hoursPerDay / daysPerYear) * daysPerYear;
        return (yearStart + (choice - FirstMonth + 0.5) / 12 * daysPerYear) * hoursPerDay;
    }

    /// <summary>RG32F texels: the wind (east, south) at each texel centre over the whole world.</summary>
    public static float[] Build(Func<double, double, (double X, double Z)> wind, Topology t, CancellationToken stop)
    {
        var data = new float[Width * Height * 2];
        for (int z = 0; z < Height; z++)
        {
            stop.ThrowIfCancellationRequested();
            double wz = (z + 0.5) / Height * t.Height;
            for (int x = 0; x < Width; x++)
            {
                var w = wind(t.StartX + (x + 0.5) / Width * t.Width, wz);
                int i = (z * Width + x) * 2;
                data[i] = (float)w.X; data[i + 1] = (float)w.Z;
            }
        }
        return data;
    }

    /// <summary>Texels per side of the barometer's local field.</summary>
    public const int LocalSize = 96;

    /// <summary>
    /// RG32F texels: the wind (east, south) over the square [x0, x0 + size) × [z0, z0 + size), rows running south;
    /// x wraps round a ring, places beyond the poles are calm.
    /// </summary>
    public static float[] BuildLocal(Func<double, double, (double X, double Z)> wind, Topology t, double x0, double z0, double size, CancellationToken stop)
    {
        var data = new float[LocalSize * LocalSize * 2];
        for (int z = 0; z < LocalSize; z++)
        {
            stop.ThrowIfCancellationRequested();
            double wz = z0 + (z + 0.5) / LocalSize * size;
            if (wz < 0 || wz >= t.Height) continue;
            for (int x = 0; x < LocalSize; x++)
            {
                var w = wind(t.Canonical(x0 + (x + 0.5) / LocalSize * size), wz);
                int i = (z * LocalSize + x) * 2;
                data[i] = (float)w.X; data[i + 1] = (float)w.Z;
            }
        }
        return data;
    }

    /// <summary>Knots for a wind strength in vanilla units (Sea: one unit is 25 m/s).</summary>
    public static double Knots(double strength) => strength * 25 * 1.943844;

    static readonly string[] Points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    /// <summary>The compass point a wind blows from (north is -Z).</summary>
    public static string From(double x, double z)
    {
        double bearing = Math.Atan2(-x, z) * 180 / Math.PI;
        return Points[(int)Math.Round(Topology.Mod(bearing, 360) / 22.5) % 16];
    }

    /// <summary>The compass point of a direction toward (x, z) (north is -Z).</summary>
    public static string Toward(double x, double z) => From(-x, -z);
}
