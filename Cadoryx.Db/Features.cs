using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cadoryx.Db;

public enum BooleanOperation { Fuse=0, Cut=1, Common=2 }
[JsonPolymorphic(TypeDiscriminatorPropertyName="type")]
[JsonDerivedType(typeof(BoxRecipe),"box")]
[JsonDerivedType(typeof(CylinderRecipe),"cylinder")]
[JsonDerivedType(typeof(BooleanRecipe),"boolean")]
[JsonDerivedType(typeof(ImportedRecipe),"import")]
[JsonDerivedType(typeof(TransformRecipe),"transform")]
[JsonDerivedType(typeof(ExtrudeRecipe),"extrude")]
[JsonDerivedType(typeof(RevolveRecipe),"revolve")]
[JsonDerivedType(typeof(LocalFeatureRecipe),"local-box-edge")]
public abstract record GeometryRecipe
{
    public abstract void Validate();
    [JsonIgnore] public virtual IEnumerable<GeometryAssetRef> AssetInputs => [];
}
public sealed record BoxRecipe(double X,double Y,double Z,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate() { CadGuard.Positive(X,Y,Z); Placement.Validate(); }
}
public sealed record CylinderRecipe(double Radius,double Height,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate() { CadGuard.Positive(Radius,Height); Placement.Validate(); }
}
public sealed record ImportedRecipe(GeometryAssetRef Source) : GeometryRecipe
{
    public override void Validate() => Source.Validate();
    public override IEnumerable<GeometryAssetRef> AssetInputs => [Source];
}
public sealed record TransformRecipe(GeometryAssetRef Source,RigidTransform3d Transform) : GeometryRecipe
{
    public override void Validate() { Source.Validate(); Transform.Validate(); }
    public override IEnumerable<GeometryAssetRef> AssetInputs => [Source];
}
public sealed record BooleanRecipe(BooleanOperation Operation,ImmutableArray<GeometryAssetRef> Inputs) : GeometryRecipe
{
    public override void Validate() { if(!Enum.IsDefined(Operation) || Inputs.IsDefault || Inputs.Length<2) throw new CadValidationException("Boolean requires two or more inputs."); foreach(var i in Inputs) i.Validate(); }
    public override IEnumerable<GeometryAssetRef> AssetInputs => Inputs;
}
public readonly record struct Point2d(double X,double Y);
public sealed record SketchProfile(ImmutableArray<Point2d> Points)
{
    public void Validate()
    {
        if(Points.IsDefault || Points.Length<3) throw new CadValidationException("A profile requires at least three vertices.");
        foreach(var p in Points) CadGuard.Finite(p.X,p.Y);
        double area=0;
        for(int i=0;i<Points.Length;i++)
        {
            var a=Points[i]; var b=Points[(i+1)%Points.Length];
            if(Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y)<1e-10) throw new CadValidationException("Profile contains a zero-length edge.");
            area+=a.X*b.Y-b.X*a.Y;
            for(int j=i+2;j<Points.Length;j++)
            {
                if(i==0 && j==Points.Length-1) continue;
                if(Intersects(a,b,Points[j],Points[(j+1)%Points.Length])) throw new CadValidationException("Profile self-intersects or touches itself.");
            }
        }
        if(Math.Abs(area)<1e-10) throw new CadValidationException("Profile area is zero.");
    }
    private static bool Intersects(Point2d a,Point2d b,Point2d c,Point2d d)
    {
        static double Cross(Point2d p,Point2d q,Point2d r)=>(q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        static bool On(Point2d p,Point2d q,Point2d r)=>r.X>=Math.Min(p.X,q.X)-1e-10&&r.X<=Math.Max(p.X,q.X)+1e-10&&r.Y>=Math.Min(p.Y,q.Y)-1e-10&&r.Y<=Math.Max(p.Y,q.Y)+1e-10;
        var x=Cross(a,b,c);var y=Cross(a,b,d);var z=Cross(c,d,a);var w=Cross(c,d,b);
        return (x*y<0&&z*w<0)||(Math.Abs(x)<1e-10&&On(a,b,c))||(Math.Abs(y)<1e-10&&On(a,b,d))||(Math.Abs(z)<1e-10&&On(c,d,a))||(Math.Abs(w)<1e-10&&On(c,d,b));
    }
}
public sealed record ExtrudeRecipe(SketchProfile Profile,double Distance,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate(){Profile.Validate();CadGuard.Positive(Distance);Placement.Validate();}
}
public sealed record RevolveRecipe(SketchProfile Profile,double AngleRadians,RigidTransform3d Placement) : GeometryRecipe
{
    public override void Validate(){Profile.Validate();CadGuard.Positive(AngleRadians);if(AngleRadians>Math.PI*2+1e-10)throw new CadValidationException("Revolution exceeds a full turn.");Placement.Validate();}
}
public sealed record FeatureDefinition(FeatureId Id,DefinitionId PartId,string Name,GeometryRecipe Recipe,
    ImmutableArray<FeatureId> Inputs,BodyId OutputBodyId,GeometryAssetRef Result,int SchemaVersion=1,BodyOutputMetadata? OutputMetadata=null)
{
    public SketchProfileReference? SketchSource {get;init;}
    public TopologyHistory? TopologyHistory {get;init;}
}
/// <summary>Retains the output's authored attributes when recompute temporarily produces no body.</summary>
public sealed record BodyOutputMetadata(string Name,LayerId Layer,CadAppearance Appearance,bool Visible,MaterialId? Material)
{
    public static BodyOutputMetadata FromBody(CadBody body)=>new(body.Name,body.LayerId,body.Appearance,body.IsVisible,body.MaterialId);
}
