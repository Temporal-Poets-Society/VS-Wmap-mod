using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Wmap;
using Wmap.Client;
using Wmap.Core;

/// <summary>Exercise the real GUI element with the game's projection, culling and dialog depth.</summary>
public static class GuiRenderChecks
{
    public static string Run(string output, BaseRaster raster, Topology topology, int width, int height, int framebuffer)
    {
        var manager = new WorldMapManager();
        manager.RegisterDefaultMapLayers();
        WmapMinimapLayer.Register(manager);
        var order = new List<string>(manager.MapLayerRegistry.Keys);
        if (order.IndexOf("wmapchart") != order.IndexOf("chunks") + 1 || order.IndexOf("wmapchart") > order.IndexOf("players"))
            throw new Exception("Wmap HUD layer must render over terrain and before native markers");
        if (manager.MapLayerRegistry["players"] != typeof(WmapPlayerMapLayer)) throw new Exception("Wmap HUD heading layer was not registered");
        // ClientMain.OrthoMode uses this projection and translates the GUI towards the far plane.
        const float near = .4f, far = 20001;
        float[] projection = { 2f / width, 0, 0, 0, 0, -2f / height, 0, 0, 0, 0, -2f / (far - near), 0, -1, 1, -(far + near) / (far - near), 1 };
        float[] view = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -19849, 1 };
        var sharedMatrix = new float[16];
        bool reuseMatrix = false;
        float[] Matrix(float[] source)
        {
            if (!reuseMatrix) return source;
            // ClientMain returns tmpMatrix from BOTH getters, overwriting it on each read.
            Array.Copy(source, sharedMatrix, 16);
            return sharedMatrix;
        }
        var render = GuiServiceProxy.Make<IRenderAPI>((method, args) => method.Name switch
        {
            "get_CurrentProjectionMatrix" => Matrix(projection),
            "get_CurrentModelviewMatrix" => Matrix(view),
            _ => GuiServiceProxy.Default(method)
        });
        var api = GuiServiceProxy.Make<ICoreClientAPI>((method, args) => method.Name switch
        {
            "get_Render" => render,
            "GetOrCreateDataPath" => Directory.CreateDirectory(Path.Combine(output, "gui-gpu-data", (string)args[0])).FullName,
            _ => GuiServiceProxy.Default(method)
        });
        var info = new AtlasInfo { Session = "gui-gpu", CacheId = new string('c', 64), ObservationId = new string('d', 64),
            StartX = topology.StartX, Width = topology.Width, Height = topology.Height, Wraps = topology.Wraps, CanDevelop = true, CanPredict = true };
        using var client = new WmapClient(api, new WmapConfig(), DispatchProxy.Create<IClientNetworkChannel, FakeChannel>(), info);
        client.Renderer.SetBase(raster, topology);
        while (!client.Renderer.BaseReady) client.Renderer.PumpBase();
        var parent = ElementBounds.Fixed(0, 0, width, height).WithEmptyParent();
        var bounds = ElementBounds.Fixed(0, 0, width, height);
        parent.WithChild(bounds); parent.CalcWorldBounds();
        using var element = new AtlasElement(api, bounds, client, (_, _) => { });
        element.Fit();
        int depth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        var failures = new List<string>();
        try
        {
            foreach (string mode in new[] { "culling", "depth", "shared matrices" })
            {
                bool culling = mode == "culling";
                reuseMatrix = mode == "shared matrices";
                GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(true); GL.ClearDepth(1); GL.Clear(ClearBufferMask.DepthBufferBit);
                if (mode == "depth")
                {
                    // The composer paints its static brown background at depth 50 before the map.
                    GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Less);
                    var camera = new MapCamera(); camera.Fit(topology, width, height);
                    var frame = new MapFrame { Width = width, Height = height, Camera = camera, Topology = topology, Development = true };
                    client.Renderer.Begin(projection, view, frame, false);
                    client.Renderer.DrawWorld(50); client.Renderer.End();
                }
                GL.ClearColor(.25f, .15f, .08f, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
                if (culling) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); GL.FrontFace(FrontFaceDirection.Ccw); }
                // IClientPlayer has an internal interface member, so invoke the actual Draw path
                // directly instead of replacing that player interface with a DispatchProxy.
                typeof(AtlasElement).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(element, new object[] { new EntityPos(), false, true });
                var pixels = new byte[width * height * 4];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                int changed = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (Math.Abs(pixels[i] - 64) > 3 || Math.Abs(pixels[i + 1] - 38) > 3 || Math.Abs(pixels[i + 2] - 20) > 3) changed++;
                if (changed < width * height * .8) failures.Add("GUI map blank with " + mode);
                if (GL.IsEnabled(EnableCap.CullFace) != culling) failures.Add("GUI map did not restore face-culling state");
                if (GL.GetError() is var error && error != ErrorCode.NoError) failures.Add("GUI OpenGL error: " + error);
            }
            if (failures.Count == 0)
            {
                GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace);
                reuseMatrix = true;
                // Exercise both production marker paths against the game's actual forward
                // vector, independent of the native footprint's front/back convention.
                var markerFrame = (MapFrame)typeof(AtlasElement).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(element);
                markerFrame.Gui = 3;
                foreach (float yaw in new[] { 0f, (float)Math.PI / 2, (float)Math.PI, (float)Math.PI * 1.5f })
                {
                    GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
                    client.Renderer.Begin(render, markerFrame, false);
                    typeof(AtlasElement).GetMethod("DrawPlayer", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(element, new object[] { new EntityPos(markerFrame.Camera.X, 0, markerFrame.Camera.Z, yaw) });
                    client.Renderer.End();
                    // a player's level pitch is π (pitch 0 would be the view straight back over the head)
                    var forward = EntityPos.GetViewVector((float)Math.PI, yaw);
                    float dx = forward.X, dy = forward.Z;
                    var ahead = new byte[4]; var behind = new byte[4];
                    GL.ReadPixels((int)(width / 2 + dx * 22), height - 1 - (int)(height / 2 + dy * 22), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, ahead);
                    GL.ReadPixels((int)(width / 2 - dx * 22), height - 1 - (int)(height / 2 - dy * 22), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, behind);
                    if (ahead[3] < 180 || behind[3] > 80) failures.Add($"Atlas/native minimap heading mismatch at yaw {yaw:0.00}");
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    client.Renderer.Begin(render, markerFrame, false);
                    WmapPlayerMapLayer.DrawPlayer(client.Renderer, markerFrame, markerFrame.Camera.X, markerFrame.Camera.Z, yaw);
                    client.Renderer.End();
                    var miniAhead = new byte[4]; var miniBehind = new byte[4];
                    GL.ReadPixels((int)(width / 2 + dx * 22), height - 1 - (int)(height / 2 + dy * 22), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, miniAhead);
                    GL.ReadPixels((int)(width / 2 - dx * 22), height - 1 - (int)(height / 2 - dy * 22), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, miniBehind);
                    if (miniAhead[3] < 180 || miniBehind[3] > 80) failures.Add($"Minimap arrow disagrees with game forward vector at yaw {yaw:0.00}");
                }
                // Map layers drawn after Wmap's (Magelan's ships at depth 55) show through the empty corners of the
                // player marker's quad and everywhere under the wind layer's whole-map quad: 0.3.6 wrote depth there.
                GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); GL.DepthMask(true);
                GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                var breeze = new float[WindGrid.Width * WindGrid.Height * 2];
                for (int i = 0; i < breeze.Length; i += 2) breeze[i] = .3f;
                client.Renderer.SetWind(breeze, WindGrid.Width, WindGrid.Height, topology.Wraps);
                double cx = width / 2.0, cy = height / 2.0, r = 9 * markerFrame.Gui;
                float[] green = { 0, 1, 0, 1 };
                client.Renderer.Begin(render, markerFrame, false);
                client.Renderer.DrawWinds(57, false);
                client.Renderer.DrawMarker(cx, cy, 0, r, 58);
                client.Renderer.DrawMarker(cx, cy, 0, 90, 55, green);
                client.Renderer.DrawMarker(cx - 300, cy, 0, 90, 55, green);
                client.Renderer.End();
                GL.Disable(EnableCap.DepthTest);
                bool Green(double x, double y)
                {
                    var p = new byte[4];
                    GL.ReadPixels((int)x, height - 1 - (int)y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                    return p[1] > 180 && p[0] < 90 && p[2] < 90;
                }
                if (!Green(cx + 1.3 * r, cy + 1.3 * r)) failures.Add("A layer under the player marker is hidden by the marker's empty corners");
                if (!Green(cx - 320, cy + 10) || !Green(cx - 280, cy + 30))
                {
                    var p1 = new byte[4]; GL.ReadPixels((int)(cx - 320), height - 1 - (int)(cy + 10), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p1);
                    failures.Add($"A layer drawn after the wind arrows is hidden under them ({string.Join(",", p1)})");
                }
                if (Green(cx, cy - 0.5 * r)) failures.Add("The player marker no longer covers a layer drawn under it");
                // The barometer's local field: arrows within its reach only, with a dashed circle at the edge.
                var reachFrame = new MapFrame { Width = width, Height = height, Camera = new MapCamera { X = topology.StartX + topology.Width / 2, Z = topology.Height / 2, Scale = .5 },
                    Topology = topology, Gui = 1 };
                int n = WindGrid.LocalSize;
                var field = new float[n * n * 2];
                for (int i = 0; i < field.Length; i += 2) { field[i] = .45f; field[i + 1] = .2f; }
                client.Renderer.SetLocalWind(field, n, reachFrame.Camera.X - 500, reachFrame.Camera.Z - 500, 1000);
                GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
                client.Renderer.Begin(render, reachFrame, false);
                client.Renderer.DrawWinds(57, true);
                client.Renderer.End();
                var winds = new byte[width * height * 4];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, winds);
                int inside = 0, beyond = 0;
                for (int py = 0; py < height; py++)
                for (int px = 0; px < width; px++)
                {
                    if (winds[(py * width + px) * 4 + 3] < 128) continue;
                    double dx = px - cx, dy = height - 1 - py - cy, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 200) inside++; else if (d > 262) beyond++;
                }
                if (inside < 300 || beyond > 0) failures.Add($"Barometer field drew {inside} ink pixels inside its reach and {beyond} beyond it");
                var miniBounds = ElementBounds.Fixed(80, 60, 320, 240);
                parent.WithChild(miniBounds); miniBounds.CalcWorldBounds();
                var map = (GuiElementMap)RuntimeHelpers.GetUninitializedObject(typeof(GuiElementMap));
                map.Bounds = miniBounds; map.ZoomLevel = .25f;
                double x = topology.StartX + topology.Width / 2, z = topology.Height / 2;
                map.CurrentBlockViewBounds = new Cuboidd(x - 640, 0, z - 480, x + 640, 0, z + 480);
                using var minimap = new WmapMinimapLayer(api, null);
                byte[] Center()
                {
                    var pixel = new byte[4];
                    GL.ReadPixels((int)(miniBounds.renderX + 160), height - (int)(miniBounds.renderY + 120), 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                    return pixel;
                }
                int Difference(byte[] a, byte[] b) => Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
                GL.ClearColor(.25f, .15f, .08f, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
                client.Config.DefaultStyle = 1;
                minimap.Draw(client, map); var terrain = Center();
                if (Difference(terrain, new byte[] { 64, 38, 20, 255 }) < 25) failures.Add("Wmap minimap remained the vanilla/brown background");
                client.Config.DefaultStyle = 0;
                minimap.Draw(client, map); var chart = Center();
                if (Difference(terrain, chart) < 25) failures.Add("Minimap did not share the atlas style");
                client.Tiles.SetDevelopment(false);
                minimap.Draw(client, map); var unexplored = Center();
                client.Mask.Reveal(x, z, 100);
                minimap.Draw(client, map); var explored = Center();
                if (Difference(unexplored, explored) < 15) failures.Add("Minimap did not share gameplay exploration");
                var outside = new byte[4]; GL.ReadPixels(2, 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, outside);
                if (Difference(outside, new byte[] { 64, 38, 20, 255 }) > 3) failures.Add("Minimap drew outside its HUD bounds");
                if (GL.GetError() is var error && error != ErrorCode.NoError) failures.Add("Minimap OpenGL error: " + error);
            }
        }
        finally
        {
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, 0);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0); GL.DeleteRenderbuffer(depth);
        }
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
        return "real GUI element: game projection, enabled culling, depth-tested dialog background, shared matrix getters; atlas/minimap arrows match game forward vector at four cardinal directions; " +
            "later layers show through the marker's empty corners and over the wind arrows; the barometer field stays within its reach; Wmap minimap: style, exploration and HUD bounds";
    }
}
