# Wmap logo source

The selected publication logo follows the Earth logo supplied by the author on 31 October 2026: a mapped cylinder on a navy background, a pale polar cap and serif typography. The cylinder uses Wmap's actual nautical-chart preview at `docs/previews/world-chart.png`. At the author's follow-up, the arrow and explanatory lines are removed.

Final upload: `docs/moddb-logo.png`, exactly 480 × 320 pixels. Master: `docs/moddb-logo-master.png`, 1440 × 960 pixels. The only text is the full public name **World Map**, set on two lines.

The native renderer at `tools/make-logo.py` is adapted from `Earth/tools/make-logo.py`, which follows `Cworld/tools/make-logo.py`. It reuses their background, cylinder geometry, lighting, polar cap and typography. It crops the world-chart preview's upper and lower parchment margins and places the Atlantic-facing part of the chart on the cylinder. No new geography or imagegen artwork is generated for this revision.

Reproduce from the Wmap repository:

```powershell
python tools/make-logo.py docs/previews/world-chart.png docs/moddb-logo.png
python tools/make-logo.py docs/previews/world-chart.png docs/moddb-logo-master.png --scale 3
```

The renderer uses NumPy and Pillow. This is publication artwork made from an existing shader preview; it is not a new in-game screenshot or runtime verification. No game, release package or save is changed.

The earlier square folded-map logo and its imagegen prompt remain in `docs/publishing-assets/` as an alternative. The selected logo is now the 480 × 320 cylinder artwork above.
