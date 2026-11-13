"""Render Wmap's chart with the Earth/Cylinder World publication-logo layout.

Uses Wmap's existing chart preview as the cylinder texture. No generated geography.
Usage: python tools/make-logo.py docs/previews/world-chart.png docs/moddb-logo.png
Add --scale 3 for the 1440x960 master. Adapted from Earth/tools/make-logo.py.
"""

import argparse
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def render(map_path, output_path, scale=1, rotation=0.40):
    aa = 3 * scale
    width, height = 480 * aa, 320 * aa
    with Image.open(map_path) as source:
        # The 1600x900 shader preview fits the world between y=67 and y=833.
        # Remove the surrounding parchment margins; retain the actual chart.
        texture = np.asarray(source.convert("RGB"), dtype=np.float32)
        source_height = texture.shape[0]
        texture = texture[round(source_height * 67 / 900):round(source_height * 833 / 900)] / 255
    source_h, source_w, _ = texture.shape

    y = np.linspace(0, 1, height)[:, None]
    background = (1 - y) * np.array([0.10, 0.16, 0.26]) + y * np.array([0.03, 0.06, 0.11])
    pixels = np.repeat(background[:, None, :], width, axis=1)

    cx, radius, ellipse = 140 * aa, 84 * aa, 22 * aa
    top, side_height = 38 * aa, 248 * aa
    ys, xs = np.mgrid[0:height, 0:width].astype(np.float32)
    dx = (xs + 0.5 - cx) / radius
    curve = np.sqrt(np.clip(1 - dx * dx, 0, 1))
    side_y = ys + 0.5 - top - ellipse * curve
    side = (np.abs(dx) < 1) & (side_y >= 0) & (side_y <= side_height)
    longitude = (np.arcsin(np.clip(dx, -1, 1)) / (2 * math.pi) + rotation) % 1
    latitude = np.clip(side_y / side_height, 0, 1)
    surface = texture[
        np.clip((latitude * (source_h - 1)).astype(int), 0, source_h - 1),
        np.clip((longitude * (source_w - 1)).astype(int), 0, source_w - 1),
    ]
    lighting = (0.45 + 0.6 * np.clip(curve * 0.8 - dx * 0.35 + 0.2, 0, 1))[..., None]
    pixels[side] = np.clip(surface * lighting, 0, 1)[side]

    # Keep the suite's polar cap and bottom rim.
    r2 = ((xs + 0.5 - cx) / radius) ** 2 + ((ys + 0.5 - top) / ellipse) ** 2
    cap = r2 <= 1
    ice = np.array([0.93, 0.96, 1.0]) - 0.12 * np.clip(r2, 0, 1)[..., None] * np.array([1, 0.8, 0.5])
    pixels[cap] = ice[cap]
    pixels[cap & (r2 > 0.80)] = np.array([0.80, 0.87, 0.95])

    result = Image.fromarray((pixels * 255).astype(np.uint8))
    draw = ImageDraw.Draw(result)
    draw.arc([cx-radius, top+side_height-ellipse, cx+radius, top+side_height+ellipse],
             0, 180, fill=(220, 232, 245), width=3 * aa)

    def font(names, size):
        for name in names:
            try:
                return ImageFont.truetype(str(Path("C:/Windows/Fonts") / name), size * aa)
            except OSError:
                pass
        return ImageFont.load_default(size=size * aa)

    heading = font(["georgiab.ttf", "timesbd.ttf"], 50)
    for line, y_pos in zip(("World", "Map"), (94, 151)):
        draw.text((267 * aa, y_pos * aa), line, font=heading, fill=(240, 244, 250))

    result = result.resize((480 * scale, 320 * scale), Image.Resampling.LANCZOS)
    result.save(output_path)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("map", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--scale", type=int, choices=(1, 2, 3), default=1)
    parser.add_argument("--rotation", type=float, default=0.40)
    args = parser.parse_args()
    render(args.map, args.output, args.scale, args.rotation)
