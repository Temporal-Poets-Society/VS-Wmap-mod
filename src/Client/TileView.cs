using System.Collections.Generic;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// The detail tiles one map view (the atlas, the HUD minimap) requests, uploads and draws, worked out again only when
/// something they depend on changed: the camera, the view's size, its policy, the recordings on disk, the tile client's
/// view, or the atlas's slots and data. A still map then costs no selection, sets or sorts per frame. Render thread.
/// </summary>
public sealed class TileView
{
    readonly List<TileInstance> visible = new();
    readonly List<TileKey> keys = new();
    readonly List<TileInstance> display = new();
    readonly HashSet<TileKey> shownRecordings = new();
    int maxLevel;
    State last;
    bool selected, uploadMore;
    int displayVersion = -1, uploadVersion = -1, wantedVersion = -1;

    readonly record struct State(double X, double Z, double Scale, double Width, double Height, double BaseTexel, bool Predictions,
        bool Observations, int Budget, bool Sharp, bool Flat, bool Enabled, int Observed, int View, double DrawTexel);

    /// <summary>The tiles the view requests (coarse first), as <see cref="ViewTiles.Select"/> chose them.</summary>
    public IReadOnlyList<TileInstance> Visible => visible;
    /// <summary>The tiles to draw, in draw order (<see cref="TileAtlas.Display"/>).</summary>
    public IReadOnlyList<TileInstance> Display => display;
    /// <summary>The drawn recording a drawn prediction tile is checked against (<see cref="ViewTiles.Reference"/>).</summary>
    public TileInstance? Reference(TileInstance tile) => ViewTiles.Reference(tile, shownRecordings.Contains, maxLevel);

    /// <summary>Brings the selection, the tile client's wanted tiles, the uploads and the draw list up to date.</summary>
    /// <param name="enabled">False selects nothing (another dimension).</param>
    public void Update(TileClient tiles, TileAtlas atlas, Topology t, MapCamera c, double width, double height, ViewPolicy p, double baseTexel, bool enabled = true)
    {
        var state = new State(c.X, c.Z, c.Scale, width, height, p.BaseTexel, p.Predictions, p.Observations, p.Budget, p.Sharp, p.FlatPrediction,
            enabled, tiles.ObservationVersion, tiles.View, baseTexel);
        // Another view (the atlas over the minimap) or closing a view may have replaced the tile client's wanted tiles.
        bool changed = !selected || state != last || tiles.WantedVersion != wantedVersion;
        if (changed)
        {
            last = state; selected = true;
            if (enabled) ViewTiles.Select(visible, t, c, width, height, p);
            else visible.Clear();
            keys.Clear();
            foreach (var v in visible) keys.Add(v.Key);
            tiles.SetView(keys);
            wantedVersion = tiles.WantedVersion;
        }
        // GL uploads happen outside the scissor and the shader.
        if (changed || uploadMore || atlas.Version != uploadVersion)
        {
            uploadMore = atlas.Upload(keys);
            uploadVersion = atlas.Version;
        }
        if (changed || atlas.Version != displayVersion)
        {
            display.Clear();
            display.AddRange(atlas.Display(visible, t, c, width, height, baseTexel, p.Observations));
            shownRecordings.Clear();
            foreach (var tile in display) if (tile.Key.Layer == 1) shownRecordings.Add(tile.Key);
            maxLevel = t.MaxLevel;
            displayVersion = atlas.Version;
        }
    }

    /// <summary>Forgets everything (a new atlas or client): the next update starts over.</summary>
    public void Reset() { selected = false; uploadMore = false; displayVersion = uploadVersion = wantedVersion = -1; visible.Clear(); keys.Clear(); display.Clear(); shownRecordings.Clear(); }
}
