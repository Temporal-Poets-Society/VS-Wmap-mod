using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// The game's colour of a recorded sample and its block light, packed into <see cref="TileData.Colors"/> (the mid-distance
/// view, WM-P06 prototype). The low 24 bits are the colour as <c>Block.GetColor</c> gives it (red in the low byte, as the
/// game's map uploads it), the top byte 0x80 | structure (0x20) | block light (0..31). WCL6 adds structure provenance;
/// WCL5 colours/lights remain readable, without structure evidence. The terrain tile codec is unchanged.
/// Recordings before 0.4.4 stored <c>Block.GetColor</c> raw, alpha in the top byte (usually 0xFF: flag set, light 31), and
/// 0.4.4 to 0.4.11 stored 0. So a tile says which it holds (0.5.0-pre.3): <see cref="Mark"/> in its padding corner, which no
/// sample and no paper-map merge ever writes. Only in a marked tile are colours this format (<see cref="Of"/>); the recorder
/// marks a tile when it first writes a colour into it, clearing pre-WCL5 colours or upgrading WCL5 colours in place.
/// </summary>
public static class RecordedColor
{
    /// <summary>Block light from which a recorded sample is a light seen from afar (a torch gives 14, a lantern more).</summary>
    public const int Lit = 8;
    public static int Pack(int gameColor, int light) => Pack(gameColor, light, false);
    public static int Pack(int gameColor, int light, bool structure) =>
        (gameColor & 0xFFFFFF) | ((0x80 | (structure ? 0x20 : 0) | Math.Clamp(light, 0, 31)) << 24);
    /// <summary>Index 0, the north-west padding corner: the recorder copies samples into the padding along the edges only, and
    /// merges copy known cells only, so it is 0 in every tile written before.</summary>
    public const int MarkIndex = 0, MarkValue = 0x57434C36, LegacyMarkValue = 0x57434C35; // WCL6 / WCL5
    public static bool Marked(TileData tile) => tile.ColorAt(MarkIndex) is MarkValue or LegacyMarkValue;
    public static bool Current(TileData tile) => tile.ColorAt(MarkIndex) == MarkValue;
    /// <summary>Makes a tile hold WCL6, preserving valid WCL5 colours/lights and clearing earlier raw colours.</summary>
    public static void Mark(TileData tile)
    {
        if (Current(tile)) return;
        if (Marked(tile))
        {
            // V5 has colour/light but no evidence that any cell is a structure. Keep valid colours, never invent boxes.
            for (int i = 0; i < TileData.Count; i++)
                if (((tile.ColorAt(i) >> 29) & 7) != 4) tile.Colors[i] = 0;
        }
        else Array.Clear(tile.Colors);
        tile.Colors[MarkIndex] = MarkValue;
    }
    /// <summary>A cell's colour as this format reads it: 0 (none) in a tile not marked.</summary>
    public static int Of(TileData tile, int i) => Current(tile) ? tile.ColorAt(i)
        : Marked(tile) && ((tile.ColorAt(i) >> 29) & 7) == 4 ? tile.ColorAt(i) : 0;
    /// <summary>A colour of this format: its top byte is 0x80 to 0xBF (bit 5 is structure provenance in WCL6). 0.5.0-pre.4: the top bit alone also
    /// took a colour that a recorder from before 0.4.4 wrote raw (top byte 0xFF) into a marked tile after a rollback, as light
    /// 31. Such a colour is ignored now; one whose alpha was 0x80 to 0x9F would still pass (the game's block colours are
    /// opaque).</summary>
    public static bool Has(int packed) => ((packed >> 30) & 3) == 2;
    /// <summary>The colour grassy soil is recorded with (0.5.3-rc.7, WM-0022): far terrain does not draw it but its own grass
    /// there, which follows Earth's grass season maps and wet seasons as the game's grass does (the game's colour for a map,
    /// <c>Block.GetColor</c>, takes neither: tan where Earth's grass was green in rc.5, green where it was dry in rc.6). A plain
    /// grass green (91, 134, 52), as older versions and the atlas draw it. R in the lowest byte.</summary>
    public const int GrassMarker = 0x34865B;
    /// <summary>A recorded colour that stands for grass (<see cref="GrassMarker"/>).</summary>
    public static bool IsGrass(int packed) => Has(packed) && (packed & 0xFFFFFF) == GrassMarker;
    public static bool IsStructure(int packed) => Has(packed) && (packed & 0x20000000) != 0;
    public static int Light(int packed) => Has(packed) ? (packed >> 24) & 31 : 0;
    public static byte R(int packed) => (byte)packed;
    public static byte G(int packed) => (byte)(packed >> 8);
    public static byte B(int packed) => (byte)(packed >> 16);
    /// <summary>Whether a sample whose terrain did not change is still a change: it now has a colour where it had none, its
    /// light came on, went out or changed by a step of 4, or its colour moved by more than <see cref="ColourStep"/> in a channel
    /// (0.5.3-rc.6: grass recorded before as the soil under the plant tint, and grass that turned with the season, are recorded
    /// again where the player passes). A small colour change alone is not (the tints drift with the weather).</summary>
    public static bool Changed(int old, int now) => Has(now) && (!Has(old) || IsStructure(old) != IsStructure(now)
        || (Light(old) >= Lit) != (Light(now) >= Lit) || Light(old) / 4 != Light(now) / 4 || Moved(old, now));
    /// <summary>A colour change that counts on its own: more than this in a channel (of 255).</summary>
    public const int ColourStep = 40;
    static bool Moved(int old, int now) => Math.Abs(R(old) - R(now)) > ColourStep || Math.Abs(G(old) - G(now)) > ColourStep || Math.Abs(B(old) - B(now)) > ColourStep;
}
