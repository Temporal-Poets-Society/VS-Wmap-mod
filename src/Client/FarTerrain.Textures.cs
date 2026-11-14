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

// Far terrain's colour textures from the game's assets: the climate tints, the grass seasons and their altitude row (part of FarTerrain).
public sealed partial class FarTerrain
{
    /// <summary>One of the game's climate colour maps as a GL texture, or 0.</summary>
    int LoadTint(string path)
    {
        try
        {
            var asset = api.Assets.TryGet(new AssetLocation("game", path));
            if (asset?.Data == null) return 0;
            using var bitmap = api.Render.BitmapCreateFromPng(asset.Data);
            using var unpack = new GlUnpack();
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, bitmap.Width, bitmap.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, bitmap.Pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return id;
        }
        catch (Exception e)
        {
            api.Logger.Warning("[wmap] Could not load the game's {0}; far terrain keeps the atlas colours: {1}", path, e.Message);
            return 0;
        }
    }

    /// <summary>The season maps' rows over the year (<see cref="GrassSeasons"/>): Earth's grass maps where Earth has them, else
    /// the game's grass map in their rows; 0 without the game's grass map (far grass then takes the climate tint alone).</summary>
    int LoadGrassSeasons()
    {
        try
        {
            var strip = new byte[GrassSeasons.Rows * GrassSeasons.Columns * 4];
            bool Row(int row, string domain, string path)
            {
                var asset = api.Assets.TryGet(new AssetLocation(domain, path));
                if (asset?.Data == null) return false;
                using var bitmap = api.Render.BitmapCreateFromPng(asset.Data);
                GrassSeasons.Row(strip, row, bitmap.Pixels, bitmap.Width, bitmap.Height);
                return true;
            }
            if (!Row(GrassSeasons.VanillaGrass, "game", GrassSeasons.VanillaMaps[0])) return 0;
            if (!Row(GrassSeasons.Oak, "game", GrassSeasons.VanillaMaps[1])) GrassSeasons.CopyRow(strip, GrassSeasons.VanillaGrass, GrassSeasons.Oak);
            if (!Row(GrassSeasons.Needles, "game", GrassSeasons.VanillaMaps[2])) GrassSeasons.CopyRow(strip, GrassSeasons.Oak, GrassSeasons.Needles);
            int earth = 0;
            for (int i = 0; i < GrassSeasons.EarthMaps.Length; i++)
            {
                if (Row(i, "earth", "textures/environment/seasons/earthgrass-" + GrassSeasons.EarthMaps[i] + ".png")) earth++;
                else GrassSeasons.CopyRow(strip, GrassSeasons.VanillaGrass, i);
            }
            seasonStrip = strip;
            using var unpack = new GlUnpack();
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, GrassSeasons.Columns, GrassSeasons.Rows, 0, PixelFormat.Rgba, PixelType.UnsignedByte, strip);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            api.Logger.Notification("[wmap] Far grass and leaves follow the seasons ({0} of Earth's {1} grass season maps).", earth, GrassSeasons.EarthMaps.Length);
            return id;
        }
        catch (Exception e)
        {
            api.Logger.Warning("[wmap] Could not load the season colour maps; far grass keeps the climate tint alone: {0}", e.Message);
            return 0;
        }
    }

    byte[] seasonStrip;
    (int, int, double) altitudeKey = (-1, -1, -1);

    /// <summary>The climate's cooling with height in the season strip's altitude row (GrassSeasons.AltitudeRow), from this
    /// client's own Climate.GetAdjustedTemperature: Earth replaces it with the real lapse once its world's rules are in, which
    /// can be after the strip is made, so the row is written again whenever it answers differently.</summary>
    void UpdateAltitudeRow(double heightScale)
    {
        if (seasonStrip == null) return;
        var key = (Climate.GetAdjustedTemperature(255, 200), Climate.GetAdjustedTemperature(128, 40), heightScale);
        if (key == altitudeKey) return;
        altitudeKey = key;
        static int Cooling(int above)
        {
            int at = Climate.GetAdjustedTemperature(128, above);
            return at > 0 ? 128 - at : 255 - Climate.GetAdjustedTemperature(255, above);
        }
        GrassSeasons.AltitudeRow(seasonStrip, Cooling, heightScale);
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, grassSeasons);
        var row = new byte[GrassSeasons.Columns * 4];
        Array.Copy(seasonStrip, GrassSeasons.Altitude * GrassSeasons.Columns * 4, row, 0, row.Length);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, GrassSeasons.Altitude, GrassSeasons.Columns, 1, PixelFormat.Rgba, PixelType.UnsignedByte, row);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        api.Logger.Notification("[wmap] Far terrain's climate cools {0:0.0} °C at 200 blocks above sea, as this world's own climate does.",
            GrassSeasons.ReadAltitude(seasonStrip, 200, heightScale) / Climate.TemperatureScaleConversion);
    }
}
