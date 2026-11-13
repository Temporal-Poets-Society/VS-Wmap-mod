using System;

namespace Wmap.Core;

/// <summary>Map marker headings (radians, 0 = north/up, clockwise), as map.frag's marker() draws them.</summary>
public static class Heading
{
    /// <summary>
    /// A player's marker: a player looks towards (sin yaw, cos yaw) in world X/Z (a player's level pitch is π, and
    /// EntityPos.GetViewVector(π, yaw) = (sin yaw, 0, cos yaw)); vanilla's PlayerMapLayer turns its arrow by
    /// RotateZ(π - yaw). Till Wmap 0.3.3 this was -yaw, which pointed 180° off.
    /// </summary>
    public static double OfPlayer(double yaw) => Math.PI - yaw;

    /// <summary>Where a marker of heading h points on the map, in world X/Z (+Z is south, down the screen).</summary>
    public static (double X, double Z) Tip(double h) => (Math.Sin(h), -Math.Cos(h));

    /// <summary>A player's compass bearing in whole degrees, 0..359 (0 north, 90 east).</summary>
    public static int Bearing(double yaw) => (int)Math.Round(Normal(OfPlayer(yaw) * 180 / Math.PI)) % 360;

    /// <summary>Degrees in 0..360.</summary>
    public static double Normal(double degrees) => (degrees % 360 + 360) % 360;

    static readonly string[] Points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    /// <summary>The nearest of the sixteen compass points to a bearing in degrees.</summary>
    public static string Point(double degrees) => Points[(int)Math.Round(Normal(degrees) / 22.5) % 16];
}
