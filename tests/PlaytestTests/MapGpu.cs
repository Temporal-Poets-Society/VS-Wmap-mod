using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Wmap;
using Wmap.Client;
using Wmap.Core;
using EarthMap.Core;

// Offscreen production map shader, using the Windows OpenGL driver. No game client or game assets are needed.
static class MapGpu
{
    // Fully ice-covered terrain above the old 256-block limit, and winds containing broad exactly calm regions.
    // Exercise the embedded production shader on the real driver; baseline mode records the pre-fix failures.
    public static void ChartWind(string output, bool baseline)
    {
        using var context = new Context();
        const int w=512,h=256,sea=110;
        var topology=new Topology { Width=w,Height=h };
        using var renderer=new MapRenderer();
        int fbo=GL.GenFramebuffer(),target=GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D,target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.Viewport(0,0,w,h); GL.Disable(EnableCap.DepthTest);
        float[] projection={2f/w,0,0,0,0,-2f/h,0,0,0,0,-.01f,0,-1,1,0,1};
        float[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var frame=new MapFrame { Width=w,Height=h,Camera=new MapCamera { X=w/2,Z=h/2,Scale=1 },
            Topology=topology,Style=0,Development=true,Gui=1,HeightScale=512-sea,HeightRange=Semantic.HeightRangeFor(512,sea) };
        byte[] Read(string name)
        {
            GL.Finish();
            var pixels=new byte[w*h*4]; GL.ReadPixels(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
            var rgb=new byte[w*h*3];
            for(int y=0;y<h;y++) for(int x=0;x<w;x++) Array.Copy(pixels,((h-1-y)*w+x)*4,rgb,(y*w+x)*3,3);
            Png.WriteRgb(Path.Combine(output,name+".png"),w,h,rgb);
            return pixels;
        }
        byte[] Ice(bool peak, int lift=0)
        {
            MapSample Sample(double x,double z) => new(390+lift+(peak ? (int)(110*Math.Exp(-((x-256)*(x-256)+(z-128)*(z-128))/6000)) : 0),0,TileData.Ice);
            renderer.SetBase(BaseRaster.Bake(topology,w,h,sea,()=>Sample,null,1,CancellationToken.None,heightRange:frame.HeightRange),topology);
            while(!renderer.PumpBase()) { }
            if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
            renderer.DrawWorld(50); renderer.End();
            return Read(lift!=0 ? "ice-peak-raised" : peak ? "ice-peak" : "ice-flat");
        }
        var flat=Ice(false); var peak=Ice(true); var raised=Ice(true,5);
        int changed=0,total=0,contours=0;
        for(int y=48;y<208;y++) for(int x=176;x<336;x++)
        {
            int i=(y*w+x)*4; total++;
            if(Math.Abs(peak[i]-flat[i])>8 || Math.Abs(peak[i+1]-flat[i+1])>8 || Math.Abs(peak[i+2]-flat[i+2])>8) changed++;
            if(Math.Abs(peak[i]-raised[i])>12 || Math.Abs(peak[i+1]-raised[i+1])>12 || Math.Abs(peak[i+2]-raised[i+2])>12) contours++;
        }
        double relief=(double)changed/total;
        Console.WriteLine($"Ice peak (Y=390..500, 512-high world): {100*relief:F1}% of summit pixels distinguish height from a flat ice field");
        double contourCoverage=(double)contours/total;
        Console.WriteLine($"Ice contours: raising the peak five blocks changes {100*contourCoverage:F1}% of summit pixels");

        topology.Width=4096; topology.Height=2048;
        frame.Camera=new MapCamera { X=2048,Z=1024,Scale=.125 };
        byte[] Winds(bool calm, bool belts)
        {
            var field=WindGrid.Build((x,z)=>calm ? (0.0,0.0) : ((z-1024)/2048,0.0),topology,CancellationToken.None);
            renderer.SetWind(field,WindGrid.Width,WindGrid.Height,false);
            GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit);
            if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
            renderer.DrawWinds(50,belts); renderer.End();
            return Read(calm ? "wind-calm" : belts ? "wind-boundary" : "wind-boundary-arrows");
        }
        var calm=Winds(true,true);
        int covered=0;
        for(int i=3;i<calm.Length;i+=4) if(calm[i]>10) covered++;
        double calmCoverage=(double)covered/(w*h);
        Console.WriteLine($"Calm wind coverage: {100*calmCoverage:F2}% (circles only; no stripe-filled calm field)");
        var arrows=Winds(false,false); var boundary=Winds(false,true);
        int boundaryPixels=0,farPixels=0;
        for(int y=0;y<h;y++) for(int x=0;x<w;x++)
        {
            int i=(y*w+x)*4;
            if(boundary[i+3]<=arrows[i+3]+10) continue;
            if(Math.Abs(y-h/2)<3) boundaryPixels++; else farPixels++;
        }
        Console.WriteLine($"Prevailing-wind sign change: boundary {boundaryPixels} px, false distant belt pixels {farPixels}");
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
        if(!baseline && (relief<.15 || contourCoverage<.10 || calmCoverage>.04 || boundaryPixels<100 || farPixels>0))
            throw new Exception("Chart ice relief / calm wind boundary regression");
        Console.WriteLine(baseline ? "BASELINE: chart/wind regressions recorded" : "PASS: chart ice relief and wind boundaries");
    }

    // WM-D46, reported in game at 1:10 on the Niagara: predicted two- and four-block tiles drew the world chart's river
    // channel, interpolated from 489-block texels, as a lens up to a texel long round their own river, a second, darker
    // river. Here a 30-block river runs north-south through a column of 512-block chart texel centres (the Niagara at 1:10
    // lies along one), so the channel is negative along the column and its interpolated bank lies 30 to 85 blocks out on
    // both sides; the tiles at 3.7 blocks per pixel must leave the land more than 25 blocks from the river's centreline
    // free of river blue, in both styles.
    public static void RiverChannel(string output, bool baseline)
    {
        using var context = new Context();
        const int w=800,h=600,sea=110;
        const double texel=512, bpp=3.7, half=15;
        var topology=new Topology { Width=256*texel,Height=128*texel };
        double range=Semantic.HeightRangeFor(512,sea);
        double x1=117.5*texel,z1=58*texel,x2=x1,z2=70*texel;
        double Off(double x,double z)
        {
            double dx=x2-x1,dz=z2-z1,u=Math.Clamp(((x-x1)*dx+(z-z1)*dz)/(dx*dx+dz*dz),0,1);
            return Math.Sqrt((x1+u*dx-x)*(x1+u*dx-x)+(z1+u*dz-z)*(z1+u*dz-z));
        }
        MapSample Sample(double x,double z)=>Off(x,z)<=half ? new(sea+6,4,TileData.River) : new(sea+10,0,TileData.Land);
        var raster=BaseRaster.Bake(topology,256,128,sea,()=>Sample,new[]{new RiverSegment(x1,z1,x2,z2,half)},2,CancellationToken.None,heightRange:range);
        using var renderer=new MapRenderer(); renderer.SetBase(raster,topology);
        while(!renderer.PumpBase()) { }
        if(renderer.Error!=null) throw new Exception(renderer.Error);
        int fbo=GL.GenFramebuffer(),target=GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D,target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.Viewport(0,0,w,h); GL.Disable(EnableCap.DepthTest);
        float[] projection={2f/w,0,0,0,0,-2f/h,0,0,0,0,-.01f,0,-1,1,0,1};
        float[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var camera=new MapCamera { X=(x1+x2)/2,Z=(z1+z2)/2,Scale=1/bpp };
        var pages=new System.Collections.Generic.List<int>();
        int worst=0;
        foreach(int level in new[]{1,2})
        {
            var tiles=System.Linq.Enumerable.ToList(TileSelection.Visible(topology,camera,w,h,level,0));
            var ids=new System.Collections.Generic.Dictionary<TileKey,int>();
            foreach(var tile in tiles)
            {
                if(ids.ContainsKey(tile.Key)) continue;
                var semantic=SemanticTile.Sample(topology,tile.Key,Sample,sea,CancellationToken.None,heightRange:range);
                int id=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,id); pages.Add(id); ids[tile.Key]=id;
                GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,130,130,0,PixelFormat.Rgba,PixelType.UnsignedByte,semantic);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            }
            foreach(int style in new[]{0,1})
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
                GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit);
                var frame=new MapFrame { Width=w,Height=h,Camera=camera,Topology=topology,Style=style,Development=true,Gui=1,HeightScale=512-sea,HeightRange=range };
                if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
                renderer.DrawWorld(50);
                foreach(var tile in tiles) renderer.DrawTile(ids[tile.Key],130,0,0,tile.X,tile.Z,tile.Width,tile.Height,tile.Key.Step,1,49);
                renderer.End(); GL.Finish();
                var pixels=new byte[w*h*4]; GL.ReadPixels(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
                var rgb=new byte[w*h*3];
                int water=0,blue=0;
                for(int y=0;y<h;y++) for(int x=0;x<w;x++)
                {
                    int i=((h-1-y)*w+x)*4;
                    Array.Copy(pixels,i,rgb,(y*w+x)*3,3);
                    double off=Off(camera.X+(x-w/2.0)*bpp,camera.Z+(y-h/2.0)*bpp);
                    if(off<half-4) { water++; continue; }
                    // Parchment and land colours are warm (red over blue); the river channel's ink and blue are not.
                    if(off>25 && pixels[i+2]>pixels[i]+10) blue++;
                }
                string name=$"river-channel-L{level}-{(style==0 ? "chart" : "terrain")}";
                Png.WriteRgb(Path.Combine(output,name+".png"),w,h,rgb);
                Console.WriteLine($"River channel, {(style==0 ? "chart" : "terrain")} style, {1<<level}-block tiles at {bpp} blocks/px: {blue} river-blue land pixels beyond 25 blocks of the river ({water} pixels inside it)");
                if(water<2000) throw new Exception($"River channel fixture: the river is not in view ({water} pixels)");
                worst=Math.Max(worst,blue);
            }
        }
        foreach(int page in pages) GL.DeleteTexture(page);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
        if(!baseline && worst>50) throw new Exception($"Predicted tiles draw the world chart's river channel beside their own river ({worst} pixels)");
        Console.WriteLine(baseline ? $"BASELINE: river channel beside predicted rivers recorded ({worst} pixels)" : "PASS: predicted tiles draw rivers from their own water only");
    }

    public static void Render(string output, Func<double,double,MapSample> sample, Func<double,double,MapSample> generated,
        double x, double z, int sea, bool baseline)
    {
        using var context = new Context();
        const int w=1024,h=512;
        var topology=new Topology { Width=w, Height=h };
        var raster=BaseRaster.Bake(topology,w,h,sea,()=> (xx,zz)=>sample(x+xx-w/2,z+zz-h/2),null,4,CancellationToken.None,
            heightRange:Semantic.HeightRangeFor(512,sea));
        using var renderer=new MapRenderer(); renderer.SetBase(raster,topology);
        while(!renderer.PumpBase()) { }
        if(renderer.Error!=null) throw new Exception(renderer.Error);
        int fbo=GL.GenFramebuffer(), target=GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D,target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.Viewport(0,0,w,h); GL.Disable(EnableCap.DepthTest);
        float[] projection={2f/w,0,0,0,0,-2f/h,0,0,0,0,-.01f,0,-1,1,0,1};
        float[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var pages=new int[2]; int disagreements=0;
        for(int tx=0;tx<2;tx++)
        {
            var key=new TileKey(1,2,tx,0);
            var prediction=SemanticTile.Sample(topology,key,(xx,zz)=>sample(x+xx-w/2,z+zz-h/2),sea,CancellationToken.None,
                heightRange:Semantic.HeightRangeFor(512,sea));
            var recording=new TileData();
            for(int iz=1;iz<=128;iz++) for(int ix=1;ix<=128;ix++)
            {
                if(ix/4%5!=1 || iz/4%5!=1) continue;
                var real=generated(x+tx*512+(ix-.5)*4-w/2,z+(iz-.5)*4-h/2);
                if(real.Kind!=TileData.Ice) continue;
                int i=iz*TileData.Padded+ix; recording.Set(i,real);
                if(prediction[i*4+2]<128) disagreements++;
            }
            var encoded=SemanticTile.Encode(recording,sea,prediction,Semantic.HeightRangeFor(512,sea));
            pages[tx]=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,pages[tx]);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,130,130,0,PixelFormat.Rgba,PixelType.UnsignedByte,encoded);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
        }
        Console.WriteLine($"Observed RGI patch cells disagreeing with prediction: {disagreements}");
        if(!baseline && disagreements!=0) throw new Exception("Recorded RGI patches still differ from the prediction");
        foreach(int style in new[]{0,1})
        foreach(bool recorded in new[]{false,true})
        {
            var frame=new MapFrame { X=0,Y=0,Width=w,Height=h,Camera=new MapCamera { X=w/2,Z=h/2,Scale=1 },
                Topology=topology,Style=style,Development=true,Gui=1,HeightScale=512-sea,HeightRange=Semantic.HeightRangeFor(512,sea) };
            if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
            renderer.DrawWorld(50);
            if(recorded) for(int tx=0;tx<2;tx++) renderer.DrawTile(pages[tx],130,0,0,tx*512,0,512,512,4,1,49,true);
            renderer.End(); GL.Finish();
            var pixels=new byte[w*h*4]; GL.ReadPixels(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
            var rgb=new byte[w*h*3];
            for(int yy=0;yy<h;yy++) for(int xx=0;xx<w;xx++)
                Array.Copy(pixels,((h-1-yy)*w+xx)*4,rgb,(yy*w+xx)*3,3);
            Png.WriteRgb(Path.Combine(output,$"Makalu-gpu-{(style==0 ? "chart" : "terrain")}{(recorded ? "-recorded" : "")}.png"),w,h,rgb);
        }
        Console.WriteLine("GPU map: "+GL.GetString(StringName.Renderer)+"; production chart and terrain shaders rendered");
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
        foreach(int page in pages) GL.DeleteTexture(page);
    }

    // Coordinator GPU regression for old generated chunks versus a newer Earth prediction. Unlike the glacier
    // fixture above, this deliberately supplies contrary sources, including a stale raw river centreline.
    public static void RecordedPrecedence(string output)
    {
        using var context = new Context();
        const int w=512,h=256,sea=110;
        var topology=new Topology { Width=w,Height=h };
        var camera=new MapCamera { X=w/2,Z=h/2,Scale=1 };
        MapSample Prediction(double x,double z)=>x>184 && x<216
            ? new(145,9,TileData.River) : x>392 && x<440 ? new(145,9,TileData.Water) : new(155,0,TileData.Land);
        var rivers=new[]{new RiverSegment(200,0,200,h,16)};
        var raster=BaseRaster.Bake(topology,64,32,sea,()=>Prediction,rivers,1,CancellationToken.None);
        using var renderer=new MapRenderer(); renderer.SetBase(raster,topology);
        while(!renderer.PumpBase()) { }
        if(renderer.Error!=null) throw new Exception(renderer.Error);
        int fbo=GL.GenFramebuffer(),target=GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D,target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.Viewport(0,0,w,h); GL.Disable(EnableCap.DepthTest);
        var requested=new System.Collections.Generic.List<TileInstance>(TileSelection.Visible(topology,camera,w,h,0));
        var observed=new TileInstance(new TileKey(1,2,0,0),0,0,512,256);
        var recording=new TileData();
        for(int iz=1;iz<=128;iz++) for(int ix=1;ix<=128;ix++)
        {
            double x=(ix-.5)*4,z=(iz-.5)*4;
            if(x<64 || x>=320 || z<32 || z>=224) continue;
            var actual=x>=272 && x<304 && z>=112 && z<144 ? new MapSample(145,9,TileData.Water)
                : x>=88 && x<112 && z>=88 && z<112 ? new MapSample(160,0,TileData.Ice) : new MapSample(155,0,TileData.Land);
            recording.Set(iz*TileData.Padded+ix,actual);
        }
        // Existing observations need no WCL6 metadata to correct the terrain/water shown on the map.
        recording=TileCodec.Decode(TileCodec.Encode(recording));
        var pages=new System.Collections.Generic.Dictionary<TileKey,int>();
        int Upload(byte[] semantic)
        {
            int id=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,130,130,0,PixelFormat.Rgba,PixelType.UnsignedByte,semantic);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            return id;
        }
        foreach(var tile in requested) pages[tile.Key]=Upload(SemanticTile.Sample(topology,tile.Key,Prediction,sea,CancellationToken.None));
        var underlay=SemanticTile.Sample(topology,observed.Key,Prediction,sea,CancellationToken.None);
        pages[observed.Key]=Upload(SemanticTile.Encode(recording,sea,underlay));
        float[] projection={2f/w,0,0,0,0,-2f/h,0,0,0,0,-.01f,0,-1,1,0,1};
        float[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        byte[] Draw(System.Collections.Generic.IEnumerable<TileInstance> tiles,int style,float fade)
        {
            GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit);
            var frame=new MapFrame { Width=w,Height=h,Camera=camera,Topology=topology,Style=style,Development=true,Gui=1,HeightScale=402,HeightRange=256 };
            if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
            renderer.DrawWorld(50);
            var list=System.Linq.Enumerable.ToList(tiles);
            var shown=System.Linq.Enumerable.ToHashSet(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(list,v=>v.Key.Layer==1),v=>v.Key));
            foreach(var tile in list) renderer.DrawTile(pages[tile.Key],130,0,0,tile.X,tile.Z,tile.Width,tile.Height,
                tile.Key.Step,ViewTiles.Opacity(tile.Key,fade),49,tile.Key.Layer==1,ReferenceOf(tile,shown.Contains,topology,pages));
            renderer.End(); GL.Finish();
            var pixels=new byte[w*h*4]; GL.ReadPixels(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
            return pixels;
        }
        void Same(byte[] actual,byte[] expected,int x,int z,string description)
        {
            int i=((h-1-z)*w+x)*4;
            for(int c=0;c<3;c++) if(Math.Abs(actual[i+c]-expected[i+c])>1) throw new Exception("Recorded precedence: "+description);
        }
        foreach(int style in new[]{0,1})
        {
            var reference=Draw(new[]{observed},style,1);
            foreach(float fade in new[]{0f,.25f,1f})
            foreach(bool fallback in new[]{false,true})
            {
                var predictedOnly=Draw(requested,style,fade);
                var order=fallback ? ViewTiles.Display(requested,pages.ContainsKey,new[]{observed})
                    : ViewTiles.Display(System.Linq.Enumerable.Append(requested,observed),pages.ContainsKey);
                var actual=Draw(order,style,fade);
                Same(actual,reference,200,128,"predicted small river covered by old dry observation");
                Same(actual,reference,288,128,"actual lake retained over predicted land");
                Same(actual,reference,100,100,"recorded glacier retained");
                Same(actual,predictedOnly,416,128,"unrecorded wide eastern river unchanged");
                if(fade==1) Same(actual,predictedOnly,150,60,"recorded cell confirming the prediction shows its finer detail (WM-D41)");
                var rgb=new byte[w*h*3];
                for(int yy=0;yy<h;yy++) for(int xx=0;xx<w;xx++) Array.Copy(actual,((h-1-yy)*w+xx)*4,rgb,(yy*w+xx)*3,3);
                string amount=fade==0 ? "0" : fade==1 ? "1" : "025";
                Png.WriteRgb(Path.Combine(output,$"recorded-river-{style}-fade{amount}-{(fallback ? "fallback" : "requested")}.png"),w,h,rgb);
            }
        }
        foreach(int page in pages.Values) GL.DeleteTexture(page);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
        Console.WriteLine("Recorded precedence GPU: PASS (chart/terrain; old dry chunks versus finer predicted river; real lake/glacier; zoom fade 0/.25/1; cached observed fallback; eastern river unchanged)");
    }

    static MapRenderer.TileReference? ReferenceOf(TileInstance tile,Func<TileKey,bool> shown,Topology topology,
        System.Collections.Generic.IReadOnlyDictionary<TileKey,int> pages) =>
        ViewTiles.Reference(tile,shown,topology.MaxLevel) is { } r ? new MapRenderer.TileReference(pages[r.Key],0,0,r.X,r.Z,r.Key.Step) : null;

    // Play-test #36: zoomed in over recorded ground (St Lawrence Island), the atlas kept drawing the four-block recordings
    // over the one-block prediction. The recordings here are the prediction itself sampled where the recorder samples (the
    // world as generated), plus one recorded lake the prediction lacks. Three zooms: 0.3, 1 and 2 blocks per pixel.
    public static void ZoomDetail(string output,Func<double,double,MapSample> sample,double cx,double cz,int sea)
    {
        using var context=new Context();
        const int w=1024,h=512,size=2048,half=1024;
        var topology=new Topology { Width=size,Height=size/2 };
        double range=Semantic.HeightRangeFor(512,sea);
        MapSample Local(double xx,double zz)=>sample(cx+xx-half,cz+zz-half/2);
        // 16 blocks per texel: detail up to level 3, as in a world chart.
        var raster=BaseRaster.Bake(topology,size/16,size/32,sea,()=>Local,null,4,CancellationToken.None,heightRange:range);
        using var renderer=new MapRenderer(); renderer.SetBase(raster,topology);
        while(!renderer.PumpBase()) { }
        if(renderer.Error!=null) throw new Exception(renderer.Error);
        int fbo=GL.GenFramebuffer(),target=GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D,target);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.Viewport(0,0,w,h); GL.Disable(EnableCap.DepthTest);
        float[] projection={2f/w,0,0,0,0,-2f/h,0,0,0,0,-.01f,0,-1,1,0,1};
        float[] identity={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        // Recorded: everything within 600 blocks of the centre, as four-block samples at the cells' centre blocks (ObservedTerrain).
        // A 48-block lake on land the prediction calls dry stands for an old chunk the newer prediction contradicts.
        double lakeX=half-150,lakeZ=half/2+60;
        bool InLake(double x,double z)=>x>=lakeX && x<lakeX+48 && z>=lakeZ && z<lakeZ+48;
        var pages=new System.Collections.Generic.Dictionary<TileKey,int>();
        var predictions=new System.Collections.Generic.Dictionary<TileKey,byte[]>();
        byte[] Prediction(TileKey key)
        {
            var k=new TileKey(0,key.Level,key.X,key.Z);
            if(!predictions.TryGetValue(k,out var p)) predictions[k]=p=SemanticTile.Sample(topology,k,Local,sea,CancellationToken.None,heightRange:range);
            return p;
        }
        bool Observed(TileKey key)=>key.Level==2 && Math.Abs((key.X+.5)*key.Span-half)<600+key.Span/2 && Math.Abs((key.Z+.5)*key.Span-half/2)<600+key.Span/2;
        int lakeLand=0;
        int Page(TileKey key)
        {
            if(pages.TryGetValue(key,out int id)) return id;
            byte[] semantic;
            if(key.Layer==0) semantic=Prediction(key);
            else
            {
                var recording=new TileData();
                for(int iz=1;iz<=128;iz++) for(int ix=1;ix<=128;ix++)
                {
                    int bx=(int)(key.X*key.Span+(ix-1)*4+2),bz=(int)(key.Z*key.Span+(iz-1)*4+2);
                    if(Math.Sqrt((bx-half)*(bx-half)+(bz-half/2.0)*(bz-half/2.0))>600) continue;
                    var real=Local(bx+.5,bz+.5);
                    if(InLake(bx,bz) && real.Kind==TileData.Land) { real=new MapSample(real.Height-3,3,TileData.Water); lakeLand++; }
                    recording.Set(iz*TileData.Padded+ix,real);
                }
                semantic=SemanticTile.Encode(recording,sea,Prediction(key),range);
            }
            id=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,130,130,0,PixelFormat.Rgba,PixelType.UnsignedByte,semantic);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            return pages[key]=id;
        }
        byte[] Draw(MapCamera camera,System.Collections.Generic.IEnumerable<TileInstance> tiles,bool refine,int style)
        {
            var list=System.Linq.Enumerable.ToList(tiles);
            foreach(var tile in list) Page(tile.Key);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit);
            var frame=new MapFrame { Width=w,Height=h,Camera=camera,Topology=topology,Style=style,Development=true,Gui=1,HeightScale=512-sea,HeightRange=range };
            if(!renderer.Begin(projection,identity,frame,false)) throw new Exception(renderer.Error);
            renderer.DrawWorld(50);
            var shown=System.Linq.Enumerable.ToHashSet(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(list,v=>v.Key.Layer==1),v=>v.Key));
            foreach(var tile in list) renderer.DrawTile(pages[tile.Key],130,0,0,tile.X,tile.Z,tile.Width,tile.Height,tile.Key.Step,1,49,tile.Key.Layer==1,
                refine ? ReferenceOf(tile,shown.Contains,topology,pages) : null);
            renderer.End(); GL.Finish();
            var pixels=new byte[w*h*4]; GL.ReadPixels(0,0,w,h,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
            return pixels;
        }
        void Save(byte[] pixels,string name)
        {
            var rgb=new byte[w*h*3];
            for(int yy=0;yy<h;yy++) for(int xx=0;xx<w;xx++) Array.Copy(pixels,((h-1-yy)*w+xx)*4,rgb,(yy*w+xx)*3,3);
            Png.WriteRgb(Path.Combine(output,name),w,h,rgb);
        }
        bool Differ(byte[] a,byte[] b,int i)=>Math.Abs(a[i]-b[i])>6 || Math.Abs(a[i+1]-b[i+1])>6 || Math.Abs(a[i+2]-b[i+2])>6;
        foreach(double bpp in new[]{.3,1,2})
        foreach(int style in new[]{1,0})
        {
            var camera=new MapCamera { X=half-60,Z=half/2+40,Scale=1/bpp };
            var policy=new ViewPolicy(16,true,true,Observed,368);
            var view=new System.Collections.Generic.List<TileInstance>();
            int level=ViewTiles.Select(view,topology,camera,w,h,policy);
            var predictedOnly=Draw(camera,System.Linq.Enumerable.Where(ViewTiles.Display(view,_=>true),v=>v.Key.Layer==0),false,style);
            var recordedOnly=Draw(camera,System.Linq.Enumerable.Where(ViewTiles.Display(view,_=>true),v=>v.Key.Layer==1),false,style);
            // 0.5.0-pre.12 (WM-D40): every prediction under every recording, no per-cell check.
            var before=Draw(camera,System.Linq.Enumerable.ThenByDescending(System.Linq.Enumerable.OrderBy(view,v=>v.Key.Layer),v=>v.Key.Level),false,style);
            var after=Draw(camera,ViewTiles.Display(view,_=>true),true,style);
            if(lakeLand<20) throw new Exception($"Zoom fixture: the contrary lake lies off the island ({lakeLand} cells)");
            // Pixels well inside the recorded disc and away from the lake.
            int inside=0,beforeOff=0,afterOff=0,lakePixels=0,lakeKept=0;
            for(int py=0;py<h;py++) for(int px=0;px<w;px++)
            {
                double bx=camera.X+(px-w/2.0)*bpp,bz=camera.Z+(py-h/2.0)*bpp;
                int i=((h-1-py)*w+px)*4;
                if(bx>lakeX+8 && bx<lakeX+40 && bz>lakeZ+8 && bz<lakeZ+40) { lakePixels++; if(!Differ(after,recordedOnly,i)) lakeKept++; continue; }
                if(bx>lakeX-12 && bx<lakeX+60 && bz>lakeZ-12 && bz<lakeZ+60) continue;
                if(Math.Sqrt((bx-half)*(bx-half)+(bz-half/2.0)*(bz-half/2.0))>560) continue;
                inside++;
                if(Differ(before,predictedOnly,i)) beforeOff++;
                if(Differ(after,predictedOnly,i)) afterOff++;
            }
            string tag=$"{(style==1 ? "terrain" : "chart")}-{bpp.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.','p')}bpp";
            Save(predictedOnly,$"zoom-{tag}-prediction.png"); Save(before,$"zoom-{tag}-before.png"); Save(after,$"zoom-{tag}-after.png");
            double b=100.0*beforeOff/Math.Max(1,inside),a=100.0*afterOff/Math.Max(1,inside),k=100.0*lakeKept/Math.Max(1,lakePixels);
            Console.WriteLine($"Zoom {bpp} blocks/px ({tag}), prediction level {level}: recorded pixels unlike the prediction before {b:F1}% after {a:F1}% of {inside}; recorded lake kept {k:F1}% of {lakePixels} px (lake cells {lakeLand})");
            if(a>1.0) throw new Exception($"Zoom {bpp}: recorded ground that confirms the prediction is not drawn with its detail ({a:F1}%)");
            if(lakePixels==0 || k<99.0) throw new Exception($"Zoom {bpp}: the recorded lake gave way to the contrary prediction ({k:F1}%)");
        }
        foreach(int page in pages.Values) GL.DeleteTexture(page);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
        Console.WriteLine("Zoom detail GPU: PASS (St Lawrence Island; 0.3/1/2 blocks per pixel; terrain and chart; confirmed recordings refined, contrary recorded lake kept)");
    }


    sealed class Context : IDisposable, IBindingsContext
    {
        readonly IntPtr window,dc,rc;
        static readonly IntPtr gl=NativeLibrary.Load("opengl32.dll");
        public Context()
        {
            window=CreateWindowEx(0,"STATIC","Wmap offline render",0,0,0,64,64,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            dc=GetDC(window);
            var p=new PixelFormatDescriptor { Size=40,Version=1,Flags=0x24,ColorBits=32,DepthBits=24 };
            int format=ChoosePixelFormat(dc,ref p);
            if(format==0 || !SetPixelFormat(dc,format,ref p)) throw new Exception("No offscreen pixel format");
            rc=wglCreateContext(dc);
            if(!wglMakeCurrent(dc,rc)) throw new Exception("Cannot create offscreen OpenGL context");
            GL.LoadBindings(this);
        }
        public IntPtr GetProcAddress(string name)
        {
            var address=wglGetProcAddress(name);
            if(address!=IntPtr.Zero && address.ToInt64()!=-1 && address.ToInt64()>3) return address;
            return NativeLibrary.TryGetExport(gl,name,out address) ? address : IntPtr.Zero;
        }
        public void Dispose() { wglMakeCurrent(IntPtr.Zero,IntPtr.Zero); wglDeleteContext(rc); ReleaseDC(window,dc); DestroyWindow(window); }
    }
    [StructLayout(LayoutKind.Sequential)] struct PixelFormatDescriptor
    {
        public ushort Size,Version; public uint Flags;
        public byte PixelType,ColorBits,RedBits,RedShift,GreenBits,GreenShift,BlueBits,BlueShift,AlphaBits,AlphaShift,
            AccumBits,AccumRedBits,AccumGreenBits,AccumBlueBits,AccumAlphaBits,DepthBits,StencilBits,AuxBuffers,LayerType,Reserved;
        public uint LayerMask,VisibleMask,DamageMask;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc,ref PixelFormatDescriptor p);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc,int format,ref PixelFormatDescriptor p);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc,IntPtr rc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
}
