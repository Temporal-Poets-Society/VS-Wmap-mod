using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Wmap.Client;
using EarthMap.Core;
using Wmap.Core;
using Wmap.Integrations;

class Program
{
    static void Main(string[] args)
    {
        if (args[0] == "--chart-wind")
        {
            Directory.CreateDirectory(args[1]);
            MapGpu.ChartWind(args[1], argsBaseline);
            MapGpu.RiverChannel(args[1], argsBaseline);
            return;
        }
        var data = EarthData.LoadFromFolder(args[0], true);
        data.SetFineTemperature(true); data.SetFineRain(true); data.SetSummits(true); data.SetGlaciers(true);
        data.SetIslandOverlay(true); data.SetLakeOverlay(true); data.SetShoreOverlay(true);
        double ring=args.Length>2 ? double.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture) : 510976;
        double length=ring==409600 ? 204800 : 256000;
        var geo = EarthLayout.Build(new LayoutInput { WorldWidth=512000, CwRingStart=(512000-ring)/2, CwRingLength=ring,
            CwEquatorZ=length/2, CwPoleDistance=length/2-48, Gutter=false }, data, out _);
        Console.WriteLine($"Fixture: physical width=512000, ring={ring}, length={length}, sea=110, height=512, seed=918273 (not a copy of the player's save)");
        var model = new TerrainModel(data, geo, new VerticalScale(512, 110, 510, 200, 47, 20), 918273)
            { LocalAlpineEcology=true, ClimateRules=3, GutterSeabed=3, MinSeaDepth=6, LakeShores=data.ShoreOverlay };
        model.Islands = new IslandLayer(data.Islands, geo, 4);
        model.Coasts = new CoastLayer(data.Coasts, geo, 4);
        var ends = RiverEnds.Prepare(data, data.Rivers);
        model.Rivers = new RiverNetwork(RiverDeltas.Apply(ends.Reaches).Reaches, geo, 100, 1.5, model.RiverWaterTop, "ends", 20,
            geo.RingLength/204800, .5, 2, falls:RiverFalls.All, continueDownstream:true, fadeEnds:ends.Fade, geometrySeams:true, poolLayout:10);
        model.Channels = SeaChannels.Build(model, true, null, SeaChannels.Version);
        var sample = WorldSource.EarthSampler(model);
        var rivers = WorldSource.EarthRivers(model.Rivers);
        string output=args.Length>1 ? args[1] : ".local/playtest";
        Directory.CreateDirectory(output);
        foreach (var place in new[] { ("Makalu",27.8846,87.1527), ("Fuji",35.2110,138.5951) })
        {
            geo.FromGeo(place.Item2,place.Item3,out double x,out double z);
            Console.WriteLine($"{place.Item1} x={x:F1} z={z:F1}");
            int glacier=0, missing=0, river=0, zeroDepth=0;
            const int width=513, height=257;
            var rgb=new byte[width*height*3];
            for(int dz=-256;dz<=256;dz+=2) for(int dx=-512;dx<=512;dx+=2)
            {
                double xx=Math.Floor(x)+dx, zz=Math.Floor(z)+dz;
                var c=model.GetColumn(xx,zz); var s=sample(xx,zz); var g=model.TerrainGeoAt(xx,zz);
                bool ice=c.Water==0 && c.Top>=110 && c.Top<510 && data.GlacierCoverage(g.Lat,g.Lon)>=.5;
                if(ice) { glacier++; if(s.Kind!=TileData.Ice) missing++; }
                if(s.Kind==TileData.River) { river++; if(s.Depth==0) zeroDepth++; }
                int i=((dz+256)/2*width+(dx+512)/2)*3;
                byte shade=(byte)Math.Clamp(105+c.Top/4,0,235);
                rgb[i]=shade; rgb[i+1]=(byte)(shade+5); rgb[i+2]=(byte)(shade-15);
                if(s.Kind==TileData.Ice) { rgb[i]=225; rgb[i+1]=241; rgb[i+2]=250; }
                if(Semantic.IsWater(s.Kind)) { rgb[i]=70; rgb[i+1]=135; rgb[i+2]=180; }
                if(Math.Abs(dx)<4 && Math.Abs(dz)<4) { rgb[i]=200; rgb[i+1]=30; rgb[i+2]=30; }
            }
            Png.WriteRgb(Path.Combine(output,place.Item1+"-surface.png"),width,height,rgb);
            Console.WriteLine($"RGI eligible {glacier}, missing from adapter {missing}, river columns {river}, dry river samples {zeroDepth}");
            if(!argsBaseline && missing!=0) throw new Exception("RGI glacier surface missing from prediction");
            int absent=0, present=0, frozen=0;
            foreach(var r in rivers)
            {
                double mx=(r.X1+r.X2)/2,mz=(r.Z1+r.Z2)/2;
                if(Math.Abs(mx-x)>512 || Math.Abs(mz-z)>256) continue;
                var c=model.GetColumn(mx,mz);
                var cg=model.TerrainGeoAt(Math.Floor(mx/32)*32+16,Math.Floor(mz/32)*32+16);
                model.ClimateAt(cg.Lat,cg.Lon,out double temp,out _,out _);
                if(c.River) { present++; if(temp < -12) frozen++; if(mz>z && mz<z+96 && Math.Abs(mx-x)<24)
                    Console.WriteLine($"south course: top={c.Top}, waterTop={c.WaterTop}, chunk temperature={temp:F2}, lat={cg.Lat:F5} lon={cg.Lon:F5}"); }
                else {
                    absent++; var g=geo.ToGeo(mx,mz);
                    Console.WriteLine($"uncarved course {g.Lat:F5},{g.Lon:F5}: top={c.Top}, water={c.Water}, ice={c.IceBottom}, width={r.HalfWidth:F2}");
                }
            }
            Console.WriteLine($"raw river pieces: carved={present} uncarved={absent} frozen={frozen}");
            if(place.Item1=="Makalu") MapGpu.Render(output,sample,(xx,zz)=>GeneratedSurface(model,xx,zz),x,z,110,argsBaseline);
        }
        Recorder(output);
        if(!argsBaseline) { SurfaceRules(); MapGpu.RecordedPrecedence(output); }
        MapGpu.RiverChannel(output, argsBaseline);
        // Play-test #36 (WM-D41): St Lawrence Island, where the player zoomed in to 0.3 blocks per pixel.
        geo.FromGeo(63.3575,-169.6337,out double lx,out double lz);
        Console.WriteLine($"St Lawrence Island x={lx:F1} z={lz:F1}");
        // The pale patch in the screenshot (63.27 N 169.66 W): does the prediction put ice there?
        geo.FromGeo(63.27,-169.66,out double px,out double pz);
        int iceNear=0,samples=0,highest=int.MinValue;
        for(int dz=-256;dz<=256;dz+=4) for(int dx=-256;dx<=256;dx+=4)
        { var s=sample(px+dx,pz+dz); samples++; if(s.Kind==TileData.Ice) iceNear++; highest=Math.Max(highest,s.Height); }
        Console.WriteLine($"Pale patch 63.27N 169.66W: predicted ice {iceNear}/{samples} samples within 256 blocks, highest predicted top Y={highest}");
        if(!argsBaseline) MapGpu.ZoomDetail(output,sample,lx,lz,110);
        Console.WriteLine(argsBaseline ? "BASELINE: expected regressions recorded" : "PASS: playtest regression checks");
    }

    static void Recorder(string output)
    {
        var ba=DispatchProxy.Create<IBlockAccessor, Blocks>();
        var proxy=(Blocks)(object)ba;
        var rock=new Block { BlockId=1, Code=new AssetLocation("rock-granite"), BlockMaterial=EnumBlockMaterial.Stone };
        var leaves=new Block { BlockId=2, Code=new AssetLocation("leaves-oak"), BlockMaterial=EnumBlockMaterial.Leaves };
        var snow=new Block { BlockId=3, Code=new AssetLocation("snowblock"), BlockMaterial=EnumBlockMaterial.Snow };
        proxy.At[100]=rock; proxy.At[124]=leaves; proxy.At[125]=snow;
        var ground=ObservedTerrain.Classify(ba,new ushort[]{100},0,new BlockPos(0,125,0),out var color);
        Console.WriteLine($"Snow canopy: recorded height={ground.Height}, material={color?.BlockMaterial} (ground=100)");
        proxy.MissingBelow=120;
        var partial=ObservedTerrain.Classify(ba,new ushort[]{100},0,new BlockPos(0,124,0),out _);
        Console.WriteLine($"Missing lower chunk: kind={partial.Kind} (unknown=0)");
        // Exercise the actual worker's box derivation with natural cliff, glacier and a player tower.
        var tile=new TileData(); RecordedColor.Mark(tile);
        var pack=typeof(RecordedColor).GetMethod("Pack",new[]{typeof(int),typeof(int),typeof(bool)});
        int Color(bool structure)=>pack==null ? RecordedColor.Pack(0xFFFFFF,0) : (int)pack.Invoke(null,new object[]{0xFFFFFF,0,structure});
        tile.Set(10*TileData.Padded+10,new MapSample(220,0,TileData.Land,Color(false)));
        tile.Set(10*TileData.Padded+11,new MapSample(225,0,TileData.Ice,Color(false)));
        tile.Set(10*TileData.Padded+12,new MapSample(248,0,TileData.Land,Color(true)));
        var derive=typeof(RecordedTiles).GetMethod("Derive",BindingFlags.Static|BindingFlags.NonPublic);
        var d=derive.Invoke(null,new object[]{new TileKey(1,1,0,0),tile,1L,false,null});
        var boxes=(float[])d.GetType().GetField("Boxes").GetValue(d);
        Console.WriteLine($"Structure boxes: {boxes.Length/RecordedTiles.BoxFloats} (natural cliff + glacier + tower; expected 1)");
        var rgb=new byte[400*200*3]; Array.Fill(rgb,(byte)180);
        for(int b=0;b<boxes.Length;b+=RecordedTiles.BoxFloats)
        {
            int x=(int)((boxes[b]/2-9)*100);
            for(int yy=Math.Clamp(300-(int)boxes[b+2],0,199); yy<190; yy++)
            for(int xx=Math.Max(x,0);xx<Math.Min(x+50,400);xx++)
            { int i=(yy*400+xx)*3; rgb[i]=rgb[i+1]=rgb[i+2]=245; }
        }
        Png.WriteRgb(Path.Combine(output,"structure-extrusions.png"),400,200,rgb);
        if(argsBaseline) return;
        if(ground.Height!=100 || partial.Kind!=TileData.Unknown || boxes.Length/RecordedTiles.BoxFloats!=1)
            throw new Exception("Recorder regression");
    }
    static bool argsBaseline => Environment.GetEnvironmentVariable("WMAP_BASELINE")=="1";

    static MapSample GeneratedSurface(TerrainModel model,double x,double z)
    {
        var c=model.GetColumn(x,z);
        // Independent transcription of Earth's Surface gates, for simulated observed RGI patches only.
        var g=model.TerrainGeoAt(x,z);
        if(!c.Beach && c.Water==0 && c.Top>=110 && c.Top<510 && !g.InGutter && !g.BeyondPole
            && model.Data.GlacierCoverage(g.Lat,g.Lon)>=.5) return new(c.Top,0,TileData.Ice);
        return WorldSource.ColumnSample(c.Top,c.WaterTop,c.Water,c.IceBottom,c.River);
    }

    static void SurfaceRules()
    {
        void Check(bool ok,string name) { if(!ok) throw new Exception(name); }
        MapSample Read(SurfaceModel model)=>WorldSource.EarthSampler(model)(20,20);
        var m=new SurfaceModel();
        Check(Read(m).Kind==TileData.Ice,"enabled RGI cover is ice");
        m.Data.HasGlaciers=false; Check(Read(m).Kind==TileData.Land,"saved glacier switch off preserves land");
        m.Data.HasGlaciers=true; m.Column.Beach=true; Check(Read(m).Kind==TileData.Land,"beach surface pass precedes glacier ice");
        m.Column.Beach=false; m.Column.Top=510; Check(Read(m).Kind==TileData.Land,"build-limit surface is not repainted");
        m.Column.Top=150; m.Column.Water=2; m.Column.WaterTop=160; m.Column.River=true;
        Check(Read(m).Kind==TileData.River,"RGI does not overwrite open river water");
        m.Temperature=-13; var ice=Read(m);
        Check(ice.Kind==TileData.Ice && ice.Height==160 && ice.Depth==0,"frozen fresh water has ice surface height");
        m.Column.Water=1; Check(Semantic.IsWater(Read(m).Kind),"cold salt water does not freeze by the fresh-water rule");
        var old=WorldSource.EarthSampler(new ColumnOnlyModel())(0,0);
        Check(old.Kind==TileData.Land,"older column-only provider fallback");
        Console.WriteLine("Surface rules: PASS (saved RGI off, beaches, ceiling, open rivers, frozen freshwater, salt water, old provider)");
    }
}

public class SurfaceModel
{
    public SurfaceData Data=new();
    public VerticalScale Vertical=new(512,110,510,200,47,20);
    public TerrainModel.Column Column=new() { Top=150, IceBottom=int.MaxValue, WaterTop=-1 };
    public double Temperature=5;
    public TerrainModel.Column GetColumn(double x,double z)=>Column;
    public EarthGeometry.GeoPos TerrainGeoAt(double x,double z)=>new() { Lat=27.9,Lon=87.15 };
    public void ClimateAt(double lat,double lon,out double t,out double rain,out double height)
    { t=Temperature;rain=100;height=150; }
}
public class SurfaceData
{
    public bool HasGlaciers=true;
    public double GlacierCoverage(double lat,double lon)=>1;
}
public class ColumnOnlyModel
{
    public TerrainModel.Column GetColumn(double x,double z)=>new() { Top=150,IceBottom=int.MaxValue,WaterTop=-1 };
}

public class Blocks : DispatchProxy
{
    public readonly Dictionary<int,Block> At=new();
    public int MissingBelow=int.MinValue;
    readonly Block air=new() { BlockId=0,Code=new AssetLocation("air"),BlockMaterial=EnumBlockMaterial.Air };
    protected override object Invoke(MethodInfo method, object[] args)
    {
        var pos=args.Length>0 ? args[0] as BlockPos : null;
        if(method.Name=="GetBlock")
        {
            if(args.Length>1 && (int)args[1]==BlockLayersAccess.Fluid) return air;
            return pos!=null && At.TryGetValue(pos.Y,out var block) ? block : air;
        }
        if(method.Name=="GetChunkAtBlockPos") return pos.Y<MissingBelow ? null : DispatchProxy.Create(method.ReturnType,typeof(Empty));
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
public class Empty : DispatchProxy
{
    protected override object Invoke(MethodInfo method,object[] args)=>method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}


