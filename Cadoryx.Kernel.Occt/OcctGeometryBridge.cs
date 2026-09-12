using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

/// <summary>Only native adapters use this bridge. Each read constructs an independent native graph.</summary>
public static class OcctGeometryBridge
{
    public static Shape ReadShape(GeometryAssetRef geometry,IAssetStore assets)
    {
        using var lease=assets.Acquire(geometry.AssetId);using var files=new KernelFiles();
        var path=files.PathFor("input.brep");File.WriteAllBytes(path,lease.Content.ToArray());
        return ShapeExchange.ReadBrep(path);
    }
    public static XdeDocument ReadContext(AssetId id,IAssetStore assets)
    {
        using var lease=assets.Acquire(id);using var files=new KernelFiles();
        var path=files.PathFor("context.xbf");File.WriteAllBytes(path,lease.Content.ToArray());
        return XdeDocument.Open(path);
    }
    public static GeometryResult StoreShape(Shape shape,IAssetStore assets)
    {
        var summary=shape.GetTopologySummary();var counts=summary.UniqueCounts;
        bool empty=counts.VertexCount==0&&counts.EdgeCount==0&&counts.FaceCount==0;
        if(!empty&&!shape.IsValid)throw new CadValidationException("OCCT produced invalid topology.");
        var kind=empty?BodyKind.Empty:counts.SolidCount==1?BodyKind.Solid:counts.SolidCount>1?BodyKind.Compound:counts.FaceCount>0?BodyKind.Sheet:BodyKind.Wire;
        var box=empty?new Bounds3d(Vector3d.Zero,Vector3d.Zero):Bounds(shape.GetBoundingBox());
        double volume=counts.SolidCount>0?Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass):0;
        using var files=new KernelFiles();var path=files.PathFor("result.brep");ShapeExchange.WriteBrep(shape,path);
        var lease=assets.Stage(File.ReadAllBytes(path));
        try
        {
            var reference=new GeometryAssetRef(lease.Id,GeometryRevisionId.New(),kind,box,volume);reference.Validate();
            return new(reference,lease);
        }
        catch{lease.Dispose();throw;}
    }
    public static GpTrsf ToNative(RigidTransform3d transform)
    {
        transform.Validate();var q=transform.Rotation;double angle=2*Math.Acos(Math.Clamp(q.W,-1,1));
        var axis=new Vector3d(q.X,q.Y,q.Z);axis=axis.Length<1e-12?Vector3d.UnitZ:axis.Normalized();
        return GpTrsf.Create(transform.Translation.X,transform.Translation.Y,transform.Translation.Z,axis.X,axis.Y,axis.Z,angle);
    }
    public static RigidTransform3d FromNative(GpTrsf t)
    {
        var x=new Vector3d(t.Value(1,1),t.Value(2,1),t.Value(3,1));
        var y=new Vector3d(t.Value(1,2),t.Value(2,2),t.Value(3,2));
        var z=new Vector3d(t.Value(1,3),t.Value(2,3),t.Value(3,3));
        if(Math.Abs(x.Length-1)>1e-7||Math.Abs(y.Length-1)>1e-7||Math.Abs(z.Length-1)>1e-7||
            Math.Abs(x.Dot(y))>1e-7||Math.Abs(x.Cross(y).Dot(z)-1)>1e-7)
            throw new NotSupportedException("Scaled or mirrored assembly placements must be baked before import.");
        double a=x.X,b=y.X,c=z.X,d=x.Y,e=y.Y,f=z.Y,g=x.Z,h=y.Z,i=z.Z;
        Quaterniond q;
        double trace=a+e+i;
        if(trace>0){var s=Math.Sqrt(trace+1)*2;q=new((h-f)/s,(c-g)/s,(d-b)/s,s/4);}
        else if(a>e&&a>i){var s=Math.Sqrt(1+a-e-i)*2;q=new(s/4,(b+d)/s,(c+g)/s,(h-f)/s);}
        else if(e>i){var s=Math.Sqrt(1+e-a-i)*2;q=new((b+d)/s,s/4,(f+h)/s,(c-g)/s);}
        else{var s=Math.Sqrt(1+i-a-e)*2;q=new((c+g)/s,(f+h)/s,s/4,(d-b)/s);}
        var result=new RigidTransform3d(new(t.Value(1,4),t.Value(2,4),t.Value(3,4)),q);result.Validate();return result;
    }
    public static uint ToArgb(XdeColor color)
    {
        static uint Srgb(double v)=>(uint)Math.Round(255*(v<=0.0031308?v*12.92:1.055*Math.Pow(v,1/2.4)-0.055));
        return ((uint)Math.Round(color.Alpha*255)<<24)|(Srgb(color.Red)<<16)|(Srgb(color.Green)<<8)|Srgb(color.Blue);
    }
    public static XdeColor ToXdeColor(uint argb)
    {
        static double Linear(uint v){double n=v/255.0;return n<=0.04045?n/12.92:Math.Pow((n+0.055)/1.055,2.4);}
        return new(Linear((argb>>16)&255),Linear((argb>>8)&255),Linear(argb&255),(argb>>24)/255.0);
    }
    private static Bounds3d Bounds(BoundingBox3d b)=>new(new(b.Minimum.X,b.Minimum.Y,b.Minimum.Z),new(b.Maximum.X,b.Maximum.Y,b.Maximum.Z));
}
internal sealed class KernelFiles : IDisposable
{
    private readonly string directory;
    public KernelFiles(){directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);}
    public string PathFor(string fileName)=>System.IO.Path.Combine(directory,fileName);
    public void Dispose()
    {
        var boundary=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Cadoryx"))+System.IO.Path.DirectorySeparatorChar;
        if(!System.IO.Path.GetFullPath(directory).StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid temporary directory.");
        try{Directory.Delete(directory,true);}catch(IOException){/* Antivirus may briefly hold a file; startup cleanup may reclaim it. */}
    }
}
