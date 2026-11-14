using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Optional sea colour from the Sea mod, read by reflection: a member <c>FarWaterColor</c> on <c>Sea.SeaSystem</c>
/// (field, property or parameterless method) returning RGB 0..1 as float[] / Vec3f / Vec4f, or an int 0xRRGGBB.
/// Sea does not provide one yet; without it the atlas's sea colours are used.
/// </summary>
public sealed class SeaWater
{
    readonly object system;
    readonly MemberInfo member;
    float[] color;
    long readAt;

    public SeaWater(ICoreClientAPI api)
    {
        system = api.ModLoader?.Systems?.FirstOrDefault(s => s?.GetType().FullName == "Sea.SeaSystem");
        var type = system?.GetType();
        member = (MemberInfo)type?.GetField("FarWaterColor") ?? (MemberInfo)type?.GetProperty("FarWaterColor") ?? type?.GetMethod("FarWaterColor", Type.EmptyTypes);
    }

    /// <summary>RGB, or null when Sea does not supply a colour. Refreshed every few seconds.</summary>
    public float[] Color
    {
        get
        {
            if (member == null || Environment.TickCount64 - readAt < 5000) return color;
            readAt = Environment.TickCount64;
            try
            {
                object v = member switch
                {
                    FieldInfo f => f.GetValue(system),
                    PropertyInfo p => p.GetValue(system),
                    MethodInfo m => m.Invoke(system, null),
                    _ => null
                };
                color = Parse(v);
            }
            catch (Exception) { color = null; }
            return color;
        }
    }

    public static float[] Parse(object v) => v switch
    {
        float[] a when a.Length >= 3 => new[] { a[0], a[1], a[2] },
        double[] a when a.Length >= 3 => new[] { (float)a[0], (float)a[1], (float)a[2] },
        Vec3f a => new[] { a.X, a.Y, a.Z },
        Vec4f a => new[] { a.R, a.G, a.B },
        int rgb => new[] { (rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f },
        _ => null
    };
}
