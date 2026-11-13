"""Serve the map shader preview: python serve.py [world-WxH.bin.gz] [port]

The raster is written by the Earth smoke test (WmapSmoke/world-<W>x<H>.bin.gz). Without one, the
newest Earth smoke run in .local is used. Shaders are read from src/Client/Shaders on every request,
so edits show after pressing R in the page."""
import glob, http.server, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.normpath(os.path.join(HERE, "..", "..", "src", "Client", "Shaders"))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
args = sys.argv[1:]
PORT = int(args.pop()) if args and args[-1].isdigit() else 8765
if args: RASTER = os.path.abspath(args[0])
else:
    # Run folders are named smoke-earth-<yyyyMMdd-HHmmss>, so the last in name order is the newest.
    runs = sorted(glob.glob(os.path.join(ROOT, ".local", "smoke-earth-*", "WmapSmoke", "world-*.bin.gz")))
    if not runs: sys.exit("No world raster found: run tools\\Run-Smoke.ps1 -Profile earth, or pass a raster path")
    RASTER = runs[-1]
size = re.search(r"(\d+)x(\d+)", os.path.basename(RASTER))
# The climate raster (Terrain colours) the Earth smoke run writes next to the world raster, if any.
CLIMATE = next(iter(sorted(glob.glob(os.path.join(os.path.dirname(RASTER), "climate-*.bin.gz")))), None)
climate_size = re.search(r"(\d+)x(\d+)", os.path.basename(CLIMATE)) if CLIMATE else None
if not size: sys.exit("The raster file name must contain its size, e.g. world-8192x4096.bin.gz")

class Handler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        path = path.split("?", 1)[0]
        if path.startswith("/shaders/"): return os.path.join(SHADERS, os.path.basename(path))
        if path == "/raster": return RASTER
        if path == "/climate" and CLIMATE: return CLIMATE
        return os.path.join(HERE, os.path.basename(path) or "index.html")
    def do_GET(self):
        if self.path.startswith("/raster-info"):
            body = json.dumps({"width": int(size.group(1)), "height": int(size.group(2)),
                               "climateWidth": int(climate_size.group(1)) if climate_size else 0,
                               "climateHeight": int(climate_size.group(2)) if climate_size else 0}).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers(); self.wfile.write(body)
            return
        super().do_GET()
    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()
    def log_message(self, *args): pass

print(f"Map shader preview: http://127.0.0.1:{PORT}/ ({os.path.relpath(RASTER, ROOT)})")
http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
