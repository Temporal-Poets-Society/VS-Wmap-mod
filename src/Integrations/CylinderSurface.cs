using System;
using System.Linq.Expressions;
using System.Reflection;
using Wmap.Core;

namespace Wmap.Integrations;

/// <summary>Projects Cylinder World's deterministic finishing passes onto a terrain column.
/// Geometry/noise come from the installed mod's saved SeamParams, never a Wmap pole layout.</summary>
public sealed class CylinderSurface
{
    public readonly record struct PolarColumn(int Floor, double ShelfBlend, int IceTop, int IceBottom, bool Floe);
    readonly Func<double, bool> inSeam;
    readonly Func<double, double> seamBlend, edgeDistance, jumpDistance, edgeX;
    readonly Func<double, double, bool> polarOwns;
    readonly Func<double, double, int> planeCap;
    readonly Func<double, int, double, bool> wallBlock;
    readonly Func<double, double, PolarColumn> polar;
    // Cylinder 0.11.5+: the band's final ground in any layout (v10 relief, wavy edge and ramp past H); null before.
    readonly Func<double, double, int, int> seamGround;
    readonly Func<double, double, int> seamFloor;
    readonly double seamReach;
    readonly int version, sea, seabed, wallTop, wallFaceMax, wallDepth, polarReach, iceCover, curtainHalfWidth;
    readonly bool walls, flattenSeam;
    readonly bool earthPolar;
    readonly Func<double, double> earthLatitude;
    readonly int lifelessBorder;
    readonly Func<double, double, int> earthIceRise, earthSnow;
    readonly int earthIceCap;

    static int Number(object p, string name) => Convert.ToInt32(WorldSource.Member(p, name));
    static T Bind<T>(object p, string name) where T : Delegate => (T)p.GetType().GetMethod(name).CreateDelegate(typeof(T), p);
    /// <param name="flattenSeam">Cylinder World's FlattenSeabed setting: without it the seam band keeps the generated terrain.</param>
    public CylinderSurface(object parameters, int worldHeight, int seaLevel, bool flattenSeam = true)
    {
        this.flattenSeam = flattenSeam;
        // Saved geometry may be replaced by a mod command. Capture this revision for the tile cache.
        object p = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(parameters, null);
        sea = seaLevel;
        version = Number(p, "Version"); seabed = Number(p, "SeabedY");
        wallDepth = Number(p, "WallDepth"); wallTop = Math.Min(Number(p, "WallTopY"), worldHeight - 2);
        wallFaceMax = Number(p, "WallFaceMax"); polarReach = Number(p, "PolarZoneDepth");
        walls = WorldSource.Member(p, "HasWalls") is true;
        iceCover = Convert.ToInt32(p.GetType().GetField("IceCover")?.GetRawConstantValue() ?? 3);
        curtainHalfWidth = Convert.ToInt32(p.GetType().GetField("CurtainHalfWidth")?.GetRawConstantValue() ?? 2);
        inSeam = Bind<Func<double, bool>>(p, "InSeamBand");
        seamBlend = Bind<Func<double, double>>(p, "SeamBlendWeight");
        if (p.GetType().GetMethod("SeamGround", new[] { typeof(double), typeof(double), typeof(int) }) != null && p.GetType().GetProperty("SeamWorkReach") != null)
        {
            seamGround = Bind<Func<double, double, int, int>>(p, "SeamGround");
            seamFloor = Bind<Func<double, double, int>>(p, "SeamFloorY");
            edgeX = Bind<Func<double, double>>(p, "DistToEdge");
            seamReach = Convert.ToDouble(WorldSource.Member(p, "SeamWorkReach"));
        }
        jumpDistance = Bind<Func<double, double>>(p, "DistToJumpLine");
        edgeDistance = Bind<Func<double, double>>(p, "DistToZEdge");
        polarOwns = Bind<Func<double, double, bool>>(p, "PolarOwnsSeabed");
        planeCap = Bind<Func<double, double, int>>(p, "PlaneCap");
        wallBlock = Bind<Func<double, int, double, bool>>(p, "IsWallBlock");
        var method = p.GetType().GetMethod("GetPolarColumn");
        var x = Expression.Parameter(typeof(double)); var z = Expression.Parameter(typeof(double));
        var col = Expression.Variable(method.ReturnType);
        var body = Expression.Block(new[] { col }, Expression.Assign(col, Expression.Call(Expression.Constant(p), method, x, z)),
            Expression.New(typeof(PolarColumn).GetConstructors()[0], Expression.Field(col, "Floor"), Expression.Field(col, "ShelfBlend"),
                Expression.Field(col, "IceTop"), Expression.Field(col, "IceBottom"), Expression.Field(col, "Floe")));
        polar = Expression.Lambda<Func<double, double, PolarColumn>>(body, x, z).Compile();
        earthPolar = WorldSource.Member(p, "EarthPolarIce") is true;
        if (earthPolar)
        {
            object earth = WorldSource.Member(p, "EarthPolar") ?? throw new InvalidOperationException("Cylinder Earth polar model is not ready");
            earthLatitude = Bind<Func<double, double>>(earth, "Latitude");
            lifelessBorder = Convert.ToInt32(earth.GetType().GetField("LifelessBorder").GetRawConstantValue());
            earthIceCap = worldHeight - 5;
            var sample = earth.GetType().GetMethod("Sample");
            var earthColumn = Expression.Variable(sample.ReturnType);
            var floor = typeof(Math).GetMethod(nameof(Math.Floor), new[] { typeof(double) });
            var wx = Expression.Add(Expression.Call(floor, x), Expression.Constant(.5));
            var wz = Expression.Add(Expression.Call(floor, z), Expression.Constant(.5));
            var rises = p.GetType().GetMethod("EarthIceRise");
            var freeze = Expression.Call(Expression.Constant(p), p.GetType().GetMethod("HasArcticIce"), wx, wz, earthColumn);
            Expression rise = rises == null ? Expression.Constant(0) : Expression.Call(Expression.Constant(p), rises, wx, wz, earthColumn);
            earthIceRise = Expression.Lambda<Func<double, double, int>>(Expression.Block(new[] { earthColumn },
                // Cylinder 0.10.1 matches Earth's integer terrain samples at coastlines.
                Expression.Assign(earthColumn, Expression.Call(Expression.Constant(earth), sample, Expression.Call(floor, x), Expression.Call(floor, z))),
                Expression.Condition(freeze, rise, Expression.Constant(-1))), x, z).Compile();
            if (p.GetType().GetMethod("EarthSnowEighths") != null) earthSnow = Bind<Func<double, double, int>>(p, "EarthSnowEighths");
        }
    }
    /// <summary>Applies the finishing passes to a terrain sampler. Cylinder's functions are pure; the result is as thread-safe as terrain.</summary>
    public Func<double, double, MapSample> Bind(Func<double, double, MapSample> terrain) => (x, z) => Sample(terrain, x, z);
    public MapSample Sample(Func<double, double, MapSample> terrain, double x, double z)
    {
        // Earth's base pass samples integer columns; Cylinder's finishing pass samples their centers.
        double bx = Math.Floor(x), bz = Math.Floor(z), wx = bx + .5, wz = bz + .5;
        var original = terrain(bx, bz);
        int ground = original.Height;
        int waterTop = Semantic.IsWater(original.Kind) ? original.Height + original.Depth : -1;
        byte kind = original.Kind;
        bool seam = inSeam(wx);
        if (seamGround != null)
        {
            // v10's ramp reaches past the band (SeamWorkReach > H). Like Cylinder, keep a ramp column it would not move as it is.
            int shaped = flattenSeam && edgeX(wx) < seamReach && !polarOwns(wx, wz) ? seamGround(wx, wz, ground) : ground;
            if (shaped != ground)
            {
                ground = shaped;
                waterTop = ground < sea - 1 ? sea - 1 : -1;
                kind = 1;
            }
        }
        else if (flattenSeam && seam && !polarOwns(wx, wz))
        {
            double blend = jumpDistance(wx) <= curtainHalfWidth ? 0 : seamBlend(wx);
            ground = (int)Math.Round(seabed + (ground - seabed) * blend);
            waterTop = ground < sea - 1 ? sea - 1 : -1;
            kind = 1;
        }
        double dz = edgeDistance(wz);
        if (!walls) return Finish(ground, waterTop, kind);
        if (dz < wallDepth) return new(wallTop, 0, 4);
        if (earthPolar)
        {
            // Earth mode has Arctic ice well beyond the old generated-sheet strip.
            // It preserves Antarctica and coats the last kilometre without raising land.
            if (dz < wallFaceMax)
            {
                for (int y = wallTop; y >= Math.Max(1, ground); y--)
                    if (wallBlock(wx, y, wz)) { ground = y; kind = 4; break; }
            }
            else
            {
                if (earthLatitude(wz) >= 66 && waterTop == sea - 1 && ground < waterTop)
                {
                    int rise = earthIceRise(bx, bz);
                    if (rise >= 0)
                    {
                        ground = Math.Min(earthIceCap, waterTop + rise) + (earthSnow?.Invoke(wx, wz) ?? 0) / 8;
                        kind = 3;
                    }
                }
                if (dz - wallDepth <= lifelessBorder && ground >= sea - 1) kind = 3;
            }
            return Finish(ground, waterTop, kind);
        }
        if (dz >= polarReach) return Finish(ground, waterTop, kind);

        var col = polar(wx, wz);
        int iceTop = Math.Min(col.IceTop, wallTop), iceBottom = col.IceBottom;
        bool exactFloor = version >= 5 && col.Floor > 0 && seam;
        int floor = -1;
        if (col.Floor > 0)
        {
            int baseFloor = exactFloor ? seamFloor?.Invoke(wx, wz) ?? seabed : Math.Min(ground, col.Floor);
            if (exactFloor || baseFloor < col.Floor) floor = baseFloor + (int)Math.Round((col.Floor - baseFloor) * col.ShelfBlend);
            if (exactFloor) { ground = floor; waterTop = ground < sea - 1 ? sea - 1 : -1; kind = 1; }
            else if (floor > ground) { ground = floor; kind = 1; }
        }
        int rawCap = version >= 9 && !exactFloor && dz >= wallFaceMax ? planeCap(wx, wz) : int.MaxValue;
        if (rawCap != int.MaxValue)
        {
            int cap = Math.Min(Math.Max(rawCap, col.IceTop), wallTop);
            int capped = Math.Min(ground, cap - iceCover);
            if (col.IceTop >= 0 || ground > capped)
            {
                int mantle = Math.Min(capped + iceCover, cap);
                if (mantle > iceTop) { if (iceTop < 0) iceBottom = capped + 1; iceTop = mantle; }
            }
            ground = capped;
        }
        if (iceTop >= iceBottom && iceTop >= ground)
        {
            ground = iceTop; kind = 3;
        }
        if (dz < wallFaceMax)
            for (int y = wallTop; y >= Math.Max(1, ground); y--)
                if (wallBlock(wx, y, wz)) { ground = y; kind = 4; break; }
        if (col.Floe && waterTop == sea - 1 && ground < waterTop) { ground = waterTop; kind = 3; }
        return Finish(ground, waterTop, kind);
    }
    static MapSample Finish(int ground, int waterTop, byte kind) => waterTop > ground
        ? new(ground, waterTop - ground, kind == TileData.River ? TileData.River : TileData.Water)
        : new(ground, 0, Semantic.IsWater(kind) ? TileData.Land : kind);
}
